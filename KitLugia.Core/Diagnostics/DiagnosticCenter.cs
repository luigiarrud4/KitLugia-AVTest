using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using KitLugia.Core.TaskManager;

namespace KitLugia.Core.Diagnostics
{
    /// <summary>
    /// A CENTRAL DE DIAGNÓSTICO: roda todos os coletores em sequência, com progresso, e
    /// devolve um <see cref="DiagnosticReport"/> com achados classificados por gravidade,
    /// evidência crua e a sugestão de reparo.
    ///
    /// REGRAS DE PROJETO (para não virar mais uma tela que mente):
    ///  1. NADA aqui altera o sistema. Tudo é leitura (registro, WMI, event log, arquivos).
    ///     A única exceção do módulo é ligar o canal de glitch de áudio, e isso é uma ação
    ///     explícita do usuário na UI (<see cref="AudioGlitchChannel.SetEnabled"/>).
    ///  2. Coletor que falha NÃO desaparece: cai em <see cref="DiagnosticReport.CollectorFailures"/>.
    ///     Silêncio seria lido como "está tudo bem".
    ///  3. Achado sem evidência não entra. Hipótese entra como INFO e diz que é hipótese.
    ///  4. Ruído conhecido (DistributedCOM 10016 e cia.) é filtrado de propósito — senão a
    ///     lista enche e o importante se perde.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class DiagnosticCenter
    {
        private const int EventLogDays = 7;
        private const int MaxEventFindings = 12;

        /// <summary>
        /// Executa a coleta completa. <paramref name="hostEvidence"/> e
        /// <paramref name="hostFindings"/> permitem que a camada de UI (que enxerga coisas
        /// que o Core não enxerga — log do console, logs do WinPE, estado dos timers do Kit)
        /// anexe o que só ela tem, sem o Core precisar conhecer a GUI.
        /// </summary>
        public static async Task<DiagnosticReport> RunAsync(
            IProgress<string>? progress = null,
            IReadOnlyList<DiagnosticEvidence>? hostEvidence = null,
            IReadOnlyList<DiagnosticFinding>? hostFindings = null,
            CancellationToken ct = default)
        {
            var report = new DiagnosticReport { Elevated = IsElevatedSafe() };
            var sw = Stopwatch.StartNew();

            var steps = new (string Name, Action<DiagnosticReport> Run)[]
            {
                ("Ambiente e sistema", CollectEnvironment),
                ("Volumes e espaço livre", CollectVolumes),
                ("Discos físicos (saúde)", CollectPhysicalDisks),
                ("Dispositivos com problema", CollectProblemDevices),
                ("Serviços automáticos parados", CollectStoppedAutoServices),
                ("Eventos críticos/erro (7 dias)", CollectEventLogs),
                ("Relatórios de falha (WER/dumps)", CollectCrashArtifacts),
                ("Reinicialização pendente", CollectPendingReboot),
                ("Intervenções do KitLugia", CollectKitJournal),
                ("Áudio (glitch e recuperação)", CollectAudio),
                ("Latência do sistema (DPC/ISR)", CollectLatency),
            };

            foreach (var (name, run) in steps)
            {
                if (ct.IsCancellationRequested) break;
                progress?.Report(name);
                try
                {
                    await Task.Run(() => run(report), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    report.CollectorFailures.Add($"{name}: {ex.GetType().Name} — {ex.Message}");
                }
            }

            if (hostFindings != null) report.Findings.AddRange(hostFindings);
            if (hostEvidence != null) report.Evidence.AddRange(hostEvidence);

            report.Duration = sw.Elapsed;
            return report;
        }

        // ══════════════════════════════════════════════════════════════════
        //  COLETORES
        // ══════════════════════════════════════════════════════════════════

        private static void CollectEnvironment(DiagnosticReport r)
        {
            var sb = new StringBuilder();
            string display = ReadRegistryString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion");
            string product = ReadRegistryString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName");
            sb.AppendLine($"SO: {Environment.OSVersion.VersionString} (build {Environment.OSVersion.Version.Build})");
            if (product.Length > 0) sb.AppendLine($"Produto: {product}{(display.Length > 0 ? " · versão " + display : "")}");
            sb.AppendLine($"Arquitetura: {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}   núcleos lógicos: {Environment.ProcessorCount}");
            sb.AppendLine($"Elevado: {(r.Elevated ? "sim" : "NÃO")}");

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Caption, TotalVisibleMemorySize, FreePhysicalMemory, LastBootUpTime FROM Win32_OperatingSystem");
                foreach (ManagementBaseObject o in searcher.Get())
                {
                    using var mo = o as ManagementObject;
                    if (mo == null) continue;
                    if (long.TryParse(mo["TotalVisibleMemorySize"]?.ToString(), out long totalKb) &&
                        long.TryParse(mo["FreePhysicalMemory"]?.ToString(), out long freeKb))
                    {
                        sb.AppendLine($"RAM: {freeKb / 1024.0 / 1024:F1} GB livres de {totalKb / 1024.0 / 1024:F1} GB");
                        double usedPct = totalKb > 0 ? (totalKb - freeKb) * 100.0 / totalKb : 0;
                        if (usedPct >= 90)
                        {
                            r.Findings.Add(new DiagnosticFinding
                            {
                                Severity = DiagnosticSeverity.Warning,
                                Category = "Memória",
                                Title = $"Memória em {usedPct:F0}% de uso no momento da coleta",
                                Evidence = $"RAM total {totalKb / 1024.0 / 1024:F1} GB, livre {freeKb / 1024.0 / 1024:F1} GB.",
                                Suggestion = "Se isso for constante, verifique vazamentos de memória no Gerenciador de Tarefas (aba Processos, coluna Memória) antes de culpar o Windows.",
                                PageTag = "⚡",
                                Source = "Win32_OperatingSystem"
                            });
                        }
                    }
                    var boot = mo["LastBootUpTime"]?.ToString();
                    if (!string.IsNullOrEmpty(boot))
                    {
                        try
                        {
                            var when = ManagementDateTimeConverter.ToDateTime(boot);
                            sb.AppendLine($"Última inicialização: {when:dd/MM/yyyy HH:mm}   (uptime {(DateTime.Now - when).TotalHours:F1}h)");
                        }
                        catch
                        {
                            sb.AppendLine($"Última inicialização: {boot}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                r.CollectorFailures.Add($"Ambiente (WMI): {ex.GetType().Name} — {ex.Message}");
            }

            r.Evidence.Add(DiagnosticEvidence.From("Ambiente do sistema", sb.ToString(), alwaysCopy: true));

            if (!r.Elevated)
            {
                r.Findings.Add(new DiagnosticFinding
                {
                    Severity = DiagnosticSeverity.Warning,
                    Category = "Ambiente",
                    Title = "O KitLugia não está rodando como administrador",
                    Evidence = "Sem elevação, o Windows bloqueia a leitura de alguns canais de eventos e de parte do registro, " +
                               "o que reduz a cobertura do diagnóstico.",
                    Suggestion = "Feche o KitLugia e reabra como administrador para um diagnóstico completo.",
                    Source = "Ambiente"
                });
            }
        }

        private static void CollectVolumes(DiagnosticReport r)
        {
            var sb = new StringBuilder();
            var low = new List<string>();
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                long total, free;
                try
                {
                    total = d.TotalSize;
                    free = d.TotalFreeSpace;
                }
                catch
                {
                    continue;
                }
                if (total <= 0) continue;
                double pct = free * 100.0 / total;
                sb.AppendLine($"{d.Name} {d.DriveFormat} — {Gb(free)} GB livres de {Gb(total)} GB ({pct:F1}%)");
                if (pct < 5 || free < 2L * 1024 * 1024 * 1024)
                    low.Add($"{d.Name} com apenas {Gb(free)} GB livres ({pct:F1}% — abaixo de 5%)");
                else if (pct < 12)
                    low.Add($"{d.Name} com {Gb(free)} GB livres ({pct:F1}% — abaixo de 12%)");
            }

            r.Evidence.Add(DiagnosticEvidence.From("Volumes locais", sb.Length > 0 ? sb.ToString() : "(nenhum volume fixo pronto)", alwaysCopy: true));

            if (low.Count > 0)
            {
                bool anyCritical = low.Any(l => l.Contains("abaixo de 5%"));
                r.Findings.Add(new DiagnosticFinding
                {
                    Severity = anyCritical ? DiagnosticSeverity.Critical : DiagnosticSeverity.Warning,
                    Category = "Armazenamento",
                    Title = anyCritical ? "Disco quase cheio em pelo menos um volume" : "Espaço livre baixo em pelo menos um volume",
                    Evidence = string.Join("\n", low),
                    Suggestion = "Libere espaço antes de qualquer outra coisa: o Windows Update, o arquivo de paginação e os " +
                                 "pontos de restauração falham quando o volume do sistema fica sem folga.",
                    RepairName = "Limpeza de Disco (SAGE)",
                    PageTag = "💿",
                    Source = "DriveInfo"
                });
            }
        }

        private static void CollectPhysicalDisks(DiagnosticReport r)
        {
            var sb = new StringBuilder();
            List<StorageDiagnostics.DiskDevice> disks;
            try
            {
                disks = StorageDiagnostics.GetDevices();
            }
            catch (Exception ex)
            {
                r.CollectorFailures.Add($"Discos físicos: {ex.GetType().Name} — {ex.Message}");
                return;
            }

            var bad = new List<string>();
            foreach (var d in disks)
            {
                sb.AppendLine($"{d.Display}: {d.Describe} · {Gb((long)(d.SizeGb * 1024 * 1024 * 1024))} GB · saúde '{d.Health}'");
                if (IsDiskHealthBad(d.Health))
                    bad.Add($"{d.Display} ({d.Model}) reporta saúde '{d.Health}'");
            }
            r.Evidence.Add(DiagnosticEvidence.From("Discos físicos", sb.Length > 0 ? sb.ToString() : "(nenhum disco físico listado)"));

            if (bad.Count > 0)
            {
                r.Findings.Add(new DiagnosticFinding
                {
                    Severity = DiagnosticSeverity.Critical,
                    Category = "Armazenamento",
                    Title = "Disco físico reportando estado de saúde anormal",
                    Evidence = string.Join("\n", bad) + "\n(0 = saudável; qualquer outro valor veio do Storage API do Windows)",
                    Suggestion = "Faça backup AGORA do que for importante nesse disco e confira o SMART completo antes de " +
                                 "culpar o Windows por travamentos ou lentidão. Ataque o problema na origem antes de otimizar.",
                    PageTag = "💽",
                    Source = "MSFT_PhysicalDisk"
                });
            }
        }

        private static void CollectProblemDevices(DiagnosticReport r)
        {
            var warn = new List<string>();
            var info = new List<string>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
                foreach (ManagementBaseObject o in searcher.Get())
                {
                    using var mo = o as ManagementObject;
                    if (mo == null) continue;
                    if (!int.TryParse(mo["ConfigManagerErrorCode"]?.ToString(), out int code)) continue;
                    string name = mo["Name"]?.ToString() ?? "(sem nome)";
                    string desc = $"{name} — código {code} ({DeviceErrorMeaning(code)})";
                    // 24 = dispositivo não presente (fantasma) e 22 = desativado: não são falhas.
                    if (code == 24) continue;
                    if (code == 22) info.Add(desc);
                    else warn.Add(desc);
                }
            }
            catch (Exception ex)
            {
                r.CollectorFailures.Add($"Dispositivos: {ex.GetType().Name} — {ex.Message}");
                return;
            }

            if (warn.Count > 0)
            {
                r.Findings.Add(new DiagnosticFinding
                {
                    Severity = DiagnosticSeverity.Warning,
                    Category = "Drivers/Dispositivos",
                    Title = $"{warn.Count} dispositivo(s) não estão funcionando corretamente",
                    Evidence = string.Join("\n", warn),
                    Suggestion = "Código 28 = sem driver instalado; 43 = o Windows parou o dispositivo (quase sempre driver ou hardware); " +
                                 "10/31 = o driver não consegue iniciar. Reinstale o driver pelo fabricante antes de qualquer otimização.",
                    PageTag = "💾",
                    Source = "Win32_PnPEntity"
                });
            }
            if (info.Count > 0)
            {
                r.Findings.Add(new DiagnosticFinding
                {
                    Severity = DiagnosticSeverity.Info,
                    Category = "Drivers/Dispositivos",
                    Title = $"{info.Count} dispositivo(s) desativado(s) (pode ser intencional)",
                    Evidence = string.Join("\n", info),
                    Suggestion = "Dispositivo desativado é escolha de alguém (ou saída de fábrica). Só ligue se você sabe que falta essa função.",
                    PageTag = "💾",
                    Source = "Win32_PnPEntity"
                });
            }
        }

        private static void CollectStoppedAutoServices(DiagnosticReport r)
        {
            var stopped = new List<string>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DisplayName FROM Win32_Service WHERE StartMode = 'Auto' AND State = 'Stopped'");
                foreach (ManagementBaseObject o in searcher.Get())
                {
                    using var mo = o as ManagementObject;
                    if (mo == null) continue;
                    string name = mo["Name"]?.ToString() ?? "";
                    if (name.Length == 0) continue;
                    if (IsTriggerStartService(name)) continue; // trigger-start parado é NORMAL
                    stopped.Add($"{name} — {mo["DisplayName"]}");
                }
            }
            catch (Exception ex)
            {
                r.CollectorFailures.Add($"Serviços: {ex.GetType().Name} — {ex.Message}");
                return;
            }

            if (stopped.Count == 0) return;

            stopped.Sort(StringComparer.OrdinalIgnoreCase);
            r.Findings.Add(new DiagnosticFinding
            {
                Severity = DiagnosticSeverity.Warning,
                Category = "Serviços",
                Title = $"{stopped.Count} serviço(s) de inicialização automática estão parados",
                Evidence = string.Join("\n", stopped) +
                           "\n(serviços com inicialização por disparo/trigger foram ignorados de propósito: neles, estar parado é o normal)",
                Suggestion = "Abra Serviços, confira um por um e inicie apenas o que você reconhece. Se um deles não volta a " +
                             "subir sozinho depois do reinício, é sinal de configuração quebrada — reinicie-o e observe o log do Sistema.",
                RepairName = "Reiniciar Serviços de Desempenho (SysMain + MMCSS + WSearch)",
                PageTag = "🛡️",
                Source = "Win32_Service"
            });
        }

        /// <summary>
        /// Ruído crônico que NÃO vira achado (mas continua visível nas evidências, para
        /// ninguém achar que foi escondido). DCOM e Perflib sozinhos respondem por dezenas
        /// de "erros" por dia em qualquer Windows saudável — listar ID por ID seria perder
        /// tempo com uma lista que nunca termina, então esses dois provedores vão inteiros.
        /// </summary>
        private static readonly HashSet<string> NoiseProviders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft-Windows-DistributedCOM",
            "Perflib",
            "Microsoft-Windows-Perflib",
            "Microsoft-Windows-Diagnosis-Scripted",
        };

        private static readonly Dictionary<string, string> NoiseEvents = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft-Windows-Kernel-EventTracing#2"] = "sessão de rastreamento já existente — ruído",
            ["Microsoft-Windows-CertificateServicesClient-CertEnroll#87"] = "renovação de certificado do domínio — ruído",
        };

