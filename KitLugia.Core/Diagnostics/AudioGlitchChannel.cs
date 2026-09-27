using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace KitLugia.Core.Diagnostics
{
    /// <summary>
    /// Leitor do canal de eventos <c>Microsoft-Windows-Audio/GlitchDetection</c>.
    ///
    /// POR QUE ISTO EXISTE: o <see cref="TaskManager.AudioGlitchMonitor"/> prova que o áudio
    /// travou (flag oficial AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) e mede quanto se perdeu —
    /// mas não diz POR QUÊ. O Windows tem um canal que registra exatamente a causa interna:
    /// fim de glitch do endpoint (BASE End Glitch), pacotes IOMGR pendentes, sobreleitura do
    /// servidor de saída. É a resposta que faltava no card de áudio.
    ///
    /// FATOS VERIFICADOS NO HOST (não são suposição):
    ///  - provider Microsoft-Windows-Audio, GUID {ae4bd3be-f36f-45b6-8d21-bdd6fb832853};
    ///  - o canal vem DESLIGADO por padrão (isolation=System, sem retenção);
    ///  - LER não exige admin (o descritor dá leitura à IU); HABILITAR exige admin
    ///    (por isso <see cref="SetEnabled"/> é opt-in explícito e sempre reversível);
    ///  - na prática chegam eventos com EventID 36/41/48 (task 123, opcode 30), e não o
    ///    "Event ID 11" que a documentação de terceiros cita.
    /// Tudo aqui é SOMENTE LEITURA, exceto <see cref="SetEnabled"/>, que o usuário pede.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class AudioGlitchChannel
    {
        public const string ChannelName = "Microsoft-Windows-Audio/GlitchDetection";

        /// <summary>Chave de registro onde o Windows guarda se o canal está habilitado.</summary>
        private const string ChannelKeyPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WINEVT\Channels\" + ChannelName;

        private static readonly Guid AudioProviderGuid = new("ae4bd3be-f36f-45b6-8d21-bdd6fb832853");

        public sealed class GlitchEvent
        {
            public DateTime When { get; init; }
            public int EventId { get; init; }
            public int Opcode { get; init; }
            public int Task { get; init; }
            public int Level { get; init; }
            public int ProcessId { get; init; }
            public string OpcodeName { get; init; } = "";
            public string Summary { get; init; } = "";
            public List<KeyValuePair<string, string>> Fields { get; init; } = new();

            public string ToLine()
                => $"[{When:HH:mm:ss.fff}] Event {EventId} — {Summary}";

            public string ToBlock()
            {
                var sb = new StringBuilder();
                sb.AppendLine($"[{When:dd/MM HH:mm:ss.fff}] Event {EventId} (task {Task}, opcode {Opcode}) — {Summary}");
                if (ProcessId > 0) sb.AppendLine($"   processo do evento: PID {ProcessId}");
                foreach (var f in Fields) sb.AppendLine($"   {f.Key} = {f.Value}");
                return sb.ToString();
            }
        }

        public sealed class ReadResult
        {
            public bool ChannelExists { get; set; }
            public bool Enabled { get; set; }
            public bool AccessDenied { get; set; }
            public string Path { get; set; } = "";
            public string Note { get; set; } = "";
            public List<GlitchEvent> Events { get; } = new();

            public string Describe()
            {
                if (AccessDenied) return "o canal existe mas esta sessão não tem permissão para lê-lo (rode o Kit como administrador).";
                if (!ChannelExists) return "o canal não existe neste Windows.";
                if (!Enabled) return "o canal está DESLIGADO (padrão de fábrica) — o Windows não registrou nada nesta janela.";
                return Events.Count == 0
                    ? "canal ligado, mas nenhum evento de glitch na janela analisada."
                    : $"{Events.Count} evento(s) de glitch reportados pelo próprio motor de áudio.";
            }
        }

        // ── OPCODES declarados no manifesto do provider ───────────────────────
        // O que a prática mostrou chegar (task 123 / opcode 30) está mapeado por EventID abaixo.
        private static string OpcodeName(int opcode) => opcode switch
        {
            15 => "op_EVT_GLITCH_CM_RENDER (fim de glitch no render)",
            16 => "op_EVT_GLITCH_CM_CAPTURE (fim de glitch na captura)",
            17 => "op_EVT_GLITCH_APO_FORMAT_CONVERT (conversão de formato no APO)",
            18 => "CP_CLIENT_INPUT_NO_MESSAGES",
            19 => "INPUT_SIZE_MISMATCH (tamanho de entrada divergente)",
            20 => "OUTPUT_SERVER_OVERREAD (servidor de saída leu além do buffer)",
            21 => "OUTPUT_READ_POINTER_OVERWRITE (ponteiro de leitura sobrescrito)",
            30 => "KS Endpoint Glitch: BASE End Glitch",
            _ => $"opcode {opcode}"
        };

        /// <summary>
        /// Explicação por EventID. Os três primeiros foram capturados de verdade no host;
        /// os demais caem no texto genérico — melhor um rótulo honesto que um palpite.
        /// </summary>
        private static string EventSummary(int eventId, IReadOnlyList<KeyValuePair<string, string>> fields)
        {
            string Get(string name)
                => fields.FirstOrDefault(f => f.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? "";

            switch (eventId)
            {
                case 36:
                    return "Não há pacotes IOMGR pendentes — o motor ficou sem pacote para completar " +
                           $"(faltavam {Get("NextStreamingPacketToComplete")}, máximo {Get("MaxPacketCount")}).";
                case 41:
                {
                    var dur = Get("GlitchDuration");
                    var pos = Get("GlitchStreamPosition");
                    return "Fim de glitch do endpoint BASE: " +
                           (dur.Length > 0 ? $"duração informada {dur}, " : "") +
                           (pos.Length > 0 ? $"posição do stream {pos}." : "posição não informada.");
                }
                case 48:
                    return "Pino de render padrão (STREN privado) reportou glitch: " +
                           $"posição do dispositivo {Get("DevicePos")}, posição do stream {Get("StreamPos")}, " +
                           $"quadros disponíveis [{Get("AvailFrames")}].";
                default:
                    return fields.Count > 0
                        ? $"Evento de glitch do motor de áudio (primeiro campo: {fields[0].Key})."
                        : "Evento de glitch do motor de áudio (sem campos de dados).";
            }
        }

        /// <summary>
        /// O canal está ligado? Lê o registro primeiro (funciona mesmo sem o arquivo .evtx
        /// existir) e cai para a API de eventos se a chave não estiver lá.
        /// </summary>
        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(ChannelKeyPath, writable: false);
                if (key?.GetValue("Enabled") is int v) return v != 0;
                if (key?.GetValue("Enabled") is long l) return l != 0;
            }
            catch
            {
                // sem permissão de leitura da chave: tenta pela API abaixo
            }

            // Sem a chave no registro, o canal não foi habilitado nunca (o Windows só cria a
            // definição quando alguém mexe). Falso é a resposta correta, não um palpite.
            return false;
        }

        /// <summary>
        /// Lê os eventos de glitch da janela pedida. NUNCA lança: devolve o motivo no
        /// <see cref="ReadResult.Note"/> para o relatório poder ser honesto.
        /// </summary>
        public static ReadResult ReadRecent(TimeSpan window, int max = 60)
        {
            var result = new ReadResult
            {
                Enabled = IsEnabled(),
                Path = SafeLogPath()
            };

            try
            {
                long ms = Math.Max(1000, (long)window.TotalMilliseconds);
                string query = $"*[System[Provider[@Name='Microsoft-Windows-Audio'] and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
                var evQuery = new EventLogQuery(ChannelName, PathType.LogName, query) { TolerateQueryErrors = true };
                using var reader = new EventLogReader(evQuery);
                result.ChannelExists = true;

                for (int i = 0; i < 4000; i++)
                {
                    using EventRecord? rec = reader.ReadEvent();
                    if (rec == null) break;
                    try
                    {
                        var ev = ParseEvent(rec);
                        if (ev != null) result.Events.Add(ev);
                    }
                    catch
                    {
                        // registro corrompido não derruba a lista inteira
                    }
                }
            }
            catch (EventLogNotFoundException)
            {
                return new ReadResult { ChannelExists = false, Enabled = result.Enabled, Path = result.Path, Note = "canal não encontrado (nunca foi habilitado neste Windows)" };
            }
            catch (UnauthorizedAccessException)
            {
                return new ReadResult { ChannelExists = true, Enabled = result.Enabled, AccessDenied = true, Path = result.Path, Note = "sem permissão para ler o canal" };
            }
            catch (Exception ex)
            {
                return new ReadResult { ChannelExists = true, Enabled = result.Enabled, Path = result.Path, Note = $"{ex.GetType().Name}: {ex.Message}" };
            }

            result.Events.Sort((a, b) => b.When.CompareTo(a.When));
            if (result.Events.Count > max) result.Events.RemoveRange(max, result.Events.Count - max);
            return result;
        }

        /// <summary>
        /// Caminho do .evtx seguindo a convenção do Windows: a barra do nome do canal vira
        /// '%4' (verificado no host: Microsoft-Windows-Audio%4GlitchDetection.evtx).
        /// Derivar daqui evita depender de uma API cuja superfície muda entre versões do .NET.
        /// </summary>
        private static string SafeLogPath()
        {
            try
            {
                // 'Microsoft-Windows-Audio/GlitchDetection' -> 'Microsoft-Windows-Audio%4GlitchDetection'
                string file = ChannelName.Replace("/", "%4");
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "Winevt", "Logs", file + ".evtx");
            }
            catch
            {
                return "";
            }
        }

        private static GlitchEvent? ParseEvent(EventRecord rec)
        {
            var doc = XDocument.Parse(rec.ToXml());
            var ns = XNamespace.Get("http://schemas.microsoft.com/win/2004/08/events/event");

            var system = doc.Root?.Element(ns + "System");
            int eventId = ParseInt(system?.Element(ns + "EventID")?.Value);
            int opcode = ParseInt(system?.Element(ns + "Opcode")?.Value);
            int task = ParseInt(system?.Element(ns + "Task")?.Value);
            int level = ParseInt(system?.Element(ns + "Level")?.Value);

            int pid = 0;
            var exec = system?.Element(ns + "Execution");
            if (exec != null) int.TryParse(exec.Attribute("ProcessID")?.Value, out pid);

            var fields = new List<KeyValuePair<string, string>>();
            var data = doc.Root?.Element(ns + "EventData");
            if (data != null)
            {
                foreach (var d in data.Elements(ns + "Data"))
                {
                    string name = d.Attribute("Name")?.Value ?? "";
                    string val = d.Value ?? "";
                    if (name.Length == 0 && val.Length == 0) continue;
                    fields.Add(new KeyValuePair<string, string>(name.Length > 0 ? name : "valor", val));
                }
            }
            // Providers novos também usam <UserData>/<ComplexData>: se não houver EventData,
            // guarda o XML interno para não perder o dado.
            if (fields.Count == 0)
            {
                var inner = doc.Root?.Elements().FirstOrDefault(e => e.Name != ns + "System");
                if (inner != null)
                {
                    foreach (var e in inner.Descendants().Take(12))
                    {
                        if (!string.IsNullOrWhiteSpace(e.Value))
                            fields.Add(new KeyValuePair<string, string>(e.Name.LocalName, e.Value.Trim()));
                    }
                }
            }

            return new GlitchEvent
            {
                When = rec.TimeCreated ?? DateTime.Now,
                EventId = eventId,
                Opcode = opcode,
                Task = task,
                Level = level,
                ProcessId = pid,
                OpcodeName = OpcodeName(opcode),
                Summary = EventSummary(eventId, fields),
                Fields = fields
            };
        }

        private static int ParseInt(string? s) => int.TryParse(s, out int v) ? v : 0;

        /// <summary>Texto pronto para o relatório (estado do canal + blocos dos eventos mais recentes).</summary>
        public static string BuildEvidenceBody(ReadResult r, int maxBlocks = 12)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"canal: {ChannelName}");
            sb.AppendLine($"estado: {(r.Enabled ? "LIGADO" : "desligado")}");
            if (r.Path.Length > 0) sb.AppendLine($"arquivo: {r.Path}");
            sb.AppendLine("resultado: " + r.Describe());
            if (r.Note.Length > 0) sb.AppendLine($"observação: {r.Note}");
            if (r.Events.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("eventos (mais recente primeiro):");
                foreach (var e in r.Events.Take(maxBlocks)) sb.Append(e.ToBlock());
                if (r.Events.Count > maxBlocks)
                    sb.AppendLine($"... e mais {r.Events.Count - maxBlocks} evento(s).");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Liga/desliga o canal. Exige administrador (o descritor do canal dá só LEITURA à IU).
        /// Depois de mexer, confere o estado real e registra no diário de intervenções do Kit —
        /// nenhuma alteração feita pelo Kit pode ficar invisível.
        /// </summary>
        public static (bool Ok, string Message) SetEnabled(bool enable)
        {
            string arg = $"sl \"{ChannelName}\" /e:{(enable ? "true" : "false")}";
            var (exit, output, error) = ProcessRunner.Run("wevtutil.exe", arg, 15000);
            bool now = IsEnabled();
            bool ok = now == enable;
            string msg = ok
                ? $"canal de glitch de áudio {(enable ? "LIGADO" : "desligado")} com sucesso."
                : $"não foi possível alterar o canal (exit {exit}). " +
                  $"{(error.Length > 0 ? error.Trim() : output.Trim())}";

            try
            {
                TaskManager.StorageDiagnostics.RecordIntervention(
                    "canal de eventos",
                    ChannelName,
                    ok ? $"{(enable ? "habilitado" : "desabilitado")} para diagnóstico de áudio" : $"falhou: {msg}");
            }
            catch
            {
                // o diário é acessório: nunca derruba a operação
            }
            return (ok, msg);
        }

        /// <summary>Guid do provider, para quem quiser abrir sessão ETW própria (como o stub do stuttometer).</summary>
        public static Guid ProviderId => AudioProviderGuid;
    }
}
