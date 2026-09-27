using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KitLugia.Core.Diagnostics
{
    /// <summary>
    /// Gravidade de um achado. A ordem importa: quanto MENOR o valor, mais grave —
    /// é assim que a lista da UI ordena por padrão (o que dói primeiro aparece em cima).
    /// </summary>
    public enum DiagnosticSeverity
    {
        Critical = 0,
        Warning = 1,
        Info = 2
    }

    /// <summary>
    /// Um problema (ou observação) encontrado pelo diagnóstico, sempre com a EVIDÊNCIA
    /// junto — nada é afirmado sem dizer de onde saiu. Só achados com evidência real
    /// chegam à lista; hipótese vira <see cref="DiagnosticSeverity.Info"/> e diz isso.
    /// </summary>
    public sealed class DiagnosticFinding
    {
        public DiagnosticSeverity Severity { get; set; } = DiagnosticSeverity.Info;
        public string Category { get; set; } = "Geral";
        public string Title { get; set; } = "";
        /// <summary>Os dados crus que provam o achado (contagens, caminhos, IDs de evento...).</summary>
        public string Evidence { get; set; } = "";
        /// <summary>O que fazer — passos concretos, não "reinstale o Windows".</summary>
        public string Suggestion { get; set; } = "";
        /// <summary>Nome estável de um reparo da Central AIO (<see cref="GeneralRepairManager"/>) que ataca este achado.</summary>
        public string? RepairName { get; set; }
        /// <summary>Ação embutida que não é um reparo da Central AIO (ex.: "audio:glitch-channel-on").</summary>
        public string? ActionId { get; set; }
        /// <summary>Rótulo do botão quando o achado traz uma <see cref="ActionId"/>.</summary>
        public string? ActionLabel { get; set; }
        /// <summary>Tag de navegação (NavTagMap) da página que mostra o problema em detalhe.</summary>
        public string? PageTag { get; set; }
        public string Source { get; set; } = "KitLugia";
        public DateTime DetectedAt { get; set; } = DateTime.Now;

        public string SeverityText => Severity switch
        {
            DiagnosticSeverity.Critical => "CRÍTICO",
            DiagnosticSeverity.Warning => "ATENÇÃO",
            _ => "INFO"
        };

        public string Badge => Severity switch
        {
            DiagnosticSeverity.Critical => "🔴",
            DiagnosticSeverity.Warning => "🟡",
            _ => "🔵"
        };

        public bool CanResolve => !string.IsNullOrWhiteSpace(RepairName);
        public bool CanRunAction => !string.IsNullOrWhiteSpace(ActionId);
        public bool CanOpen => !string.IsNullOrWhiteSpace(PageTag);

        /// <summary>Contagem simples usada para deduplicar/ordenar achados repetidos.</summary>
        public int Hits { get; set; } = 1;

        public string ToLine()
            => $"{Badge} [{SeverityText}] {Category} — {Title}" +
               (Hits > 1 ? $" (x{Hits})" : "");

        public string ToBlock()
        {
            var sb = new StringBuilder();
            sb.AppendLine(ToLine());
            sb.AppendLine($"   origem: {Source}   detectado: {DetectedAt:dd/MM/yyyy HH:mm:ss}");
            if (Evidence.Length > 0)
                foreach (var line in Evidence.Split('\n'))
                    if (line.Trim().Length > 0) sb.AppendLine("   evidência: " + line.TrimEnd());
            if (Suggestion.Length > 0)
                sb.AppendLine("   como resolver: " + Suggestion.Trim());
            if (CanResolve) sb.AppendLine($"   reparo sugerido: {RepairName}");
            if (CanRunAction) sb.AppendLine($"   ação do Kit: {ActionLabel ?? ActionId}");
            if (CanOpen) sb.AppendLine($"   ver em: página {PageTag}");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Bloco de log/estado colado ao relatório sem julgar nada (log do Kit, logs do WinPE,
    /// blocos pré-formatados de outros módulos). Serve para o usuário copiar junto.
    /// </summary>
    public sealed class DiagnosticEvidence
    {
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        /// <summary>Se true, entra também na cópia "só o importante".</summary>
        public bool AlwaysCopy { get; set; }

        public static DiagnosticEvidence From(string title, string body, bool alwaysCopy = false)
            => new() { Title = title, Body = body ?? "", AlwaysCopy = alwaysCopy };
    }

    /// <summary>
    /// Resultado completo de uma execução do diagnóstico. Nada é escondido: quando um
    /// coletor falha, a falha entra em <see cref="CollectorFailures"/> em vez de virar
    /// silêncio (que o usuário leria como "está tudo bem").
    /// </summary>
    public sealed class DiagnosticReport
    {
        public DateTime GeneratedAt { get; set; } = DateTime.Now;
        public TimeSpan Duration { get; set; }
        public bool Elevated { get; set; }
        public string MachineName { get; set; } = Environment.MachineName;
        public string UserName { get; set; } = Environment.UserName;

        public List<DiagnosticFinding> Findings { get; } = new();
        public List<DiagnosticEvidence> Evidence { get; } = new();
        public List<string> CollectorFailures { get; } = new();

        public int CriticalCount => Findings.Count(f => f.Severity == DiagnosticSeverity.Critical);
        public int WarningCount => Findings.Count(f => f.Severity == DiagnosticSeverity.Warning);
        public int InfoCount => Findings.Count(f => f.Severity == DiagnosticSeverity.Info);

        /// <summary>Veredito de uma linha para o topo da tela e para o título da cópia.</summary>
        public string BuildSummary()
        {
            if (Findings.Count == 0)
                return "Nenhum problema encontrado — não há nada que precise de reparo agora.";
            var parts = new List<string>();
            if (CriticalCount > 0) parts.Add($"{CriticalCount} crítico(s)");
            if (WarningCount > 0) parts.Add($"{WarningCount} em atenção");
            if (InfoCount > 0) parts.Add($"{InfoCount} informativo(s)");
            return string.Join(" · ", parts) + $" — {Findings.Count} achado(s) no total.";
        }

        public string BuildTitle()
        {
            if (CriticalCount > 0) return $"🔴 {CriticalCount} problema(s) CRÍTICO(S) encontrado(s)";
            if (WarningCount > 0) return $"🟡 {WarningCount} ponto(s) de atenção";
            return "🟢 Nenhum problema crítico encontrado";
        }

        /// <summary>Ordena como a UI mostra: gravidade primeiro, depois categoria e título.</summary>
        public List<DiagnosticFinding> Sorted()
            => Findings
                .OrderBy(f => (int)f.Severity)
                .ThenBy(f => f.Category, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(f => f.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

        public string BuildText() => BuildReport(importantOnly: false);

        public string BuildClipboardText(bool importantOnly) => BuildReport(importantOnly);

        private string BuildReport(bool importantOnly)
        {
            var sb = new StringBuilder();
            sb.AppendLine("============================================================");
            sb.AppendLine(" KitLugia — CENTRAL DE DIAGNÓSTICO");
            sb.AppendLine("============================================================");
            sb.AppendLine($"Gerado: {GeneratedAt:dd/MM/yyyy HH:mm:ss}   (coleta em {Duration.TotalSeconds:F1}s)");
            sb.AppendLine($"Máquina: {MachineName}   usuário: {UserName}   elevado: {(Elevated ? "sim" : "NÃO")}");
            sb.AppendLine($"Veredito: {BuildTitle()}");
            sb.AppendLine(BuildSummary());
            sb.AppendLine();

            var shown = importantOnly
                ? Sorted().Where(f => f.Severity != DiagnosticSeverity.Info).ToList()
                : Sorted();

            if (importantOnly && shown.Count == 0)
                sb.AppendLine("(Não há achados críticos nem de atenção — este é o resumo curto.)");
            else if (shown.Count == 0)
                sb.AppendLine("(Nenhum achado.)");

            if (shown.Count > 0)
            {
                string currentCat = "";
                foreach (var f in shown)
                {
                    if (!string.Equals(currentCat, f.Category, StringComparison.Ordinal))
                    {
                        currentCat = f.Category;
                        sb.AppendLine();
                        sb.AppendLine($"── {currentCat} ─────────────────────────────────");
                    }
                    sb.Append(f.ToBlock());
                }
            }

            if (!importantOnly && CollectorFailures.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("── COLETORES QUE FALHARAM (o diagnóstico acima está INCOMPLETO) ──");
                foreach (var f in CollectorFailures) sb.AppendLine("   ! " + f);
            }

            var ev = importantOnly ? Evidence.Where(e => e.AlwaysCopy).ToList() : Evidence;
            if (ev.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("============================================================");
                sb.AppendLine(" EVIDÊNCIAS / LOGS BRUTOS");
                sb.AppendLine("============================================================");
                foreach (var e in ev)
                {
                    sb.AppendLine();
                    sb.AppendLine($"───── {e.Title} ─────");
                    sb.AppendLine(e.Body);
                }
            }
            return sb.ToString();
        }
    }
}
