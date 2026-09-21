using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using KitLugia.Core.TaskManager;

namespace KitLugia.GUI.Windows.TaskManager
{
    // ══════════════════════════════════════════════════════════════════════════
    //  Partial: DIÁRIO DE INTERVENÇÕES + CRUZAMENTO ÁUDIO x ARMAZENAMENTO
    //
    //  Duas exigências da revisão de precisão/segurança do Kit:
    //
    //  1. REVERSIBILIDADE VISÍVEL. Toda intervenção temporária que o Kit faz
    //     (suspender para testar, parar/reiniciar serviço) fica registrada aqui,
    //     com hora e situação. O usuário vê o que foi feito, consegue desfazer
    //     tudo num clique e nunca descobre depois que "alguma coisa" foi mexida.
    //     O diário é do CORE (StorageDiagnostics.Journal) — a tela só mostra.
    //     Só retomamos o que NÓS suspendemos: processo que o usuário já havia
    //     suspenso por conta própria não é tocado.
    //
    //  2. EVIDÊNCIA INDEPENDENTE. O estalo de áudio é o sintoma que o usuário
    //     sente antes de olhar número nenhum. Quando o áudio falha no mesmo
    //     instante em que o disco está saturado, isso é uma segunda medição
    //     (independente da contagem de E/S) apontando na mesma direção. É dito
    //     como indício — nunca como prova de causa.
    // ══════════════════════════════════════════════════════════════════════════
    public partial class KitTaskManagerWindow
    {
        // ══════════════ DIÁRIO ══════════════

        /// <summary>Redesenha o painel do diário e o rótulo de pendências.</summary>
        private void RenderInterventionJournal()
        {
            try
            {
                var entries = StorageDiagnostics.Journal;
                int pending = StorageDiagnostics.PendingReverts;

                if (entries.Count == 0)
                {
                    StJournalText.Text = "(nenhuma intervenção até agora — o Kit não suspendeu, não parou e não reiniciou nada)";
                    StJournalBadgeText.Text = "NADA FEITO";
                    StJournalBadgeText.Foreground = StIdle;
                    StJournalBadge.Background = StBrush(0x33, 0x33, 0x33);
                    BtnStUndoAll.IsEnabled = false;
                    return;
                }

                var sb = new StringBuilder();
                foreach (var e in entries.OrderByDescending(x => x.At))
                    sb.AppendLine(e.ToLine());
                StJournalText.Text = sb.ToString().TrimEnd();

                StJournalBadgeText.Text = pending > 0 ? $"{pending} A DESFAZER" : $"{entries.Count} REGISTRO(S)";
                StJournalBadgeText.Foreground = pending > 0 ? StWarn : StOk;
                StJournalBadge.Background = pending > 0 ? StBrush(0x3A, 0x30, 0x12) : StBrush(0x1B, 0x3A, 0x24);
                BtnStUndoAll.IsEnabled = pending > 0;
                BtnStUndoAll.Content = pending > 0 ? $"↩ Desfazer {pending} agora" : "↩ Nada a desfazer";
            }
            catch { }
        }

        private void BtnStUndoAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int resumed = StorageDiagnostics.ResumeAll();
                int services = StorageDiagnostics.RestoreServices();
                StorageDiagnostics.MarkAllReverted();

