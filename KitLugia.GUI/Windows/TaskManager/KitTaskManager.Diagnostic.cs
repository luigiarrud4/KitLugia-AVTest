using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KitLugia.Core;
using KitLugia.Core.Diagnostics;
using KitLugia.GUI.Logging;
using KitLugia.GUI.Services;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;

namespace KitLugia.GUI.Windows.TaskManager
{
    // ══════════════════════════════════════════════════════════════════════════
    //  Partial: CENTRAL DE DIAGNÓSTICO (aba "Diagnóstico")
    //
    //  POR QUE AQUI e não numa página do menu principal: o diagnóstico já mora
    //  neste gerenciador (Latência, Armazenamento, Áudio). Ter a mesma ferramenta
    //  em duas janelas seria duas telas para manter e o usuário sem saber qual é
    //  a "de verdade". Aqui ela fica ao lado das abas que alimentam os dados.
    //
    //  O que faz: UM botão dispara todos os coletores de
    //  KitLugia.Core.Diagnostics.DiagnosticCenter, o resultado entra ordenado por
    //  gravidade, cada achado traz EVIDÊNCIA + sugestão, e pode ser copiado ou
    //  atacado (reparo da Central AIO, ação do Kit, ou abrir a página relacionada).
    //  Depois de executar um reparo o diagnóstico roda DE NOVO e diz se o achado
    //  sumiu — não basta dizer "rodei".
    // ══════════════════════════════════════════════════════════════════════════

    public partial class KitTaskManagerWindow
    {
        private readonly ObservableCollection<DiagFindingVm> _diagFindings = new();
        private readonly ObservableCollection<DiagEvidenceVm> _diagEvidence = new();
        private DiagnosticReport? _diagReport;
        private bool _diagBuilt;
        private bool _diagRunning;
        private string _diagView = "Findings";

        /// <summary>
        /// Monta a aba na primeira abertura. Depois já dispara a coleta sozinho: quem
        /// chegou aqui (pelo atalho do painel ou clicando na aba) quer RESPOSTA, não um
        /// botão pedindo para apertar outro botão. O botão continua ali para repetir.
        /// </summary>
        private void EnsureDiagnosticBuilt()
        {
            if (_diagBuilt) return;
            _diagBuilt = true;

            ListDiagFindings.ItemsSource = _diagFindings;
            ListDiagEvidence.ItemsSource = _diagEvidence;
            SetDiagView("Findings");
            ResetDiagnosticUi();

            // A coleta é pesada (11 coletores: WMI, event log, registro, WER) e disputa CPU com o
            // primeiro refresh de processos que acabou de começar. Disparar em Background
            // garante que a janela apareça e desenhe ANTES — quem chegou pelo atalho quer a
            // janela rápida, não o relatório instantâneo.
            Dispatcher.BeginInvoke(
                new Action(() => { _ = RunDiagnosticsAsync(); }),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>Troca entre a lista de achados e os blocos de evidência/log bruto.</summary>
        private void SetDiagView(string view)
        {
            _diagView = view;
            PanelDiagFindings.Visibility = view == "Findings" ? Visibility.Visible : Visibility.Collapsed;
            PanelDiagEvidence.Visibility = view == "Evidence" ? Visibility.Visible : Visibility.Collapsed;

            foreach (var (btn, tag) in new[] { (BtnDiagViewFindings, "Findings"), (BtnDiagViewEvidence, "Evidence") })
            {
                bool on = tag == view;
                btn.FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
                btn.Opacity = on ? 1.0 : 0.62;
            }
        }

        private void DiagView_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string tag) SetDiagView(tag);
        }

        // ══════════════════════════════════════════════════════════════════
        //  EXECUÇÃO
        // ══════════════════════════════════════════════════════════════════

        private async void BtnDiagRun_Click(object sender, RoutedEventArgs e) => await RunDiagnosticsAsync();

