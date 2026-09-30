using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using KitLugia.Core.Diagnostics;
using KitLugia.Core.TaskManager;
using Clipboard = System.Windows.Clipboard;

namespace KitLugia.GUI.Windows.TaskManager
{
    // ══════════════════════════════════════════════════════════════════════════
    //  Partial: ESCUTA DE ÁUDIO (estalos / travadas de som)
    //
    //  O Kit abre uma captura em LOOPBACK do dispositivo de saída padrão e pergunta
    //  ao motor de áudio do Windows, pacote por pacote, se ele precisou pular
    //  quadros (AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY — a API oficial de detecção
    //  de glitch desde o Windows 7). Nada é gravado em disco: só leitura do buffer.
    //
    //  Cada estalo entra na MESMA tabela de Eventos desta aba, junto com as janelas
    //  travadas e os picos de DPC — é isso que permite ver "o áudio estalou no
    //  mesmo instante em que o SearchIndexer saturou o disco".
    //
    //  HONESTIDADE: o Kit só chama de "confirmado" quando quem avisa é o Windows.
    //  Arranque de stream e leitura atrasada do próprio Kit entram como INDÍCIO,
    //  e o texto diz isso. As causas apontadas (disco/DPC/RAM/CPU) são hipóteses
    //  medidas no mesmo instante, não prova de causalidade.
    // ══════════════════════════════════════════════════════════════════════════
    public partial class KitTaskManagerWindow
    {
        private bool _audioBuilt;
        private const int RecoverySuspendMsForUi = 300; // espelha AudioGlitchMonitor.RecoverySuspendMs (evita depender da instância num static)

        /// <summary>
        /// O usuário desligou a escuta DE PROPÓSITO. O auto-start das abas de diagnóstico
        /// respeita isso — só religa se ele mandar (ou ao reabrir a janela).
        /// </summary>
        private bool _audioUserStopped;

        /// <summary>
        /// Liga a escuta sozinho ao entrar numa aba de diagnóstico. O pedido era "o Kit
        /// identificar os stutters de áudio" — depender de o usuário achar um botão faria
        /// o recurso simplesmente não existir para quem não o conhece. O custo é uma leitura
        /// de buffer em loopback a cada 10 ms, sem disco e sem CPU relevante; e o próprio
        /// monitor se defende de interferir na medição (ver SelfStarvation no Core).
        /// </summary>
        private void AutoStartAudioListen()
        {
            try
            {
                if (_audioUserStopped) return;
                EnsureAudioBuilt();
                var mon = AudioGlitchMonitor.Instance;
                if (mon.IsRunning) return;
                mon.Start();
                RenderAudioCard();
            }
            catch { }
        }

        /// <summary>Garante os ganchos do monitor de áudio (idempotente).</summary>
        private void EnsureAudioBuilt()
        {
            if (_audioBuilt) return;
            _audioBuilt = true;

            var mon = AudioGlitchMonitor.Instance;

            // O Core não conhece o snapshot de processos do TMOG: a GUI injeta o "quem".
            mon.IoSnapshotProvider = () =>
            {
                try
                {
                    lock (_lock)
                        return _allRows
                            .Where(r => r.DiskReadBytesPerSec + r.DiskWriteBytesPerSec > 0)
                            .OrderByDescending(r => r.DiskReadBytesPerSec + r.DiskWriteBytesPerSec)
                            .Take(8)
                            .Select(r => new AudioGlitchMonitor.ProcIoRef(
                                r.Pid, r.Name,
                                r.DiskReadBytesPerSec + r.DiskWriteBytesPerSec,
                                r.DiskOpsPerSec))
                            .ToList();
                }
                catch { return new List<AudioGlitchMonitor.ProcIoRef>(); }
            };

            mon.GlitchDetected += OnAudioGlitchDetected;
            mon.RecoveryPerformed += OnRecoveryPerformed;
        }

        private void OnRecoveryPerformed(string text)
        {
            try
            {
                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (_latBuilt)
                        {
                            LatAudioRecover.Text = text;
                            RefreshLatencyUi();
                        }
                        if (_stBuilt) RenderStorageAudioCard();
                    }
                    catch { }
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { }
        }