        private sealed class EventGroup
        {
            public string Provider = "";
            public int EventId;
            public int Count;
            public DateTime First = DateTime.MaxValue;
            public DateTime Last = DateTime.MinValue;
            public string Sample = "";
            public string Log = "";
            public override string ToString() => $"{Provider} #{EventId}";
        }

        private static void CollectEventLogs(DiagnosticReport r)
        {
            var groups = new Dictionary<string, EventGroup>(StringComparer.OrdinalIgnoreCase);
            long windowMs = (long)TimeSpan.FromDays(EventLogDays).TotalMilliseconds;

            foreach (var logName in new[] { "System", "Application" })
            {
                try
                {
                    string query = $"*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= {windowMs}]]]";
                    var q = new EventLogQuery(logName, PathType.LogName, query) { TolerateQueryErrors = true };
                    using var reader = new EventLogReader(q);
                    for (int i = 0; i < 6000; i++)
                    {
                        using EventRecord? rec = reader.ReadEvent();
                        if (rec == null) break;
                        string provider = rec.ProviderName ?? "(sem provedor)";
                        int id = rec.Id;
                        string key = provider + "#" + id;
                        if (!groups.TryGetValue(key, out var g))
                        {
                            g = new EventGroup { Provider = provider, EventId = id, Log = logName };
                            // Amostra de texto é CARA (FormatDescription) e alguns eventos não
                            // têm DLL de mensagem — só buscamos para os primeiros grupos.
                            if (groups.Count < 30)
                            {
                                try
                                {
                                    string d = rec.FormatDescription() ?? "";
                                    g.Sample = d.Length > 240 ? d[..240] + "..." : d;
                                }
                                catch
                                {
                                    g.Sample = "";
                                }
                            }
                            groups[key] = g;
                        }
                        g.Count++;
                        var at = rec.TimeCreated ?? DateTime.MinValue;
                        if (at < g.First) g.First = at;
                        if (at > g.Last) g.Last = at;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    r.CollectorFailures.Add($"Log de eventos '{logName}': acesso negado (rode como administrador para incluir este log)");
                }
                catch (Exception ex)
                {
                    r.CollectorFailures.Add($"Log de eventos '{logName}': {ex.GetType().Name} — {ex.Message}");
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Janela analisada: últimos {EventLogDays} dias · logs: System, Application · níveis: crítico e erro");
            sb.AppendLine("(provedores de ruído crônico — " + string.Join(", ", NoiseProviders) +
                          " — aparecem aqui mas não viram achado)");
            foreach (var g in groups.Values.OrderByDescending(g => g.Count).Take(40))
                sb.AppendLine($"  {g.Count,5}x {g.Log} · {g.Provider} #{g.EventId}  (último: {g.Last:dd/MM HH:mm})");
            if (groups.Count == 0) sb.AppendLine("  (nenhum evento crítico/erro no período)");
            r.Evidence.Add(DiagnosticEvidence.From("Eventos críticos/erro agrupados", sb.ToString()));

            var findings = new List<DiagnosticFinding>();
            foreach (var g in groups.Values)
            {
                string key = g.Provider + "#" + g.EventId;
                if (NoiseProviders.Contains(g.Provider)) continue;
                if (NoiseEvents.ContainsKey(key)) continue;

                var (severity, hint) = ClassifyEvent(g);
                if (severity == null) continue;

                findings.Add(new DiagnosticFinding
                {
                    Severity = severity.Value,
                    Category = "Eventos do Windows",
                    Title = $"{g.Provider} #{g.EventId} — {g.Count}x em {EventLogDays} dias",
                    Evidence = $"log: {g.Log}\nprimeiro: {g.First:dd/MM/yyyy HH:mm}\núltimo: {g.Last:dd/MM/yyyy HH:mm}" +
                               (g.Sample.Length > 0 ? $"\nmensagem: {g.Sample}" : ""),
                    Suggestion = hint,
                    Source = g.Provider
                });
                if (findings.Count >= MaxEventFindings * 2) break;
            }

            r.Findings.AddRange(findings
                .OrderBy(f => (int)f.Severity)
                .ThenByDescending(f => f.Hits)
                .Take(MaxEventFindings));
        }

        /// <summary>Severidade do evento, ou null para ignorar (ruído/não acionável).</summary>
        private static (DiagnosticSeverity? Severity, string Hint) ClassifyEvent(EventGroup g)
        {
            string p = g.Provider;
            int id = g.EventId;

            // ── Hardware / disco / corrupção / falha grave do sistema ──
            switch (p)
            {
                case "disk":
                case "volmgr":
                case "volsnap":
                case "Ntfs":
                case "Microsoft-Windows-Ntfs":
                case "storahci":
                case "stornvme":
                case "Microsoft-Windows-StorDiag":
                case "Microsoft-Windows-DiskDiagnostic":
                    return (DiagnosticSeverity.Critical,
                        "O controlador de disco registrou erro de E/S. Antes de qualquer otimização: rode CHKDSK na unidade, " +
                        "troque o cabo/porta (SATA) e confira o SMART. Erro de bloco não se resolve com tweak.");

                case "Microsoft-Windows-WHEA-Logger":
                    return (DiagnosticSeverity.Critical,
                        "Erro de hardware reportado pela UEFI (WHEA). Costuma ser memória sem XMP estável, overclock ou tensão. " +
                        "Volte as configurações da BIOS ao padrão e teste a RAM.");

                case "BugCheck":
                case "Microsoft-Windows-WER-SystemErrorReporting":
                    return (DiagnosticSeverity.Critical,
                        $"O Windows parou com tela azul ({g.Count}x). Analise o minidump em C:\\Windows\\Minidump antes de " +
                        "culpar o Kit ou o Windows — driver e memória são as causas mais comuns.");

                case "Microsoft-Windows-Kernel-Power":
                    if (id == 41)
                        return (DiagnosticSeverity.Critical,
                            "Desligamento/reinício INESPERADO (Kernel-Power 41): o sistema caiu sem encerrar. Isso é energia ou " +
                            "travamento total — não é lentidão. Confira a fonte/cabo e os eventos 6008.");
                    return (null, "");

                case "EventLog":
                    if (id == 6008)
                        return (DiagnosticSeverity.Warning,
                            "O log confirma que o desligamento anterior foi inesperado. Cruze com Kernel-Power 41 e o minidump.");
                    return (null, "");

                case "Microsoft-Windows-Resource-Exhaustion-Detector":
                    return (DiagnosticSeverity.Warning,
                        "O Windows já detectou pressão de memória (processos fechados por falta de RAM virtual). Aumente o " +
                        "arquivo de paginação ou descubra quem consome a memória — antes de mexer em qualquer outra coisa.");

                case "Service Control Manager":
                    if (id is 7000 or 7001 or 7009 or 7011 or 7023 or 7024 or 7031 or 7034)
                        return (DiagnosticSeverity.Warning,
                            "Um serviço falhou ao iniciar ou terminou sozinho. Abra Serviços, localize o serviço citado na mensagem " +
                            "e reinicie-o observando o log do Sistema.");
                    return (null, "");

                case "Application Error":
                    return (DiagnosticSeverity.Warning,
                        "Um aplicativo travou. Se for sempre o MESMO .exe, é problema dele (driver/instalação); se for aleatório, " +
                        "suspeite de memória ou do disco.");

                case "Application Hang":
                    return (DiagnosticSeverity.Warning,
                        "Um aplicativo parou de responder (hang). Cruze com a aba Latência do Gerenciador de Tarefas para ver se " +
                        "havia DPC/ISR alto no mesmo instante.");

                case ".NET Runtime":
                    return (DiagnosticSeverity.Warning,
                        "Uma aplicação .NET lançou exceção não tratada. Detalhe da exceção está na mensagem do evento.");

                case "Microsoft-Windows-WindowsUpdateClient":
                    return (DiagnosticSeverity.Warning,
                        "O Windows Update registrou falha. Antes de reinstalar o Windows: rode o reparo de Windows Update e " +
                        "o DISM RestoreHealth.");

                case "Microsoft-Windows-Kernel-PnP":
                    return (DiagnosticSeverity.Warning,
                        "O PnP não conseguiu iniciar/configurar um dispositivo. Cruze com o evento de DriverFrameworks e reinstale o driver.");

                case "Microsoft-Windows-DriverFrameworks-UserMode":
                    return (DiagnosticSeverity.Warning,
                        "Um driver de modo usuário travou (comum em periféricos RGB/áudio). Reinstale o driver do fabricante.");

                case "Microsoft-Windows-User Profile Service":
                    return (DiagnosticSeverity.Warning,
                        "Problema ao carregar/salvar o perfil do usuário. Costuma indicar perfil corrompido ou disco com erro.");

                case "Microsoft-Windows-Kernel-Processor-Power":
                    return (null, ""); // muito comum em laptops, sem ação útil

                case "Microsoft-Windows-Hyper-V-Hypervisor":
                case "Microsoft-Windows-Hyper-V-Worker":
                case "Microsoft-Windows-Hyper-V-VmSwitch":
                    return (null, ""); // ruído em máquinas com virtualização
            }

            // Volume alto de erro do mesmo provedor desconhecido também é sinal.
            if (g.Count >= 25)
                return (DiagnosticSeverity.Warning,
                    $"O provedor '{g.Provider}' repetiu este erro {g.Count} vezes. Regra geral: repetição é sintoma — " +
                    "veja a mensagem no Visualizador de Eventos para saber a origem.");

            return (null, "");
        }

        private static void CollectCrashArtifacts(DiagnosticReport r)
        {
            var targets = new (string Label, string Path)[]
            {
                ("CrashDumps do usuário", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps")),
                ("WER — ReportArchive", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER\ReportArchive")),
                ("WER — ReportQueue", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER\ReportQueue")),
            };

            var sb = new StringBuilder();
            var recentDumps = new List<string>();
            var recent = DateTime.Now.AddDays(-30);

            foreach (var (label, dir) in targets)
            {
                if (!Directory.Exists(dir))
                {
                    sb.AppendLine($"{label}: (não existe)");
                    continue;
                }
                try
                {
                    var files = new DirectoryInfo(dir)
                        .EnumerateFiles("*", SearchOption.AllDirectories)
                        .Where(f => f.Extension.Equals(".dmp", StringComparison.OrdinalIgnoreCase) ||
                                    f.Extension.Equals(".wer", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    sb.AppendLine($"{label}: {files.Count} arquivo(s) — {dir}");
                    foreach (var f in files.Where(f => f.LastWriteTime >= recent).OrderByDescending(f => f.LastWriteTime).Take(8))
                        recentDumps.Add($"{f.LastWriteTime:dd/MM HH:mm} · {f.Name} ({Gb(f.Length)} GB) em {dir}");
                }
                catch (Exception ex)
                {
                    r.CollectorFailures.Add($"{label}: {ex.GetType().Name} — {ex.Message}");
                }
            }

            r.Evidence.Add(DiagnosticEvidence.From("Falhas e dumps (WER/CrashDumps)", sb.ToString()));

            if (recentDumps.Count > 0)
            {
                r.Findings.Add(new DiagnosticFinding
                {
                    Severity = DiagnosticSeverity.Warning,
                    Category = "Estabilidade",
                    Title = $"{recentDumps.Count} falha(s) de aplicativo nos últimos 30 dias",
                    Evidence = string.Join("\n", recentDumps),
                    Suggestion = "Se o mesmo aplicativo aparece repetidamente, o problema é dele (reinstale/atualize). " +
                                 "Se são aplicativos diferentes caindo juntos, investigue memória e disco primeiro.",
                    PageTag = "⚡",
                    Source = "WER"
                });
            }
        }

        private static void CollectPendingReboot(DiagnosticReport r)
        {
            var pend = new List<string>();
            if (RegistryKeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                pend.Add("Component Based Servicing (atualização do Windows pronta, aguardando reinício)");
            if (RegistryKeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                pend.Add("Windows Update exige reinício");
            if (RegistryValueExists(@"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations"))
                pend.Add("arquivos agendados para substituição no próximo boot (PendingFileRenameOperations)");

            if (pend.Count == 0) return;

            r.Findings.Add(new DiagnosticFinding
            {
                Severity = DiagnosticSeverity.Info,
                Category = "Windows Update",
                Title = "Há operações pendentes que só terminam no reinício",
                Evidence = string.Join("\n", pend),
                Suggestion = "Reinicie antes de tirar conclusões: atualização pela metade muda o comportamento de drivers, " +
                             "energia e até do áudio. Depois do reboot, rode o diagnóstico de novo.",
                PageTag = "🪟",
                Source = "Registro"
            });
        }

        private static void CollectKitJournal(DiagnosticReport r)
        {
            var sb = new StringBuilder();
            try
            {
                var journal = StorageDiagnostics.Journal;
                sb.AppendLine($"{journal.Count} intervenção(ões) do Kit nesta sessão:");
                foreach (var e in journal.TakeLast(30)) sb.AppendLine("  " + e.ToLine());
                if (journal.Count == 0) sb.AppendLine("  (nenhuma — o Kit ainda não suspendeu processo nem mexeu em serviço)");
            }
            catch (Exception ex)
            {
                r.CollectorFailures.Add($"Diário do Kit: {ex.GetType().Name} — {ex.Message}");
            }
            r.Evidence.Add(DiagnosticEvidence.From("Diário de intervenções do KitLugia", sb.ToString(), alwaysCopy: true));

            try
            {
                int pending = StorageDiagnostics.PendingReverts;
                int svc = StorageDiagnostics.PendingServiceRestores;
                var suspended = StorageDiagnostics.SuspendedByKit;

                if (pending > 0 || svc > 0 || suspended.Count > 0)
                {
                    var parts = new List<string>();
                    if (pending > 0) parts.Add($"{pending} intervenção(ões) de processo ainda sem reversão registrada");
                    if (svc > 0) parts.Add($"{svc} serviço(s) alterado(s) e ainda não restaurado(s)");
                    if (suspended.Count > 0) parts.Add($"{suspended.Count} processo(s) ainda SUSPENSO(S) pelo Kit (PID: {string.Join(", ", suspended.Take(20))})");

                    r.Findings.Add(new DiagnosticFinding
                    {
                        Severity = DiagnosticSeverity.Warning,
                        Category = "KitLugia",
                        Title = "O KitLugia deixou alterações temporárias abertas",
                        Evidence = string.Join("\n", parts),
                        Suggestion = "Abra o Gerenciador de Tarefas do Kit → aba Disco → Diário, e use 'Retomar tudo'/'Restaurar serviços'. " +
                                     "Processo suspenso esquecido explica travamento que o usuário costuma atribuir ao Windows.",
                        PageTag = "💽",
                        Source = "StorageDiagnostics"
                    });
                }
            }
            catch (Exception ex)
            {
                r.CollectorFailures.Add($"Estado do diário: {ex.GetType().Name} — {ex.Message}");
            }
        }

        private static void CollectAudio(DiagnosticReport r)
        {
            // 1) Canal de causa (GlitchDetection) — é o "porquê" que faltava.
            AudioGlitchChannel.ReadResult channel;
            try
            {
                channel = AudioGlitchChannel.ReadRecent(TimeSpan.FromMinutes(30));
            }
            catch (Exception ex)
            {
                r.CollectorFailures.Add($"Canal de glitch de áudio: {ex.GetType().Name} — {ex.Message}");
                channel = new AudioGlitchChannel.ReadResult();
            }
            r.Evidence.Add(DiagnosticEvidence.From("Áudio — canal GlitchDetection (causa reportada pelo Windows)",
                AudioGlitchChannel.BuildEvidenceBody(channel)));

            if (!channel.Enabled || !channel.ChannelExists)
            {
                r.Findings.Add(new DiagnosticFinding
                {
                    Severity = DiagnosticSeverity.Info,
                    Category = "Áudio",
                    Title = "A causa dos estalos de áudio não está sendo registrada (canal desligado)",
                    Evidence = "O Windows tem o canal '" + AudioGlitchChannel.ChannelName + "', que grava a causa interna de cada " +
                               "glitch (fim de glitch do endpoint, pacotes pendentes, sobreleitura do servidor). Ele vem " +
                               "DESLIGADO de fábrica, então hoje o Kit só consegue provar que o áudio travou — não por quê.\n" +
                               "estado atual: " + channel.Describe(),
                    Suggestion = "Ligue o canal para o Windows gravar a causa. Exige administrador e pode ser desligado a qualquer " +
                                 "momento (o Kit registra isso no diário de intervenções). Depois de ligar, reproduza o estalo e " +
                                 "rode o diagnóstico de novo.",
                    ActionId = "audio:glitch-channel-on",
                    ActionLabel = "Ligar canal de causa (admin)",
                    Source = "AudioGlitchChannel"
                });
            }
            else if (channel.Events.Count > 0)
            {
                var top = channel.Events.Take(5).Select(e => e.ToLine());
                r.Findings.Add(new DiagnosticFinding
                {
                    Severity = DiagnosticSeverity.Warning,
                    Category = "Áudio",
                    Title = $"O motor de áudio do Windows registrou {channel.Events.Count} glitch(es) nos últimos 30 min",
                    Evidence = string.Join("\n", top) + (channel.Events.Count > 5 ? $"\n... e mais {channel.Events.Count - 5}." : ""),
                    Suggestion = "Este texto vem do próprio Windows, não de hipótese do Kit: use o evento mais próximo do horário em " +
                                 "que você ouviu o estalo para saber se a falha foi do driver/endpoint (BASE End Glitch), de pacote " +
                                 "faltando (IOMGR) ou de sobreleitura do servidor de saída.",
                    PageTag = "🔬",
                    Source = "Microsoft-Windows-Audio"
                });
            }

            // 2) Estado do monitor do Kit + correlação temporal com o canal.
            try
            {
                var mon = AudioGlitchMonitor.Instance;
                var (level, title, text) = mon.BuildConclusion();
                var glitches = mon.Glitches.Where(g => !g.ProvokedByKit).ToList();
                var confirmed = glitches.Where(g => g.Kind == "CONFIRMADO").ToList();

                var sb = new StringBuilder();
                sb.AppendLine($"escuta ativa: {mon.IsRunning}   dispositivo: {(mon.DeviceName.Length > 0 ? mon.DeviceName : "(padrão)")}");
                sb.AppendLine($"eventos: {glitches.Count} (confirmados: {confirmed.Count})   resets automáticos: {mon.RecoveryCount}");
                sb.AppendLine(title);
                sb.AppendLine(text);

                if (confirmed.Count > 0 && channel.Events.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Correlação com o canal do Windows (±3 s) — a causa interna de cada estalo confirmado:");
                    foreach (var g in confirmed.OrderByDescending(g => g.At).Take(15))
                    {
                        var near = channel.Events.Where(e => Math.Abs((e.When - g.At).TotalSeconds) <= 3).ToList();
                        sb.AppendLine(near.Count > 0
                            ? $"  {g.At:HH:mm:ss} — {string.Join(" | ", near.Select(e => $"Event {e.EventId}: {e.Summary}"))}"
                            : $"  {g.At:HH:mm:ss} — (sem evento do Windows na janela; o estalo pode ter outra origem)");
                    }
                }
                r.Evidence.Add(DiagnosticEvidence.From("Áudio — conclusão do KitLugia", sb.ToString(), alwaysCopy: true));

                if (level >= 2)
                {
                    r.Findings.Add(new DiagnosticFinding
                    {
                        Severity = DiagnosticSeverity.Critical,
                        Category = "Áudio",
                        Title = $"{confirmed.Count} travada(s) de áudio confirmada(s) pelo Windows nesta sessão",
                        Evidence = title + "\n" + text,
                        Suggestion = "Travada confirmada pelo motor de áudio é problema real, não impressão. Comece pelo disco/DCP " +
                                     "(aba Latência) e pela aba Áudio do Gerenciador de Tarefas do Kit — o relatório completo já traz " +
                                     "top de E/S e maior DPC no instante de cada estalo.",
                        PageTag = "🔬",
                        Source = "AudioGlitchMonitor"
                    });
                }
                else if (level == 1)
                {
                    r.Findings.Add(new DiagnosticFinding
                    {
                        Severity = DiagnosticSeverity.Warning,
                        Category = "Áudio",
                        Title = "O Kit observou descontinuidades no stream de áudio",
                        Evidence = title + "\n" + text,
                        Suggestion = "Hipótese, não veredito: veja o relatório da aba Áudio do Gerenciador de Tarefas do Kit (botão de " +
                                     "copiar/IA) para o detalhe por evento.",
                        PageTag = "🔬",
                        Source = "AudioGlitchMonitor"
                    });
                }
                else if (!mon.IsRunning)
                {
                    r.Findings.Add(new DiagnosticFinding
                    {
                        Severity = DiagnosticSeverity.Info,
                        Category = "Áudio",
                        Title = "A escuta de áudio do Kit não estava ativa nesta sessão",
                        Evidence = "Sem a escuta, a captura loopback oficial do WASAPI não rodou e não há como medir estalo de áudio.",
                        Suggestion = "Se o sintoma é áudio, ligue a escuta na aba Áudio do Gerenciador de Tarefas do Kit e reproduza o " +
                                     "problema antes de concluir qualquer coisa.",
                        PageTag = "🔬",
                        Source = "AudioGlitchMonitor"
                    });
                }
            }
            catch (Exception ex)
            {
                r.CollectorFailures.Add($"Monitor de áudio: {ex.GetType().Name} — {ex.Message}");
            }
        }

        private static void CollectLatency(DiagnosticReport r)
        {
            try
            {
                var mon = LatencyMonitor.Instance;
                if (!mon.IsRunning)
                {
                    // Sem monitor ativo a evidência também precisa existir: um relatório que
                    // simplesmente omite a seção faz o leitor achar que a coleta falhou.
                    r.Evidence.Add(DiagnosticEvidence.From("Latência do sistema",
                        "Monitor de DPC/ISR do Kit NÃO estava ativo nesta sessão — não há medição de latência de kernel."));
                    r.Findings.Add(new DiagnosticFinding
                    {
                        Severity = DiagnosticSeverity.Info,
                        Category = "Latência",
                        Title = "O monitor de latência (DPC/ISR) não estava ativo",
                        Evidence = "Sem ele o Kit não tem dados de interrupções e drivers lentos desta sessão.",
                        Suggestion = "Se o sintoma é travada/micro-freeze, abra o Gerenciador de Tarefas do Kit → aba Latência, " +
                                     "aguarde ~1 minuto com o PC em uso e rode este diagnóstico de novo.",
                        PageTag = "🔬",
                        Source = "LatencyMonitor"
                    });
                    return;
                }

                var (level, text) = mon.BuildConclusion();
                r.Evidence.Add(DiagnosticEvidence.From("Latência do sistema (conclusão)", text, alwaysCopy: true));
                r.Evidence.Add(DiagnosticEvidence.From("Latência do sistema (relatório completo)", mon.BuildAiReport()));

                if (level >= 2)
                {
                    r.Findings.Add(new DiagnosticFinding
                    {
                        Severity = DiagnosticSeverity.Critical,
                        Category = "Latência",
                        Title = "Latência de kernel alta detectada (DPC/ISR)",
                        Evidence = text,
                        Suggestion = "Driver culpado aparece no relatório da aba Latência (maior DPC por driver). Atualize ou remova " +
                                     "esse driver antes de qualquer tweak de desempenho.",
                        PageTag = "🔬",
                        Source = "LatencyMonitor"
                    });
                }
                else if (level == 1)
                {
                    r.Findings.Add(new DiagnosticFinding
                    {
                        Severity = DiagnosticSeverity.Warning,
                        Category = "Latência",
                        Title = "Picos de latência de kernel observados",
                        Evidence = text,
                        Suggestion = "Veja a aba Latência do Gerenciador de Tarefas do Kit para o driver/evento específico.",
                        PageTag = "🔬",
                        Source = "LatencyMonitor"
                    });
                }
            }
            catch (Exception ex)
            {
                r.CollectorFailures.Add($"Latência: {ex.GetType().Name} — {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  UTILITÁRIOS
        // ══════════════════════════════════════════════════════════════════

        private static bool IsElevatedSafe()
        {
            try
            {
                return EnablementPackageManager.IsElevated();
            }
            catch
            {
                return false;
            }
        }

        private static string Gb(long bytes) => (bytes / 1024.0 / 1024 / 1024).ToString("F1");

        private static string ReadRegistryString(string subKey, string valueName)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(subKey, writable: false);
                return key?.GetValue(valueName)?.ToString() ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static bool RegistryKeyExists(string subKey)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(subKey, writable: false);
                return key != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool RegistryValueExists(string subKey, string valueName)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(subKey, writable: false);
                var v = key?.GetValue(valueName);
                if (v is string[] arr) return arr.Length > 0;
                return v != null && v.ToString()?.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Serviço de inicialização por DISPARO (trigger start) fica parado até o gatilho
        /// acontecer — flagrar isso encheria a lista de falso positivo. O Windows marca esses
        /// com a subchave TriggerInfo na própria definição do serviço.
        /// </summary>
        private static bool IsTriggerStartService(string serviceName)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\" + serviceName, writable: false);
                if (key == null) return true; // sem chave = não é serviço convencional; melhor ignorar
                if (key.GetValue("Start") is int start && start != 2) return true; // não é auto de verdade
                using var trigger = key.OpenSubKey("TriggerInfo", writable: false);
                return trigger != null;
            }
            catch
            {
                return true; // dúvida = não acusar
            }
        }

        /// <summary>
        /// O aviso de saúde do disco tem de vir do valor REAL do Storage API (HealthStatus),
        /// não de string vazia. <see cref="StorageDiagnostics.DiskDevice.Health"/> já chega
        /// traduzido por <c>StorageDiagnostics.HealthName</c> — "Saudável" (0),
        /// "Atenção" (1), "Não saudável" (2), "Desconhecido" (5) ou "Estado N".
        /// "Desconhecido" NÃO é alerta: significa que o disco não expôs o dado, e transformar
        /// falta de leitura em pânico foi exatamente o bug que a execução real pegou.
        /// </summary>
        private static bool IsDiskHealthBad(string health)
        {
            if (string.IsNullOrWhiteSpace(health)) return false;
            if (health.Equals("Saudável", StringComparison.Ordinal)) return false;
            if (health.Equals("Desconhecido", StringComparison.Ordinal)) return false;
            return health.Equals("Atenção", StringComparison.Ordinal)
                || health.Equals("Não saudável", StringComparison.Ordinal)
                || health.StartsWith("Estado ", StringComparison.Ordinal);
        }

        private static string DeviceErrorMeaning(int code) => code switch
        {
            1 => "não configurado corretamente",
            2 => "não é possível carregar o driver",
            3 => "driver corrompido ou sem memória",
            10 => "não é possível iniciar",
            12 => "recursos insuficientes",
            14 => "precisa de reinício",
            16 => "recursos parcialmente identificados",
            18 => "reinstalar o driver",
            19 => "registro do dispositivo corrompido",
            21 => "removendo",
            22 => "desativado",
            24 => "não presente",
            28 => "driver não instalado",
            29 => "firmware do dispositivo não forneceu recursos",
            31 => "não é possível carregar os drivers",
            32 => "driver de início desabilitado",
            37 => "falha ao inicializar o driver",
            39 => "driver corrompido ou ausente",
            43 => "o Windows parou o dispositivo por reportar problema",
            45 => "não conectado no momento",
            52 => "assinatura de driver não verificada",
            _ => "código desconhecido"
        };
    }
}