        private async Task RunDiagnosticsAsync()
        {
            if (_diagRunning) return;
            _diagRunning = true;

            try
            {
                BtnDiagRun.IsEnabled = false;
                BarDiagProgress.Visibility = Visibility.Visible;
                BarDiagProgress.IsIndeterminate = true;
                TxtDiagProgress.Visibility = Visibility.Visible;
                TxtDiagProgress.Text = "Preparando coleta...";
                TxtDiagVerdict.Text = "Coletando dados do sistema...";
                TxtDiagVerdictDetail.Text = "";
                SetDiagStatus("");

                var progress = new Progress<string>(step => TxtDiagProgress.Text = "Coletando: " + step);

                // 1) O que só a GUI sabe (log do console, logs do WinPE, estado do Kit).
                // MEDIDO (harness de UI): rodava síncrono na UI thread e travava a troca
                // de aba seguinte (o ReadAllWinpeLogs varre todos os volumes). É leitura
                // pura de estáticos — seguro em worker.
                var (hostEvidence, hostFindings) = await Task.Run(() => BuildDiagnosticHostContext());

                // 2) Coleta do Core, em background.
                var report = await DiagnosticCenter.RunAsync(progress, hostEvidence, hostFindings);
                _diagReport = report;

                RenderDiagnosticReport(report);

                TxtDiagProgress.Text = $"Coleta concluída em {report.Duration.TotalSeconds:F1}s" +
                                       (report.CollectorFailures.Count > 0
                                            ? $" — {report.CollectorFailures.Count} coletor(es) falharam (veja em Evidências)"
                                            : "");
            }
            catch (Exception ex)
            {
                TxtDiagVerdict.Text = "Falha ao gerar o diagnóstico";
                TxtDiagVerdictDetail.Text = $"{ex.GetType().Name}: {ex.Message}";
                try { KitLugia.Core.Logger.Log($"[DIAGNÓSTICO] Falha: {ex}"); } catch { }
            }
            finally
            {
                _diagRunning = false;
                BtnDiagRun.IsEnabled = true;
                BarDiagProgress.IsIndeterminate = false;
                BarDiagProgress.Visibility = Visibility.Collapsed;
            }
        }

        private void RenderDiagnosticReport(DiagnosticReport report)
        {
            _diagFindings.Clear();
            foreach (var f in report.Sorted()) _diagFindings.Add(new DiagFindingVm(f));

            _diagEvidence.Clear();
            if (report.CollectorFailures.Count > 0)
            {
                _diagEvidence.Add(new DiagEvidenceVm("Coletores que falharam (diagnóstico incompleto)",
                    string.Join(Environment.NewLine, report.CollectorFailures.Select(x => "! " + x))));
            }
            foreach (var e in report.Evidence) _diagEvidence.Add(new DiagEvidenceVm(e.Title, e.Body));

            TxtDiagCritical.Text = report.CriticalCount.ToString();
            TxtDiagWarning.Text = report.WarningCount.ToString();
            TxtDiagInfo.Text = report.InfoCount.ToString();

            TxtDiagVerdict.Text = report.BuildTitle();
            TxtDiagVerdictDetail.Text = report.BuildSummary() +
                (report.Elevated ? "" : "  ·  sem elevação a cobertura é parcial — o Kit pode ser reaberto como admin.");

            TxtDiagEmptyFindings.Visibility = _diagFindings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtDiagEmptyEvidence.Visibility = _diagEvidence.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            BtnDiagCopyAll.IsEnabled = true;
            BtnDiagCopyImportant.IsEnabled = true;
            BtnDiagSave.IsEnabled = true;
            BtnDiagClear.IsEnabled = true;
        }

        private void ResetDiagnosticUi()
        {
            TxtDiagCritical.Text = "0";
            TxtDiagWarning.Text = "0";
            TxtDiagInfo.Text = "0";
            TxtDiagVerdict.Text = "Nada foi coletado ainda. Use GERAR DIAGNÓSTICO.";
            TxtDiagVerdictDetail.Text = "";
            TxtDiagEmptyFindings.Visibility = Visibility.Visible;
            TxtDiagEmptyEvidence.Visibility = Visibility.Visible;
        }

        private void SetDiagStatus(string text)
        {
            try { TxtDiagStatus.Text = text; } catch { }
        }