                TxtStatus.Text = (resumed > 0 || services > 0)
                    ? $"↩ Desfeito: {resumed} processo(s) retomado(s)" +
                      (services > 0 ? $", {services} serviço(s) restaurado(s)" : "") + "."
                    : "Nada pendente para desfazer.";
                RenderInterventionJournal();
                UpdateStorageActionButtons();
                StorageTick();
            }
            catch (Exception ex) { TxtStatus.Text = "Falha ao desfazer: " + ex.Message; }
        }

        private void BtnStJournalClear_Click(object sender, RoutedEventArgs e)
        {
            // Limpa SÓ a lista. Nada é retomado nem restaurado — isso fica no botão de desfazer.
            if (StorageDiagnostics.PendingReverts > 0 &&
                !Confirm("Limpar o diário", $"Existem {StorageDiagnostics.PendingReverts} intervenção(ões) que ainda não voltaram ao normal.\n\n" +
                                            "Limpar o diário NÃO desfaz nada: as ações continuam valendo. Deseja limpar só a lista?"))
                return;
            StorageDiagnostics.ClearJournal();
            RenderInterventionJournal();
            TxtStatus.Text = "🧾 Diário limpo (nenhuma ação foi desfeita).";
        }

        // ══════════════ ÁUDIO x ARMAZENAMENTO ══════════════

        private void BtnStAudio_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var audio = AudioGlitchMonitor.Instance;
                if (audio.IsRunning) audio.Stop();
                else audio.Start();
                RenderStorageAudioCard();
                TxtStatus.Text = audio.IsRunning
                    ? $"🎧 Escutando o áudio ({audio.StatusText}). Deixe tocando enquanto o problema acontece — estalos serão cruzados com o disco."
                    : "🎧 Escuta de áudio desligada.";
            }
            catch (Exception ex) { TxtStatus.Text = "Falha ao alternar a escuta de áudio: " + ex.Message; }
        }

        /// <summary>
        /// Painel curto do áudio dentro da aba Disco. O detalhe completo (quem tocava, qual
        /// driver, hipótese) fica na aba Latência e travamentos — aqui é só o cruzamento.
        /// </summary>
        private void RenderStorageAudioCard()
        {
            try
            {
                var audio = AudioGlitchMonitor.Instance;
                BtnStAudio.Content = audio.IsRunning ? "🎧 Parar escuta de áudio" : "🎧 Escutar o áudio";
                bool playing = audio.AudioActive;

                if (!audio.IsRunning)
                {
                    StAudioBadgeText.Text = "DESLIGADO";
                    StAudioBadgeText.Foreground = StIdle;
                    StAudioBadge.Background = StBrush(0x33, 0x33, 0x33);
                    StAudioText.Text = "A escuta de áudio está desligada. Ligando, o Kit registra cada estalo do som " +
                                       "(pela API oficial de detecção de glitch do Windows) e cruza com o estado do disco " +
                                       "no mesmo instante.";
                    return;
                }

                int confirmed = audio.AudibleConfirmedCount;
                int total = audio.GlitchCount;
                if (total == 0)
                {
                    StAudioBadgeText.Text = playing ? "ESCUTANDO (SOM TOCANDO)" : "ESCUTANDO";
                    StAudioBadgeText.Foreground = StOk;
                    StAudioBadge.Background = StBrush(0x1B, 0x3A, 0x24);
                    StAudioText.Text = $"Escutando em \"{audio.DeviceName}\" — nenhuma travada de áudio até agora. " +
                                       (playing
                                           ? "Há som tocando: deixe rodar enquanto o problema acontece."
                                           : "Nada tocando agora: o Kit só marca estalo audível quando há som de verdade.");
                    return;
                }

                var worst = audio.Glitches.Where(g => g.Kind == "CONFIRMADO")
                                          .OrderByDescending(g => Math.Max(g.LostMs, g.InterruptionMs))
                                          .FirstOrDefault();

                StAudioBadgeText.Text = confirmed > 0 ? $"{confirmed} CONFIRMADA(S)" : $"{total} INDÍCIO(S)";
                StAudioBadgeText.Foreground = confirmed > 0 ? StWarn : StIdle;
                StAudioBadge.Background = confirmed > 0 ? StBrush(0x3A, 0x30, 0x12) : StBrush(0x33, 0x33, 0x33);

                var sb = new StringBuilder();
                if (worst != null)
                {
                    bool diskBusy = worst.DiskActivityPct >= 80 || worst.DiskQueue >= 3 || worst.DiskLatencyMs >= 20;
                    sb.AppendLine($"Pior travada: {worst.At:HH:mm:ss} — " +
                                  (worst.LostMs >= 1 ? $"{worst.LostMs:F0} ms de áudio perdidos"
                                                     : $"motor de áudio parado por {worst.InterruptionMs:F0} ms") + ".");
                    if (worst.AudioApps.Length > 0) sb.AppendLine($"Tocando na hora: {worst.AudioApps}.");
                    sb.AppendLine(diskBusy
                        ? $"No mesmo instante o disco {worst.DiskLabel} estava saturado ({worst.DiskActivityPct:F0}% de atividade, " +
                          $"fila {worst.DiskQueue:F1}, latência {worst.DiskLatencyMs:F1} ms). Isso reforça o diagnóstico do disco " +
                          "por uma segunda medição — mas ainda é indício, não prova."
                        : "Os discos estavam calmos nesse instante: o travamento de áudio provavelmente NÃO foi causado pelo " +
                          "armazenamento (veja drivers/DPC na aba Latência).");
                    if (worst.TopIo.Count > 0)
                        sb.AppendLine("Maior E/S no instante: " + string.Join(", ", worst.TopIo.Select(t => $"{t.Name} ({StorageDiagnostics.FormatBps(t.Bps)})")) + ".");
                }
                else
                {
                    sb.AppendLine($"{total} descontinuidade(s) no stream de áudio, nenhuma confirmada pelo Windows. " +
                                  "São indícios (início/troca de stream ou leitura atrasada) — não dá para afirmar que você ouviu.");
                }
                StAudioText.Text = sb.ToString().TrimEnd();
            }
            catch { }
        }

        /// <summary>
        /// Correlação antes/depois com o áudio num teste de impacto: se os estalos aconteciam no
        /// estado normal e PARARAM durante a suspensão do processo, isso é uma segunda evidência
        /// que acompanha a queda do disco — do jeito que o usuário percebe.
        /// </summary>
        private string BuildAudioImpactCrossCheck(DateTime testStartedAt, int baselineSec, int testSec)
        {
            try
            {
                var audio = AudioGlitchMonitor.Instance;
                if (!audio.IsRunning) return "";

                var boundary = testStartedAt.AddSeconds(baselineSec);
                var windowEnd = boundary.AddSeconds(testSec).AddSeconds(2);
                var inWindow = audio.Glitches.Where(g => g.At >= testStartedAt && g.At <= windowEnd).ToList();
                if (inWindow.Count == 0) return "";

                int duringBaseline = inWindow.Count(g => g.At < boundary && g.Kind == "CONFIRMADO");
                int duringSuspension = inWindow.Count(g => g.At >= boundary && g.Kind == "CONFIRMADO");

                if (duringBaseline > 0 && duringSuspension == 0)
                    return $"\n\n🎧 Áudio: {duringBaseline} travada(s) audível(is) durante a medição normal e NENHUMA enquanto o " +
                           "processo estava suspenso. O alívio que você sentiu no ouvido acompanhou o do disco.";
                if (duringBaseline == 0 && duringSuspension > 0)
                    return "\n\n🎧 Áudio: apareceram travadas de áudio DURANTE a suspensão. Antes já não havia — " +
                           "isso enfraquece a hipótese de que este processo seja o responsável pelo que você sente.";
                if (duringBaseline > 0 && duringSuspension > 0)
                    return $"\n\n🎧 Áudio: {duringBaseline} travada(s) antes e {duringSuspension} durante a suspensão — " +
                           "o áudio continuou falhando, então existe outra causa além deste processo.";
                return "";
            }
            catch { return ""; }
        }
    }
}
