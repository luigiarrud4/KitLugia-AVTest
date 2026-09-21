using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KitLugia.Core.TaskManager
{
    // ══════════════════════════════════════════════════════════════════════════
    //  STORAGE ASSESSMENT — as REGRAS de julgamento do armazenamento, num só lugar.
    //
    //  Por que isto existe separado do motor (StorageDiagnostics):
    //    1. O MESMO julgamento é usado pela tela, pelo relatório para IA e pelo
    //       cruzamento com os glitches de áudio. Regra duplicada = tela e relatório
    //       discordando, que é pior que não ter diagnóstico.
    //    2. São regras puras (entra uma medição, sai um texto) — dá para provar por
    //       asserção, sem depender de ter um disco em 100% na máquina de teste.
    //
    //  REGRAS QUE ESTE ARQUIVO GARANTE (revisão de precisão do Kit):
    //    • "100%" NUNCA, sozinho, significa problema. Atividade é tempo ocupado, não
    //      vazão: 100% com 18 MB/s é fila/latência; 100% com 7 GB/s pode ser trabalho
    //      pesado legítimo.
    //    • O LIMITE depende do meio: um HDD a 40 MB/s com latência de 20 ms está
    //      afogado (acesso mecânico), enquanto um NVMe nessa faixa está ocioso. Usar
    //      um único limite de MB/s para os dois seria mentira para um deles.
    //    • Atividade espalhada por vários processos é dita como espalhada: nenhum
    //      culpado individual é apontado.
    //    • Atividade concentrada no processo System é dita como sendo de baixo nível
    //      (cache/paginação/drivers) e o usuário é mandado para drivers, nunca para
    //      "matar o System".
    // ══════════════════════════════════════════════════════════════════════════
    public static class StorageAssessment
    {
        /// <summary>Gravidade para a tela (mesma escala do resto do Kit).</summary>
        public enum Level { Ok, Attention, Critical }

        public sealed class Verdict
        {
            public Level Level;
            public string Badge = "";      // OK | ATENCAO | CRITICO
            public string Kind = "";       // chave curta: Saudavel | DiscoOcupado | DispositivoLento | ...
            public string Title = "";
            public string Text = "";
            /// <summary>Quais limites foram usados (para o usuário/IA entender o porquê do veredito).</summary>
            public string ThresholdsUsed = "";
        }

        /// <summary>
        /// Perfil de exigência do dispositivo. HDD sofre com acesso aleatório (cabeça
        /// mecânica): latência de 15 ms já é "afogado". NVMe entrega milhares de MB/s
        /// com fila alta e continua saudável.
        /// </summary>
        public struct MediaProfile
        {
            public string Media;
            public double LatencyBadMs;      // a partir daqui a espera começa a doer
            public double LatencyCriticalMs; // a partir daqui trava de verdade
            public double QueueBad;          // fila que já indica afogamento
            public double IopsHeavy;         // quando o volume de operações é "trabalho pesado"
            public bool Mechanical;

            public string Describe() => Mechanical
                ? $"{Media}: latência >= {LatencyBadMs:F0} ms e fila >= {QueueBad:F0} já indicam afogamento (acesso mecânico)"
                : $"{Media}: só latência >= {LatencyBadMs:F0} ms ou fila >= {QueueBad:F0} indicam saturação (vazão alta aqui é normal)";
        }

        public static MediaProfile ProfileFor(string media, string model)
        {
            string m = (media ?? "").Trim().ToUpperInvariant();
            if (m.Length == 0 || m.StartsWith("DESCONHEC"))
            {
                // Sem meio confiável, usamos o palpite mais conservador possível pela
                // aparência do modelo — e dizemos isso nos limites usados.
                m = LooksLikeNvme(model) ? "NVME" : "SSD";
            }

            if (m.StartsWith("HDD"))
                return new MediaProfile { Media = "HDD", LatencyBadMs = 15, LatencyCriticalMs = 30, QueueBad = 3, IopsHeavy = 300, Mechanical = true };
            if (m.StartsWith("NVME"))
                return new MediaProfile { Media = "NVMe", LatencyBadMs = 3, LatencyCriticalMs = 10, QueueBad = 16, IopsHeavy = 100_000, Mechanical = false };
            if (m.StartsWith("SCM"))
                return new MediaProfile { Media = "SCM", LatencyBadMs = 1, LatencyCriticalMs = 4, QueueBad = 32, IopsHeavy = 500_000, Mechanical = false };
            return new MediaProfile { Media = "SSD", LatencyBadMs = 8, LatencyCriticalMs = 20, QueueBad = 10, IopsHeavy = 20_000, Mechanical = false };
        }

        private static bool LooksLikeNvme(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return false;
            string s = model.ToUpperInvariant();
            return s.Contains("NVME") || s.Contains("NVMe") || s.Contains("M.2") || s.Contains("PCIe") || s.Contains("PCI-E");
        }

        /// <summary>
        /// Julga UM disco. Nunca usa um limite único de MB/s: combina atividade, fila,
        /// latência e MEIO para decidir se o que o usuário sente é vazão ou espera.
        /// </summary>
        public static Verdict Assess(StorageDiagnostics.DiskRate d)
        {
            var prof = ProfileFor(d.Device?.Media ?? "", d.Device?.Model ?? "");
            var v = new Verdict { ThresholdsUsed = prof.Describe() };
            v.ThresholdsUsed += $". Atividade medida = tempo ocupado (latência x operações/s), o mesmo do Gerenciador de Tarefas.";
            v.ThresholdsUsed += $". Meio informado pelo Windows: {(string.IsNullOrWhiteSpace(d.Device?.Media) ? "desconhecido (perfil assumido: " + prof.Media + ")" : prof.Media)}.";

            bool saturated = d.ActivityPct >= 85 || d.Queue >= prof.QueueBad || d.LatencyMs >= prof.LatencyCriticalMs;
            bool heavyIops = d.TransfersPerSec >= prof.IopsHeavy;
            bool highThroughput = prof.Mechanical ? d.TotalBps >= 120 * 1024 * 1024 : d.TotalBps >= 400 * 1024 * 1024;
            bool slowDevice = saturated && d.LatencyMs >= prof.LatencyBadMs && !highThroughput;

            string fmt(double bps) => StorageDiagnostics.FormatBps(bps);

            if (!saturated)
            {
                v.Level = Level.Ok;
                v.Badge = "OK";
                v.Kind = "Saudavel";
                v.Title = "Armazenamento saudável";
                v.Text = $"{d.Display} está com {d.ActivityPct:F0}% de atividade" +
                         (d.Queue > 0.05 ? $", fila {d.Queue:F1}" : "") +
                         (d.LatencyMs >= 0.5 ? $", latência {d.LatencyMs:F1} ms" : "") + ".\n" +
                         "Nada aqui explica lentidão agora. Se o problema acontece em momentos específicos, " +
                         "deixe o Kit escutando e volte quando acontecer.";
                return v;
            }

            if (slowDevice)
            {
                v.Level = Level.Critical;
                v.Badge = "CRITICO";
                v.Kind = "DispositivoLento";
                v.Title = prof.Mechanical ? "O disco está atolado mas entrega pouco" : "O dispositivo está saturado mas entrega pouco";
                v.Text = $"{d.Display} está com {d.ActivityPct:F0}% de atividade, mas movimenta só {fmt(d.TotalBps)}, " +
                         $"com latência de {d.LatencyMs:F1} ms e fila {d.Queue:F1}.\n\n" +
                         (prof.Mechanical
                             ? "Num HDD isso é o comportamento clássico de disco atolado: a cabeça mecânica não dá conta de tantas " +
                               "operações pequenas. HDD fragmentado, disco quase cheio, unidade com defeito, driver antigo do controlador, " +
                               "cabo/conexão ruim ou disco externo por USB lento explicam isso.\n"
                             : "Isso NÃO é um programa 'pesado': é o dispositivo sem dar conta. Sinais típicos de unidade com defeito, " +
                               "disco quase cheio, driver antigo do controlador ou conexão ruim (cabo/porta externa).\n") +
                         "Antes de culpar qualquer processo, verifique a saúde do disco e a cadeia de armazenamento.";
                return v;
            }

            if (highThroughput)
            {
                v.Level = Level.Attention;
                v.Badge = "ATENCAO";
                v.Kind = "TransferenciaAlta";
                v.Title = "Transferência alta — trabalho pesado legítimo";
                v.Text = $"{d.Display} está movimentando {fmt(d.ReadBps)} de leitura e {fmt(d.WriteBps)} de escrita " +
                         $"(atividade {d.ActivityPct:F0}%, latência {d.LatencyMs:F1} ms, {d.TransfersPerSec:F0} operações/s).\n\n" +
                         (prof.Mechanical
                             ? "Aqui o disco está trabalhando no limite do que um HDD consegue: é vazão de verdade, não espera."
                             : "Aqui a unidade está transferindo rápido: provavelmente copiar, instalar ou descarregar arquivos grandes.") +
                         " Se você não pediu isso, veja quem está no topo da lista ao lado.";
                return v;
            }

            if (heavyIops)
            {
                v.Level = Level.Attention;
                v.Badge = "ATENCAO";
                v.Kind = "MuitasOperacoes";
                v.Title = "Muitas operações pequenas no disco";
                v.Text = $"{d.Display} está com {d.ActivityPct:F0}% de atividade executando {d.TransfersPerSec:F0} operações por segundo, " +
                         $"mas movimentando apenas {fmt(d.TotalBps)} (latência {d.LatencyMs:F1} ms, fila {d.Queue:F1}).\n\n" +
                         "Muitas operações pequenas custam caro mesmo quando o volume de dados é baixo — é o padrão de indexação, " +
                         "verificação de antivírus, atualização de arquivos e bancos de dados pequenos.\n" +
                         "Isso é o que o usuário sente como lentidão. Veja os candidatos na tabela e use 'Testar impacto'.";
                return v;
            }

            v.Level = Level.Attention;
            v.Badge = "ATENCAO";
            v.Kind = "DiscoOcupado";
            v.Title = "Disco com atividade alta";
            v.Text = $"{d.Display} está com {d.ActivityPct:F0}% de atividade, fila {d.Queue:F1} e latência {d.LatencyMs:F1} ms.\n" +
                     "Isso é o que você sente como travadas e lentidão. Veja os candidatos na tabela — e use 'Testar impacto' " +
                     "para saber qual deles é realmente o culpado.";
            return v;
        }

        // ────────────────────────────────────────────────────────────────────
        //  DISTRIBUIÇÃO DO E/S ENTRE OS PROCESSOS
        // ────────────────────────────────────────────────────────────────────

        public sealed class Distribution
        {
            public double TopSharePct;
            public double Top3SharePct;
            public bool IsDistributed;
            public bool IsSystemTop;
            public string TopName = "";
            public int TopPid;
            public string Text = "";
        }

        /// <summary>Um processo "System"/PID baixo: atividade de baixo nível, não de programa.</summary>
        public static bool IsKernelActivity(int pid, string name)
            => pid == 0 || pid == 4 || name.Equals("System", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Analisa COMO o E/S está distribuído. Existe para o Kit conseguir dizer
        /// "nenhum processo individual explica a saturação" quando for o caso — em vez
        /// de escolher arbitrariamente o primeiro da lista.
        /// </summary>
        public static Distribution Analyze(IEnumerable<StorageDiagnostics.ProcIoRank> ranks, double diskTotalBps)
        {
            var list = ranks.Where(r => r.TotalBps > 0).OrderByDescending(r => r.TotalBps).ToList();
            var d = new Distribution();
            if (list.Count == 0)
            {
                d.IsDistributed = true;
                d.Text = diskTotalBps > 0
                    ? "Nenhum processo está transferindo volume relevante agora. A ocupação vem de baixo nível: " +
                      "paginação, driver do controlador, indexação em background ou operações do próprio Windows."
                    : "";
                return d;
            }

            double sum = list.Sum(r => r.TotalBps);
            var top = list[0];
            d.TopName = top.Name;
            d.TopPid = top.Pid;
            d.IsSystemTop = IsKernelActivity(top.Pid, top.Name);
            d.TopSharePct = sum > 0 ? top.TotalBps / sum * 100 : 0;
            d.Top3SharePct = sum > 0 ? list.Take(3).Sum(r => r.TotalBps) / sum * 100 : 0;
            d.IsDistributed = d.TopSharePct < 45 && list.Count >= 3;

            var sb = new StringBuilder();

            if (d.IsSystemTop && d.TopSharePct >= 45)
            {
                sb.AppendLine("A maior parte do E/S está no processo System — isso é atividade de baixo nível " +
                              "(cache, paginação e drivers), não de um programa.");
                sb.AppendLine("O Kit não vai apontar o System como culpado: para reduzir isso é preciso olhar o dispositivo " +
                              "e os drivers. Use a aba Latência para ver se algum driver está consumindo tempo em DPC/ISR.");
            }
            else if (d.IsDistributed)
            {
                sb.AppendLine($"A atividade está espalhada: o maior processo ({top.Name}, PID {top.Pid}) responde por apenas " +
                              $"{d.TopSharePct:F0}% do E/S observado, e os 3 maiores somam {d.Top3SharePct:F0}%.");
                sb.AppendLine("Nenhum processo individual explica a saturação. Nesse cenário, suspender um único programa " +
                              "raramente alivia o disco — o peso vem da soma. O Kit testa os candidatos um a um e mostra o " +
                              "impacto de cada um, para você saber se existe pelo menos um que pese.");
            }
            else if (d.TopSharePct >= 55)
            {
                sb.AppendLine($"Principal candidato: {top.Name} (PID {top.Pid}) com {d.TopSharePct:F0}% de todo o E/S " +
                              $"observado entre os processos monitorados.");
                if (top.Services.Count > 0)
                    sb.AppendLine($"Ele hospeda o serviço {top.Services[0].DisplayName} ({top.Services[0].Name}) — " +
                                  "para agir, reiniciar/parar o SERVIÇO é mais correto que matar o processo.");
                sb.AppendLine("Ele está FAZENDO o E/S — para afirmar que é a CAUSA, use 'Testar impacto'.");
            }
            else
            {
                sb.AppendLine($"O maior consumidor é {top.Name} (PID {top.Pid}), com {d.TopSharePct:F0}% do E/S observado — " +
                              "não chega a explicar tudo sozinho.");
                sb.AppendLine("Provavelmente há mais de um responsável. Teste os primeiros da lista um por um.");
            }

            if (sum > 0 && diskTotalBps > 0)
            {
                double ratio = sum / diskTotalBps * 100;
                if (ratio < 15)
                    sb.AppendLine($"Observação: a soma de todos os processos é só {ratio:F0}% do que o disco movimenta — " +
                                  "boa parte do E/S fica em cache/kernel e não aparece por processo " +
                                  "(por isso o diagnóstico não fecha apenas com a lista).");
            }

            d.Text = sb.ToString().TrimEnd();
            return d;
        }
    }
}