        /// <summary>
        /// Contexto que vive fora do Core. Aqui o que é sintoma vira ACHADO (erro no log do
        /// Kit, falha de shrink/fresh install no log do WinPE) em vez de só despejar texto.
        /// </summary>
        private (List<DiagnosticEvidence> Evidence, List<DiagnosticFinding> Findings) BuildDiagnosticHostContext()
        {
            var evidence = new List<DiagnosticEvidence>();
            var findings = new List<DiagnosticFinding>();

            // ── Log do console do Kit ──
            try
            {
                var tail = LogStore.GetRecent(400);
                evidence.Add(DiagnosticEvidence.From("Log do KitLugia (últimas 400 linhas do console)",
                    tail.Count > 0 ? string.Join(Environment.NewLine, tail) : "(log vazio nesta sessão)"));

                var wide = LogStore.GetRecent(8000);
                var problemLines = wide
                    .Where(l => l.Contains("ERRO", StringComparison.OrdinalIgnoreCase)
                             || l.Contains("error", StringComparison.OrdinalIgnoreCase)
                             || l.Contains("exception", StringComparison.OrdinalIgnoreCase)
                             || l.Contains("falhou", StringComparison.OrdinalIgnoreCase)
                             || l.Contains("FALHA", StringComparison.Ordinal))
                    .ToList();

                if (problemLines.Count > 0)
                {
                    findings.Add(new DiagnosticFinding
                    {
                        Severity = problemLines.Count >= 10 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info,
                        Category = "KitLugia",
                        Title = $"{problemLines.Count} linha(s) de erro no log do console do Kit",
                        Evidence = string.Join(Environment.NewLine, problemLines.TakeLast(15)),
                        Suggestion = "Confira se são erros de operações antigas/repetidas ou de algo que você acabou de tentar. " +
                                     "O log completo está em " + LogStore.FilePath,
                        ActionId = "kit:copy-log",
                        ActionLabel = "Copiar caminho do log",
                        Source = "LogStore"
                    });
                }

                evidence.Add(DiagnosticEvidence.From("Arquivo de log completo do Kit", LogStore.FilePath));
            }
            catch (Exception ex)
            {
                findings.Add(new DiagnosticFinding
                {
                    Severity = DiagnosticSeverity.Info,
                    Category = "KitLugia",
                    Title = "Não foi possível ler o log do console do Kit",
                    Evidence = $"{ex.GetType().Name}: {ex.Message}",
                    Source = "LogStore"
                });
            }

            // ── Estado interno do Kit (memória/timers) ──
            try
            {
                evidence.Add(DiagnosticEvidence.From("Estado interno do KitLugia", MemoryDiagnostics.GetMemoryReport()));
            }
            catch (Exception ex)
            {
                findings.Add(new DiagnosticFinding
                {
                    Severity = DiagnosticSeverity.Info,
                    Category = "KitLugia",
                    Title = "Relatório de memória do Kit indisponível",
                    Evidence = $"{ex.GetType().Name}: {ex.Message}",
                    Source = "MemoryDiagnostics"
                });
            }

            // ── Logs persistentes do WinPE (shrink / fresh install) ──
            try
            {
                var logs = WinbootManager.ReadAllWinpeLogs();
                foreach (var kv in logs)
                {
                    string body = kv.Value ?? "";
                    evidence.Add(DiagnosticEvidence.From(kv.Key, body));

                    var failLines = body.Split('\n')
                        .Where(l => l.Contains("Status: FAIL", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (failLines.Count > 0)
                    {
                        findings.Add(new DiagnosticFinding
                        {
                            Severity = DiagnosticSeverity.Critical,
                            Category = "WinPE (shrink / fresh install)",
                            Title = "O último trabalho do WinPE terminou com FALHA",
                            Evidence = $"arquivo: {kv.Key}\n" + string.Join(Environment.NewLine, failLines.Take(10)) +
                                       "\n" + DiagTailLines(body, 12),
                            Suggestion = "Shrink ou fresh install que falha deixa o disco no meio do caminho. Abra a página " +
                                         "WinPE, leia o log completo e refaça o agendamento — não repita o processo sem entender o motivo.",
                            Source = "WinbootManager"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                findings.Add(new DiagnosticFinding
                {
                    Severity = DiagnosticSeverity.Info,
                    Category = "WinPE (shrink / fresh install)",
                    Title = "Não foi possível ler os logs do WinPE",
                    Evidence = $"{ex.GetType().Name}: {ex.Message}",
                    Suggestion = "Se você não usou shrink nem instalação via WinPE nesta máquina, isso é normal.",
                    Source = "WinbootManager"
                });
            }

            return (evidence, findings);
        }

        private static string DiagTailLines(string body, int count)
        {
            var lines = body.Split('\n');
            return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - count)));
        }

        // ══════════════════════════════════════════════════════════════════
        //  COPIAR / SALVAR / LIMPAR
        // ══════════════════════════════════════════════════════════════════

        private void BtnDiagCopyImportant_Click(object sender, RoutedEventArgs e)
        {
            if (_diagReport == null) return;
            DiagCopy(_diagReport.BuildClipboardText(importantOnly: true), "Resumo (críticos e atenção) copiado.");
        }

        private void BtnDiagCopyAll_Click(object sender, RoutedEventArgs e)
        {
            if (_diagReport == null) return;
            DiagCopy(_diagReport.BuildClipboardText(importantOnly: false), "Relatório completo copiado (com evidências).");
        }

        private void BtnDiagSave_Click(object sender, RoutedEventArgs e)
        {
            if (_diagReport == null) return;
            try
            {
                string suggested = $"KitLugia_Diagnostico_{DateTime.Now:yyyyMMdd_HHmm}.txt";
                string? path = KitLugia.GUI.DialogHelper.SaveFile(suggested, "Texto (*.txt)|*.txt|Todos os arquivos (*.*)|*.*");
                if (string.IsNullOrWhiteSpace(path)) return;
                File.WriteAllText(path, _diagReport.BuildClipboardText(importantOnly: false), Encoding.UTF8);
                SetDiagStatus("💾 Relatório salvo em " + path);
            }
            catch (Exception ex)
            {
                SetDiagStatus($"❌ Não foi possível salvar: {ex.Message}");
            }
        }

        private void BtnDiagClear_Click(object sender, RoutedEventArgs e)
        {
            _diagFindings.Clear();
            _diagEvidence.Clear();
            _diagReport = null;
            ResetDiagnosticUi();
            BtnDiagCopyAll.IsEnabled = false;
            BtnDiagCopyImportant.IsEnabled = false;
            BtnDiagSave.IsEnabled = false;
            BtnDiagClear.IsEnabled = false;
            SetDiagStatus("");
        }

        private void BtnDiagCopyOne_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not DiagFindingVm vm) return;
            DiagCopy(vm.Block, "Achado copiado (com evidência e sugestão).");
        }

        private void BtnDiagCopyEvidence_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not DiagEvidenceVm vm) return;
            DiagCopy($"{vm.Title}{Environment.NewLine}{vm.Body}", "Evidência copiada.");
        }