        /// <summary>Toggle da recuperação automática (checkbox do card de áudio).</summary>
        private void ChkLatAutoRecover_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ChkLatAutoRecover.IsChecked == true)
                {
                    EnsureAudioBuilt();
                    // Recuperar o áudio só faz sentido com a escuta ligada (sem medição não há gatilho).
                    if (!AudioGlitchMonitor.Instance.IsRunning && !_audioUserStopped) AudioGlitchMonitor.Instance.Start();
                    AudioGlitchMonitor.Instance.AutoRecover = true;
                    PersistAudioAntiStutterPreference(true);
                    TxtStatus.Text = "🩹 Recuperação de áudio ligada (salva no Kit → Configurações → Áudio): se os estalos confirmados se repetirem (2+ em 90s), o Kit sincroniza o motor de áudio sozinho.";
                }
                else
                {
                    AudioGlitchMonitor.Instance.AutoRecover = false;
                    PersistAudioAntiStutterPreference(false);
                    TxtStatus.Text = "🩹 Recuperação de áudio desligada — o Kit só observa, não toca no motor.";
                }
                if (_latBuilt) RenderAudioCard();
            }
            catch { }
        }

        /// <summary>
        /// Espelha a escolha do checkbox na preferência do Kit (engrenagem → Áudio).
        /// O anti-stutter de áudio vale entre sessões: sem isto, o usuário teria de marcar
        /// a MESMA opção em dois lugares, e a do Gerenciador de Tarefas morreria ao fechar
        /// a janela (o estado do monitor não sobrevive ao processo).
        /// </summary>
        private void PersistAudioAntiStutterPreference(bool enabled)
        {
            try
            {
                if (System.Windows.Application.Current?.MainWindow is KitLugia.GUI.MainWindow mw)
                    mw.TrayService?.PersistAudioAntiStutter(enabled);
            }
            catch { }
        }

        /// <summary>Estalo detectado (thread do monitor) → entra na tabela de Eventos da aba.</summary>
        private void OnAudioGlitchDetected(AudioGlitchMonitor.AudioGlitchEvent ev)
        {
            try
            {
                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        AddAudioEventRow(ev);
                        if (_latBuilt) RenderAudioCard();
                    }
                    catch { }
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { }
        }

        private void AddAudioEventRow(AudioGlitchMonitor.AudioGlitchEvent ev)
        {
            string key = "AUDIO|" + ev.At.Ticks + "|" + ev.Kind;
            if (!_latSeenEvents.Add(key)) return;

            // Evento criado pela RECUPERAÇÃO do próprio Kit: entra na tabela com rótulo
            // de recuperação, nunca como "Grave" nem como indício — era o que fazia o
            // usuário achar que o áudio ainda estava quebrado logo após o conserto.
            if (ev.ProvokedByKit)
            {
                var rr = new LatEventRow
                {
                    Hora = ev.At.ToString("HH:mm:ss"),
                    Tipo = "Áudio",
                    Gravidade = "Recuperação",
                    GravidadeCor = LatOk,
                    Titulo = "Kit sincronizou o motor de áudio (reset programático)",
                    Resumo = "Evento esperado do próprio reset — NÃO é estalo novo nem problema. " +
                             "O som deve voltar normal; se os estalos continuarem, a causa é outra.",
                    Tooltip = "🩹 RECUPERAÇÃO DO KIT — " + ev.At.ToString("dd/MM/yyyy HH:mm:ss.fff") + "\n\n" +
                              "O Kit congelou o motor de áudio por ~" + RecoverySuspendMsForUi + " ms e retomou " +
                              "(o desligar/ligar que resolve, feito por você). Esta entrada é o evento ESPERADO do " +
                              "reset, marcada como PROVOCADO — não entra nas estatísticas de estalos reais nem nos " +
                              "gatilhos de nova recuperação.",
                };
                _latEventRows.Insert(0, rr);
                while (_latEventRows.Count > 200) _latEventRows.RemoveAt(_latEventRows.Count - 1);
                if (_latView == "Events" && _latEventRows.Count > 0) DgLatEvents.ScrollIntoView(rr);
                return;
            }

            bool confirmedAudio = ev.Kind == "CONFIRMADO";
            string dur = ev.LostMs >= 1
                ? ev.LostMs.ToString("F0") + " ms de áudio perdidos"
                : ev.InterruptionMs >= 20
                    ? "motor de áudio parado por " + ev.InterruptionMs.ToString("F0") + " ms"
                    : "descontinuidade sem duração medível";

            string titulo = confirmedAudio
                ? "Áudio estalou: " + dur + (ev.Audible ? " (havia som tocando)" : " (em silêncio)")
                : "Possível estalo (indício): " + dur;

            // O "resumo" é a linha que aparece na grade — curta e em português.
            string resumo;
            if (ev.DiskActivityPct >= 80 || ev.DiskQueue >= 3 || ev.DiskLatencyMs >= 20)
                resumo = "No mesmo instante o disco " + ev.DiskLabel + " estava saturado (" +
                         ev.DiskActivityPct.ToString("F0") + "% , fila " + ev.DiskQueue.ToString("F1") +
                         (ev.TopIo.Count > 0 ? ", maior E/S: " + ev.TopIo[0].Name : "") + ").";
            else if (ev.DpcPercent >= 8)
                resumo = "Discos calmos: os drivers consumiam " + ev.DpcPercent.ToString("F1") +
                         "% de um núcleo em DPC" + (ev.TopDriver.Length > 0 ? " (maior: " + ev.TopDriver + ")" : "") + ".";
            else if (ev.HardFaultsPerSec >= 300)
                resumo = "Discos calmos, mas a paginação estava pesada (" + ev.HardFaultsPerSec.ToString("F0") +
                         "/s) — faltou RAM e o Windows foi buscar dados no disco.";
            else if (ev.CpuPct >= 85)
                resumo = "Discos calmos: a CPU estava em " + ev.CpuPct.ToString("F0") + "%.";
            else
                resumo = "Nenhum fator medido explica: disco, CPU, DPC e paginação estavam calmos.";

            var row = new LatEventRow
            {
                Hora = ev.At.ToString("HH:mm:ss"),
                Tipo = "Áudio",
                Gravidade = confirmedAudio ? (ev.LostMs >= 100 || ev.InterruptionMs >= 100 ? "Grave" : "Atenção") : "Indício",
                GravidadeCor = confirmedAudio
                    ? (ev.LostMs >= 100 || ev.InterruptionMs >= 100 ? LatBad : LatWarn)
                    : LatIdle,
                Titulo = titulo,
                Resumo = resumo,
                Tooltip = BuildAudioTooltip(ev),
            };

            _latEventRows.Insert(0, row);
            while (_latEventRows.Count > 200) _latEventRows.RemoveAt(_latEventRows.Count - 1);
            if (_latView == "Events" && _latEventRows.Count > 0) DgLatEvents.ScrollIntoView(row);
        }

        private static string BuildAudioTooltip(AudioGlitchMonitor.AudioGlitchEvent ev)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("🎧 ESTALO DE ÁUDIO — " + ev.At.ToString("dd/MM/yyyy HH:mm:ss.fff"));
            sb.AppendLine("Classificação: " + ev.Kind + "   (quem tocava: " +
                          (ev.AudioApps.Length > 0 ? ev.AudioApps : "não foi possível identificar") + ")");
            sb.AppendLine();
            sb.AppendLine("FATO MEDIDO");
            sb.AppendLine("  " + ev.Titulo);
            if (ev.LostFrames > 0) sb.AppendLine("  Quadros que faltaram no stream: " + ev.LostFrames);
            sb.AppendLine("  Confiança: " + ev.KindDetail);
            sb.AppendLine("  Dispositivo: " + (ev.DeviceName.Length > 0 ? ev.DeviceName : "(saída padrão)"));
            if (ev.AudioApps.Length > 0) sb.AppendLine("  Tocando na hora: " + ev.AudioApps);
            sb.AppendLine();
            sb.AppendLine("O SISTEMA NO MESMO INSTANTE");
            if (ev.DiskLabel.Length > 0)
                sb.AppendLine("  Disco " + ev.DiskLabel + ": " + ev.DiskActivityPct.ToString("F0") + "% de atividade, fila " +
                              ev.DiskQueue.ToString("F1") + ", latência " + ev.DiskLatencyMs.ToString("F1") + " ms" +
                              (ev.DiskMedia.Length > 0 ? " (" + ev.DiskMedia + ")" : ""));
            sb.AppendLine("  CPU: " + ev.CpuPct.ToString("F0") + "%   DPC do núcleo mais ocupado: " + ev.DpcPercent.ToString("F1") + "%");
            if (ev.TopDriver.Length > 0) sb.AppendLine("  Maior DPC: " + ev.TopDriver);
            sb.AppendLine("  Paginação: " + ev.HardFaultsPerSec.ToString("F0") + " falhas de disco/s" +
                          (ev.RamText.Length > 0 ? "   MEMÓRIA: " + ev.RamText : ""));
            if (ev.TopIo.Count > 0)
                sb.AppendLine("  Maior E/S: " + string.Join(", ",
                    ev.TopIo.Select(t => t.Name + " (" + StorageDiagnostics.FormatBps(t.Bps) + ")")));
            sb.AppendLine();
            sb.AppendLine("LEITURA DO KIT (hipótese, não veredito)");
            sb.AppendLine("  " + ev.Hipotese);
            sb.AppendLine();
            sb.AppendLine("O QUE FAZER");
            sb.AppendLine("  • Se o padrão é disco saturado: aba Disco → selecione o maior responsável → 'Testar impacto'.");
            sb.AppendLine("  • Se o padrão é DPC de driver: tabela Drivers, atualize o driver do maior responsável.");
            sb.AppendLine("  • Se nada aparece aqui: a causa está na cadeia de áudio (driver do dispositivo, USB, modo exclusivo).");
            return sb.ToString();
        }

        // ══════════════ AÇÕES ══════════════

        private void BtnLatAudio_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                EnsureAudioBuilt();
                var mon = AudioGlitchMonitor.Instance;
                if (mon.IsRunning) { _audioUserStopped = true; mon.Stop(); }
                else { _audioUserStopped = false; mon.Start(); }

                RenderAudioCard();
                TxtStatus.Text = mon.IsRunning
                    ? "🎧 Escuta de áudio ligada — " + mon.StatusText
                    : "🎧 Escuta de áudio desligada.";
            }
            catch (Exception ex) { TxtStatus.Text = "Falha ao alternar a escuta de áudio: " + ex.Message; }
        }

        private void BtnLatAudioClear_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AudioGlitchMonitor.Instance.Clear();
                for (int i = _latEventRows.Count - 1; i >= 0; i--)
                    if (_latEventRows[i].Tipo == "Áudio") _latEventRows.RemoveAt(i);
                RenderAudioCard();
                TxtStatus.Text = "🧹 Lista de estalos de áudio zerada (a escuta continua).";
            }
            catch { }
        }

        private void BtnLatAudioCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string rep = AudioGlitchMonitor.Instance.BuildAiReport();
                Clipboard.SetText(rep);
                TxtStatus.Text = $"📋 Relatório de áudio copiado ({rep.Length:N0} caracteres) — cole numa IA para análise profunda.";
            }
            catch (Exception ex) { TxtStatus.Text = "Erro ao copiar o relatório de áudio: " + ex.Message; }
        }

        // ══════════════ PAINEL ══════════════

        private void RenderAudioCard()
        {
            try
            {
                EnsureAudioBuilt();
                var mon = AudioGlitchMonitor.Instance;
                BtnLatAudio.Content = mon.IsRunning ? "🎧 PARAR ESCUTA" : "🎧 INICIAR ESCUTA";

                if (!mon.IsRunning)
                {
                    LatAudioCard.BorderBrush = LatOk;
                    LatAudioBadgeText.Text = mon.GlitchCount > 0 ? "PARADO (COM HISTÓRICO)" : "DESLIGADO";
                    LatAudioBadgeText.Foreground = LatIdle;
                    LatAudioBadge.Background = LatBrush(0x33, 0x33, 0x33);
                    LatAudioState.Text = mon.GlitchCount > 0
                        ? $"Escuta parada, mas a lista tem {mon.GlitchCount} evento(s) — você ainda pode copiar o relatório."
                        : "Ligue a escuta do áudio. O Kit pergunta ao motor de áudio do Windows quando ele perdeu quadros " +
                          "e cruza cada estalo com disco, drivers, CPU e memória do exato instante.";
                    LatAudioStats.Text = "";
                    LatAudioLast.Text = "";
                    return;
                }

                int confirmed = mon.AudibleConfirmedCount;
                int total = mon.GlitchCount;

                if (total == 0)
                {
                    LatAudioCard.BorderBrush = LatOk;
                    LatAudioBadgeText.Text = mon.AudioActive ? "ESCUTANDO — SOM TOCANDO" : "ESCUTANDO";
                    LatAudioBadgeText.Foreground = LatOk;
                    LatAudioBadge.Background = LatBrush(0x1B, 0x3A, 0x24);
                    LatAudioState.Text = "Nenhuma travada de áudio até agora. " +
                        (mon.AudioActive
                            ? "Há som tocando: deixe rodando enquanto o problema acontece."
                            : "Nada tocando agora — o Kit só marca estalo audível quando há som de verdade.");
                }
                else
                {
                    bool bad = confirmed >= 3;
                    LatAudioCard.BorderBrush = bad ? LatBad : confirmed > 0 ? LatWarn : LatIdle;
                    LatAudioBadgeText.Text = confirmed > 0 ? confirmed + " CONFIRMADA(S)" : total + " INDÍCIO(S)";
                    LatAudioBadgeText.Foreground = confirmed > 0 ? (bad ? LatBad : LatWarn) : LatIdle;
                    LatAudioBadge.Background = confirmed > 0 ? LatBrush(0x3A, 0x30, 0x12) : LatBrush(0x33, 0x33, 0x33);

                    var snap = mon.BuildConclusion();
                    LatAudioState.Text = snap.Title + "\n" + snap.Text.Split('\n')
                        .Where(l => l.Trim().Length > 0).Take(4)
                        .Aggregate("", (acc, l) => acc + l.Trim() + "\n").TrimEnd();
                }

                // Pior caso com duração medida (quadros perdidos ou motor parado).
                // Prioriza os CONFIRMADOS pelo Windows: dar o título de "pior caso" a um
                // indício que o próprio Kit pode ter causado (SelfStarvation) seria acusar
                // a cadeia de áudio de um problema nosso.
                var confirmedList = mon.Glitches.Where(g => g.Kind == "CONFIRMADO" && !g.ProvokedByKit).ToList();
                bool useConfirmed = confirmedList.Count > 0;
                var natural = mon.Glitches.Where(g => !g.ProvokedByKit).ToList();
                var worst = (useConfirmed ? confirmedList : natural)
                    .OrderByDescending(g => Math.Max(g.LostMs, g.InterruptionMs)).FirstOrDefault();
                if (worst != null)
                {
                    string dur = worst.LostMs >= 1
                        ? worst.LostMs.ToString("F0") + " ms de áudio perdidos"
                        : worst.InterruptionMs >= 20
                            ? "motor de áudio parado por " + worst.InterruptionMs.ToString("F0") + " ms"
                            : "duração não medível";
                    string quem = worst.AudioApps.Length > 0 ? " | tocava: " + worst.AudioApps : "";
                    LatAudioLast.Text = useConfirmed
                        ? $"Pior caso confirmado {worst.At:HH:mm:ss}: {dur}{quem}"
                        : $"Maior indício (sem confirmação do Windows) {worst.At:HH:mm:ss}: {dur}{quem}";
                }
                else LatAudioLast.Text = "";

                LatAudioStats.Text =
                    $"Pacotes lidos: {mon.PacketCount:N0} · leituras/s: {mon.PollsPerSec:F0} · " +
                    $"pior atraso do próprio Kit: {mon.OwnWorstGapMs:F0} ms · " +
                    $"arranques/trocas de stream: {mon.StreamRestartCount}" +
                    (mon.RecoveryCount > 0 ? $" · recuperações: {mon.RecoveryCount}" : "");

                // Estado da recuperação automática: o checkbox reflete o estado real do motor
                // e a linha mostra a última ação (ou por que nada foi feito).
                ChkLatAutoRecover.IsChecked = mon.AutoRecover;
                LatAudioRecover.Text = mon.LastRecoveryText;
            }
            catch { }
        }

        /// <summary>Texto do relatório combinado (latência + áudio) usado pelo botão de copiar da aba.</summary>
        private string BuildCombinedLatencyReport()
        {
            string lat = _lat.BuildAiReport();
            string audio = "";
            try
            {
                var mon = AudioGlitchMonitor.Instance;
                if (mon.GlitchCount > 0 || mon.IsRunning)
                    audio = Environment.NewLine + Environment.NewLine + mon.BuildAiReport();
            }
            catch { }

            // O PORQUÊ: a escuta do Kit prova que o áudio travou e mede quanto se perdeu,
            // mas não diz a causa interna. O canal GlitchDetection registra exatamente isso
            // (fim de glitch do endpoint, pacote faltando, sobreleitura do servidor de saída).
            // Vem junto no relatório copiado para quem for analisar (humano ou IA) ter o dado,
            // sem precisar abrir o Visualizador de Eventos.
            string causa = "";
            try
            {
                var read = AudioGlitchChannel.ReadRecent(TimeSpan.FromMinutes(30));
                causa = Environment.NewLine + Environment.NewLine +
                        "=== CAUSA REPORTADA PELO WINDOWS (" + AudioGlitchChannel.ChannelName + ") ===" +
                        Environment.NewLine + AudioGlitchChannel.BuildEvidenceBody(read);
            }
            catch { }

            return lat + audio + causa;
        }
    }
}