        private void DiagCopy(string text, string successMessage)
        {
            try
            {
                // SetDataObject com persistência: sobrevive ao fechamento do Kit.
                System.Windows.Clipboard.SetDataObject(text, true);
                SetDiagStatus("📋 " + successMessage);
            }
            catch (Exception ex)
            {
                SetDiagStatus($"❌ Não foi possível copiar: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  RESOLVER / AÇÃO DO KIT / ABRIR PÁGINA
        // ══════════════════════════════════════════════════════════════════

        private async void BtnDiagResolve_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not DiagFindingVm vm) return;
            var finding = vm.Model;

            RepairAction? repair;
            try
            {
                repair = GeneralRepairManager.GetAllRepairs()
                    .FirstOrDefault(rp => string.Equals(rp.Name, finding.RepairName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                SetDiagStatus($"❌ Não foi possível carregar o reparo: {ex.Message}");
                return;
            }

            if (repair?.Execute == null)
            {
                SetDiagStatus($"⚠ '{finding.RepairName}' não existe mais na Central AIO de Reparos — execute manualmente pela página de Reparos.");
                return;
            }

            if (repair.IsDangerous &&
                !Confirm("Reparo avançado", $"O reparo '{repair.Name}' é marcado como AVANÇADO.\n\n{repair.Description}\n\nExecutar mesmo assim?"))
                return;

            try
            {
                string title = finding.Title;
                SetDiagStatus($"⚙ Executando '{repair.Name}'...");
                await Task.Run(() => repair.Execute());

                SetDiagStatus($"'{repair.Name}' concluído — reexecutando o diagnóstico para conferir...");

                // Reverificação da evidência depois do conserto: não basta dizer "rodei",
                // é preciso mostrar se o achado ainda existe.
                await RunDiagnosticsAsync();

                if (_diagReport != null && !_diagReport.Findings.Any(f => f.Title == title))
                    SetDiagStatus($"✅ O achado '{title}' não aparece mais no diagnóstico.");
                else
                    SetDiagStatus($"⚠ O achado '{title}' continua aparecendo — o reparo não foi suficiente. Veja a sugestão do item.");
            }
            catch (Exception ex)
            {
                SetDiagStatus($"❌ Falha ao executar o reparo: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private async void BtnDiagAction_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not DiagFindingVm vm) return;
            var finding = vm.Model;

            switch (finding.ActionId)
            {
                case "audio:glitch-channel-on":
                case "audio:glitch-channel-off":
                {
                    bool enable = finding.ActionId.EndsWith("-on", StringComparison.Ordinal);
                    if (enable && !Confirm("Canal de causa do áudio",
                        "Ligar o canal de eventos 'Microsoft-Windows-Audio/GlitchDetection'?\n\n" +
                        "• Exige administrador.\n" +
                        "• O Windows passará a registrar a CAUSA de cada estalo de áudio.\n" +
                        "• É reversível a qualquer momento e o Kit registra a mudança no diário de intervenções.\n\n" +
                        "Depois de ligar, reproduza o problema e gere o diagnóstico de novo."))
                        return;

                    var (ok, msg) = await Task.Run(() => AudioGlitchChannel.SetEnabled(enable));
                    SetDiagStatus((ok ? "✅ " : "⚠ ") + msg);
                    await RunDiagnosticsAsync();
                    return;
                }

                case "kit:copy-log":
                    DiagCopy(LogStore.FilePath, "Caminho do log copiado.");
                    return;

                default:
                    SetDiagStatus("Ação desconhecida: " + (finding.ActionId ?? "(vazia)"));
                    return;
            }
        }

        private void BtnDiagOpen_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not DiagFindingVm vm) return;
            string? tag = vm.Model.PageTag;
            if (string.IsNullOrWhiteSpace(tag)) return;

            try
            {
                if (Application.Current?.MainWindow is MainWindow mw)
                {
                    if (mw.WindowState == WindowState.Minimized) mw.WindowState = WindowState.Normal;
                    mw.Activate();
                    mw.NavigateToPage(tag);
                    SetDiagStatus($"🪟 Página aberta na janela principal (a página relacionada a este achado).");
                }
            }
            catch (Exception ex)
            {
                SetDiagStatus($"❌ Não foi possível abrir a página: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  VIEW-MODELS (só de apresentação)
        // ══════════════════════════════════════════════════════════════════

        public sealed class DiagFindingVm
        {
            public DiagFindingVm(DiagnosticFinding model)
            {
                Model = model;
                Block = model.ToBlock();
            }

            public DiagnosticFinding Model { get; }
            public string Block { get; }

            public string Badge => Model.Badge;
            public string SeverityText => Model.SeverityText;
            public string Category => $"{Model.Category}  ·  {Model.Source}";
            public string Title => Model.Title;
            public string Evidence => Model.Evidence;
            public string Suggestion => Model.Suggestion.Length > 0 ? "▶ " + Model.Suggestion : "";
            public string OriginLine => $"detectado {Model.DetectedAt:dd/MM HH:mm:ss}" +
                                        (Model.Hits > 1 ? $"  ·  {Model.Hits} ocorrência(s)" : "");

            public System.Windows.Media.Brush AccentBrush => Model.Severity switch
            {
                DiagnosticSeverity.Critical => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x52, 0x52)),
                DiagnosticSeverity.Warning => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xB3, 0x00)),
                _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4F, 0xC3, 0xF7))
            };

            public Visibility EvidenceVisibility => Model.Evidence.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            public Visibility SuggestionVisibility => Model.Suggestion.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            public Visibility ResolveVisibility => Model.CanResolve ? Visibility.Visible : Visibility.Collapsed;
            public Visibility ActionVisibility => Model.CanRunAction ? Visibility.Visible : Visibility.Collapsed;
            public Visibility OpenVisibility => Model.CanOpen ? Visibility.Visible : Visibility.Collapsed;

            public string ResolveLabel => Model.RepairName is { Length: > 0 } n
                ? $"⚙ Resolver: {Truncate(n, 26)}"
                : "⚙ Resolver";
            public string ActionLabel => Model.ActionLabel ?? Model.ActionId ?? "Ação";

            private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
        }

        public sealed class DiagEvidenceVm
        {
            /// <summary>Limite de exibição: o layout de TextBox com megabytes congela a UI
            /// (MEDIDO: travava a troca de aba seguinte). Copiar/Salvar usam Body (completo).</summary>
            private const int MaxPreviewChars = 30000;

            public DiagEvidenceVm(string title, string body)
            {
                Title = title;
                Body = body ?? "";
                Preview = Body.Length <= MaxPreviewChars ? Body
                    : Body.Substring(0, MaxPreviewChars) +
                      $"\n…[exibição cortada: {Body.Length:N0} caracteres no total — use Copiar/Salvar para o conteúdo completo]";
            }

            public string Title { get; }
            public string Body { get; }
            public string Preview { get; }
        }
    }
}
