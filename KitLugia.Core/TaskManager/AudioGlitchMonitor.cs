using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace KitLugia.Core.TaskManager
{
    // ══════════════════════════════════════════════════════════════════════════
    //  AUDIO GLITCH MONITOR — "o som estalou. Por quê, quando e por culpa de quê?"
    //
    //  COMO ISTO FUNCIONA (sem driver próprio, usando o que o Windows já faz):
    //
    //  1. Abrimos um stream de CAPTURA EM LOOPBACK no dispositivo de saída padrão
    //     (WASAPI, modo compartilhado). Não gravamos nada em disco: só lemos o
    //     buffer do motor de áudio.
    //
    //  2. A cada pacote, o próprio motor de áudio do Windows nos diz se houve
    //     descontinuidade no buffer — a flag DOCUMENTADA
    //     AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY (IAudioCaptureClient::GetBuffer,
    //     "In Windows 7 and later OS releases, this flag can be used for glitch
    //     detection"). Isso é MEDIÇÃO, não inferência: quem sinaliza é o Windows.
    //
    //  3. Além da flag, medimos QUANTO faltou: a posição do dispositivo
    //     (pu64DevicePosition) avança em quadros. Se ela salta além do esperado,
    //     os quadros que faltaram viram milissegundos de áudio perdido
    //     (quadros ÷ sample rate). É o "quanto travou" em número.
    //
    //  4. No instante do glitch, tiramos uma FOTO do resto do sistema (disco,
    //     CPU, DPC/ISR, paginação e quem estava fazendo E/S). É isso que permite
    //     dizer "travou o áudio às 21:44:03 por 42 ms, enquanto o NVMe estava em
    //     100% com fila 14 e o SearchIndexer era o maior responsável".
    //
    //  HONESTIDADE (regras do Kit — fato ≠ hipótese ≠ conclusão):
    //    • Só afirmamos glitch quando o próprio Windows sinaliza. Um salto de
    //      posição SEM a flag entra como "suspeito", não como glitch confirmado.
    //    • Se o NOSSO leitor de buffer também foi atrasado no mesmo instante,
    //      quem pode ter causado a descontinuidade fomos nós, não o áudio →
    //      o evento sai marcado como atribuição incerta (e dizemos isso).
    //    • Áudio em silêncio não gera evento audível: guardamos o nível para
    //      separar "glitch que você ouviu" de "glitch no vazio".
    //    • A causa provável é sempre texto de HIPÓTESE. Para virar prova, use a
    //      aba Disco ("Testar impacto").
    //
    //  CUSTO (não podemos atrapalhar o que estamos medindo):
    //    • 1 leitura de buffer a cada ~10 ms, sem gravar em disco, sem log por
    //      tick. A thread roda em MMCSS "Pro Audio" com prioridade AboveNormal
    //      (o mesmo mecanismo que players usam) e o Kit MEDE o próprio atraso —
    //      é assim que ele sabe que não foi ele quem travou o áudio.
    // ══════════════════════════════════════════════════════════════════════════
    public sealed class AudioGlitchMonitor
    {
        private AudioGlitchMonitor() { }

        private static readonly Lazy<AudioGlitchMonitor> _lazy = new(() => new AudioGlitchMonitor());
        public static AudioGlitchMonitor Instance => _lazy.Value;

        private const int MaxGlitches = 80;

        /// <summary>Piso de ruído: salto de posição abaixo disto (ms) não vira evento, só contador.</summary>
        private const double SmallJumpMs = 5;

        /// <summary>
        /// Intervalo entre leituras do buffer, em ms. 10 ms acompanha o período do motor de áudio
        /// com folga e custa quase nada. Aumentar só faz sentido em máquina muito fraca —
        /// e aumenta o risco de o PRÓPRIO Kit atrasar a leitura (o evento então sai como
        /// suspeito, nunca como glitch confirmado).
        /// </summary>
        public int PollIntervalMs { get; set; } = 10;

        // ────────────────────────────────────────────────────────────────────
        //  RECUPERAÇÃO AUTOMÁTICA (o "desligar/ligar o som" que o usuário fazia à mão)
        //
        //  O usuário provou que desligar/ligar a saída de áudio zera os estalos — é o
        //  reset do motor de áudio (audiodg). O Kit pode fazer isso sozinho, de forma
        //  imperceptível: congela o motor por ~300 ms e retoma (sincroniza de volta).
        //
        //  DUAS REGRAS DE OURO:
        //  1. O reset do Kit PROVOCA uma descontinuidade real no stream. Sem marcação,
        //     o Kit "se recuperaria" em loop do próprio reset. Todo evento dentro da
        //     janela de recuperação sai como PROVOCADO e NUNCA entra nas estatísticas
        //     reais, nos gatilhos nem no "pior caso".
        //  2. O gatilho é conservador: >= 2 estalos CONFIRMADOS e AUDÍVEIS em 90 s
        //     (um estalo isolado não justifica mexer), cooldown de 60 s e no máximo
        //     3 recuperações por hora rolando.
        // ────────────────────────────────────────────────────────────────────

        /// <summary>Liga/desliga a recuperação automática (o usuário decide no card de áudio).</summary>
        public bool AutoRecover { get; set; }

        /// <summary>Duração do congelamento do motor durante a recuperação (ms). 300 ms é imperceptível num reset.</summary>
        public int RecoverySuspendMs { get; set; } = 300;

        private long _provokedUntilMs;                       // até aqui, eventos = PROVOCADO
        private readonly List<DateTime> _recoveries = new(); // uma entrada por reset executado
        private string _lastRecoveryText = "";               // linha pronta para o card

        /// <summary>Disparado após cada recuperação executada (a GUI atualiza o card).</summary>
        public event Action<string>? RecoveryPerformed;

        /// <summary>Número de recuperações executadas nesta sessão.</summary>
        public int RecoveryCount { get { lock (_lock) return _recoveries.Count; } }

        /// <summary>Instantes das recuperações executadas (para o gatilho e para o relatório).</summary>
        public IReadOnlyList<DateTime> Recoveries { get { lock (_lock) return _recoveries.ToList(); } }

        /// <summary>Linha de estado da recuperação para o card (vazia = nenhuma ainda).</summary>
        public string LastRecoveryText { get { lock (_lock) return _lastRecoveryText; } }

        /// <summary>
        /// TABELA DE DECISÃO da recuperação — pura, sem estado, provada por asserção.
        /// Conservadora de propósito: é melhor NÃO recuperar do que resetar à toa.
        /// </summary>
        public static (bool Should, string Reason) ShouldAutoRecover(
            IReadOnlyList<DateTime> confirmedGlitchAt, DateTime now,
            DateTime lastRecoveryAt, int recoveriesLastHour, int provokedEventsPending)
        {
            if (provokedEventsPending > 0)
                return (false, "há um reset do próprio Kit em andamento — aguardando o motor voltar");
            if (recoveriesLastHour >= 3)
                return (false, "limite de 3 recuperações por hora atingido — resetar repetidamente não resolve se a causa persiste");
            if (lastRecoveryAt != default && (now - lastRecoveryAt).TotalSeconds < 60)
                return (false, "cooldown de 60 s entre recuperações");
            int recent = confirmedGlitchAt.Count(t => (now - t).TotalSeconds <= 90);
            if (recent >= 2)
                return (true, $"{recent} estalos confirmados nos últimos 90 s");
            return (false, $"só {recent} estalo(s) confirmado(s) em 90 s — um estalo isolado não justifica resetar o motor");
        }

        // ────────────────────────────────────────────────────────────────────
        //  MODELOS
        // ────────────────────────────────────────────────────────────────────

        /// <summary>E/S de um processo no instante do glitch (injetado pela GUI — o Core não lê o snapshot do TMOG).</summary>
        public readonly record struct ProcIoRef(int Pid, string Name, double Bps, double Iops);

        /// <summary>Um stutter de áudio, com o retrato do sistema no mesmo instante.</summary>
        public sealed class AudioGlitchEvent
        {
            public DateTime At = DateTime.Now;
            public string Kind = "";            // CONFIRMADO | SINALIZADO | SUSPEITO | DESCARTAVEL | PROVOCADO
            public bool ProvokedByKit;          // o PRÓPRIO Kit causou isto (reset de recuperação) — nunca entra nas estatísticas reais
            public string KindDetail = "";      // explicação do nível de confiança
            public double LostMs;               // ms de áudio efetivamente perdidos (0 = não medível)
            public ulong LostFrames;
            public bool EngineFlagged;          // o Windows levantou a flag de descontinuidade
            public bool SelfStarvation;         // o Kit também foi atrasado → atribuição incerta
            public bool StreamRestart;          // logo no início/troca de stream → provável artefato de arranque
            /// <summary>
            /// Quanto tempo o motor ficou SEM ENTREGAR áudio, em ms. É a duração da travada
            /// quando o dispositivo congela: nesse caso a posição não salta (os quadros não
            /// existem e não são contados), então o salto não mede nada — o que mede é o
            /// intervalo entre entregas.
            /// </summary>
            public double InterruptionMs;
            public bool Audible;                // havia som de verdade tocando
            public double PeakBefore;           // nível de pico antes do glitch (0..1)
            public double PeakAfter;
            public string DeviceName = "";
            public string AudioApps = "";       // "Chrome (PID 1234), Spotify (PID 5678)"

            // Retrato do sistema no mesmo instante
            public string DiskLabel = "";
            public double DiskActivityPct;
            public double DiskQueue;
            public double DiskLatencyMs;
            public double DiskBps;
            public string DiskMedia = "";
            public List<ProcIoRef> TopIo = new();
            public double CpuPct;
            public double DpcPercent;
            public string TopDriver = "";
            public double HardFaultsPerSec;
            public long SystemStallMs;          // pior stall do loop do Kit/Monitor no momento
            public string RamText = "";

            /// <summary>Texto curto para a tabela (o que aconteceu).</summary>
            public string Titulo
            {
                get
                {
                    // Sem salto de posição o motor sinalizou, mas o TAMANHO da perda por "quadros
                    // que faltaram" não é medível — dizer "0 ms" seria enganoso. Aí usamos a medida
                    // que existe: quanto tempo o motor ficou sem entregar dados.
                    string dur = LostMs >= 1
                        ? $"{LostMs:F0} ms de áudio perdidos"
                        : InterruptionMs >= 20 ? $"motor de áudio parado por {InterruptionMs:F0} ms"
                        : EngineFlagged ? "descontinuidade no buffer (duração não medível)" : "salto no stream";
                    return Audible ? $"{dur} — som estava tocando" : $"{dur} — em silêncio";
                }
            }

            /// <summary>Hipótese de causa, em português, a partir do que foi medido.</summary>
            public string Hipotese
            {
                get
                {
                    var sb = new StringBuilder();
                    if (SelfStarvation)
                        sb.Append("O próprio Kit ficou sem CPU nesse instante, então a leitura pode ter sido " +
                                  "prejudicada pela mesma travada — trata como indício, não como prova. ");
                    if (DiskActivityPct >= 85 || DiskQueue >= 4 || DiskLatencyMs >= 25)
                    {
                        sb.Append($"O disco {DiskLabel} estava saturado ({DiskActivityPct:F0}% de atividade, " +
                                  $"fila {DiskQueue:F1}, latência {DiskLatencyMs:F1} ms)");
                        if (TopIo.Count > 0)
                            sb.Append($", com {TopIo[0].Name} no topo do E/S ({FormatBps(TopIo[0].Bps)})");
                        sb.Append(". Isto é compatível com o áudio ter estalado por espera de disco.");
                    }
                    else if (DpcPercent >= 8)
                    {
                        sb.Append($"Não havia disco saturado, mas os drivers consumiam {DpcPercent:F1}% de um núcleo em DPC");
                        if (TopDriver.Length > 0) sb.Append($" (maior responsável: {TopDriver})");
                        sb.Append(". Travadas de áudio assim costumam vir de driver/DPC, não de programa.");
                    }
                    else if (HardFaultsPerSec >= 300)
                    {
                        sb.Append($"A paginação estava pesada ({HardFaultsPerSec:F0} falhas de disco por segundo): " +
                                  "faltou RAM e o Windows foi buscar dados no disco, congelando quem pediu — inclusive o áudio.");
                    }
                    else if (CpuPct >= 85)
                    {
                        sb.Append($"O processador estava em {CpuPct:F0}%: com a CPU esgotada, o motor de áudio pode " +
                                  "não receber tempo de execução na hora certa.");
                    }
                    else
                    {
                        sb.Append("Nenhum fator do Kit explica este glitch: disco, CPU, DPC e paginação estavam calmos. " +
                                  "Sobra a cadeia de áudio (driver do dispositivo, USB, cabo, modo exclusivo) — " +
                                  "ou um pico curto demais para aparecer na média de 1 s.");
                    }
                    return sb.ToString();
                }
            }            public string ToLine()
                => $"[{At:HH:mm:ss}] {Kind,-11} perdidos={LostMs,7:F0}ms  parada={InterruptionMs,7:F0}ms  " +
                   $"audivel={(Audible ? "sim" : "nao ")}  disco={DiskActivityPct,5:F0}% fila={DiskQueue,5:F1} " +
                   $"lat={DiskLatencyMs,6:F1}ms cpu={CpuPct,5:F0}% dpc={DpcPercent,5:F1}%" +
                   (SelfStarvation ? "  [KIT ATRASADO]" : "") + (StreamRestart ? "  [ARRANQUE DE STREAM]" : "");
        }

        // ────────────────────────────────────────────────────────────────────
        //  ESTADO
        // ────────────────────────────────────────────────────────────────────
        private readonly object _lock = new();
        private Thread? _thread;
        private volatile bool _run;
        private readonly List<AudioGlitchEvent> _glitches = new();

        private string _status = "parado";
        private string _deviceName = "";
        private bool _audioActive;
        private double _lastPeak;
        private int _packets;
        private long _polls;
        private double _pollsPerSec;
        private double _ownWorstGapMs;
        private double _ownGapMs;
        private string _lastError = "";
        private int _sessionRefreshTicks;
        private List<string> _audioApps = new();
        private long _smallJumps;
        private long _streamRestarts;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastPacketTicks;
        private bool _backlogPending;
        private long _lateEpisodeUntilMs;

        /// <summary>Provedor do topo de E/S por processo (a GUI injeta o snapshot do TMOG). Preenche o "quem".</summary>
        public Func<List<ProcIoRef>>? IoSnapshotProvider { get; set; }

        /// <summary>Disparado a cada glitch novo (a GUI atualiza a tela e pode mostrar alerta).</summary>
        public event Action<AudioGlitchEvent>? GlitchDetected;

        public bool IsRunning => _run;
        public string StatusText { get { lock (_lock) return _status; } }
        public string DeviceName { get { lock (_lock) return _deviceName; } }
        public bool AudioActive { get { lock (_lock) return _audioActive; } }
        public double LastPeak { get { lock (_lock) return _lastPeak; } }
        public int PacketCount { get { lock (_lock) return _packets; } }
        public long PollCount { get { lock (_lock) return _polls; } }
        public double PollsPerSec { get { lock (_lock) return _pollsPerSec; } }
        public double OwnWorstGapMs { get { lock (_lock) return _ownWorstGapMs; } }
        public long SmallJumpCount { get { lock (_lock) return _smallJumps; } }
        public long StreamRestartCount { get { lock (_lock) return _streamRestarts; } }
        public double OwnGapMs { get { lock (_lock) return _ownGapMs; } }
        public string LastError { get { lock (_lock) return _lastError; } }
        public IReadOnlyList<string> AudioApps { get { lock (_lock) return _audioApps.ToList(); } }
        public IReadOnlyList<AudioGlitchEvent> Glitches { get { lock (_lock) return _glitches.ToList(); } }

        // ── EXECUÇÃO da recuperação ──────────────────────────────────────
        // Usa NtSuspend/NtResume DIRETOS (sem passar por StorageDiagnostics.SuspendProcess,
        // que bloqueia audiodg como crítico). É deliberado: congelar o motor de áudio por
        // ~300 ms é exatamente o "desligar/ligar" que o usuário já fazia — e a retomada
        // acontece no MESMO handle no finally, então PID reciclado não tem vez: ou é o
        // processo que congelamos, ou o handle já não é dele.
        [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr h);
        [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);

        private int _recoveryCheckTicks;

        /// <summary>Avalia e, se o critério fechar, executa a recuperação. Chama ~1x/s no loop do monitor.</summary>
        private void MaybeAutoRecover()
        {
            if (!AutoRecover || !_run) return;

            List<DateTime> confirmedAt;
            DateTime lastRecovery;
            int lastHour, pending;
            lock (_lock)
            {
                confirmedAt = _glitches.Where(g => g.Kind == "CONFIRMADO" && !g.ProvokedByKit)
                                       .Select(g => g.At).ToList();
                lastRecovery = _recoveries.Count > 0 ? _recoveries[^1] : default;
                lastHour = _recoveries.Count(t => (DateTime.Now - t).TotalHours < 1);
                pending = _clock.ElapsedMilliseconds < _provokedUntilMs ? 1 : 0;
            }

            var (should, reason) = ShouldAutoRecover(confirmedAt, DateTime.Now, lastRecovery, lastHour, pending);
            if (!should) return;

            try
            {
                var audiodg = System.Diagnostics.Process.GetProcessesByName("audiodg").FirstOrDefault();
                if (audiodg == null)
                {
                    lock (_lock) _lastRecoveryText = "Recuperação não executada: motor de áudio (audiodg) não encontrado.";
                    return;
                }

                int pid = audiodg.Id;
                var id = StorageDiagnostics.GetIdentity(pid, "audiodg");
                var h = OpenProcess(StorageDiagnostics.PROCESS_SUSPEND_RESUME, false, pid);
                if (h == IntPtr.Zero)
                {
                    lock (_lock) _lastRecoveryText = "Recuperação não executada: sem acesso ao motor de áudio (rode o Kit como administrador).";
                    return;
                }

                try
                {
                    if (!StorageDiagnostics.SameProcess(id, out _))
                    {
                        lock (_lock) _lastRecoveryText = "Recuperação cancelada: o PID do motor trocou antes do reset (nada foi tocado).";
                        return;
                    }
                    int rc = NtSuspendProcess(h);
                    if (rc != 0)
                    {
                        lock (_lock) _lastRecoveryText = $"Recuperação não executada: o Windows recusou o reset (0x{rc:X8}).";
                        return;
                    }
                    try { Thread.Sleep(Math.Clamp(RecoverySuspendMs, 100, 2000)); }
                    finally { NtResumeProcess(h); }   // MESMO handle: retoma exatamente o que congelamos

                    long until = _clock.ElapsedMilliseconds + RecoverySuspendMs + 1500;
                    lock (_lock)
                    {
                        _provokedUntilMs = until;
                        _recoveries.Add(DateTime.Now);
                        if (_recoveries.Count > 20) _recoveries.RemoveAt(0);
                        _lastRecoveryText = $"{DateTime.Now:HH:mm:ss} — motor de áudio sincronizado de volta " +
                                            $"(motivo: {reason}). O estalo que vier agora foi o PRÓPRIO reset, não é problema novo.";
                    }
                    StorageDiagnostics.RecordIntervention("recuperar", "audiodg",
                        $"reset de ~{RecoverySuspendMs} ms para sincronizar o áudio (auto)", pid, reverted: true);
                    try { RecoveryPerformed?.Invoke(_lastRecoveryText); } catch { }
                }
                finally { CloseHandle(h); }
            }
            catch (Exception ex)
            {
                lock (_lock) _lastRecoveryText = "Falha na recuperação: " + ex.Message;
            }
        }

        /// <summary>Glitches que o usuário provavelmente OUVIU (motor sinalizou + som tocando + sem confusão do Kit).</summary>
        public int AudibleConfirmedCount
        {
            // Igual ao rótulo do evento: nada de contar como confirmado um indício descartado.
            // E nada de contar o que o PRÓPRIO Kit provocou no reset de recuperação.
            get { lock (_lock) return _glitches.Count(g => g.Kind == "CONFIRMADO" && !g.ProvokedByKit); }
        }

        public int GlitchCount {            get { lock (_lock) return _glitches.Count(g => !g.ProvokedByKit); } }

        // ────────────────────────────────────────────────────────────────────
        //  CONTROLE
        // ────────────────────────────────────────────────────────────────────
        public void Start()
        {
            lock (_lock)
            {
                if (_run) return;
                _run = true;
                _status = "iniciando…";
                _lastError = "";
                _thread = new Thread(Loop) { IsBackground = true, Name = "KitLugiaAudioGlitch" };
                try { _thread.Priority = ThreadPriority.AboveNormal; } catch { }
                _thread.Start();
            }
        }

        public void Stop()
        {
            lock (_lock) { _run = false; _status = "parado"; }
            try { _thread?.Join(2000); } catch { }
            _thread = null;
        }

        public void Clear()
        {
            _lastPacketTicks = 0;
            lock (_lock)
            {
                _glitches.Clear();
                _packets = 0;
                _polls = 0;
                _smallJumps = 0;
                _streamRestarts = 0;
                _ownWorstGapMs = 0;
                _lastError = "";
            }
        }

        // ────────────────────────────────────────────────────────────────────
        //  LOOP DE CAPTURA
        // ────────────────────────────────────────────────────────────────────
        private IntPtr _mmEnum, _device, _client, _capture;
        private int _sampleRate = 48000;
        private int _blockAlign = 4;
        private bool _isFloat;
        private ulong _lastDevPos;
        private bool _hasLastPos;
        private double _peakWindow;             // nível de pico nos últimos ~500 ms
        private int _packetsInWindow;
        private readonly Stopwatch _sinceLastPeakReset = Stopwatch.StartNew();

        private void Loop()
        {
            IntPtr mmcss = IntPtr.Zero;
            uint mmcssTask = 0;
            bool comInit = false;
            try
            {
                // MMCSS "Pro Audio": a mesma classe de thread que players usam para não
                // serem interrompidos. Sem isso, um stall do sistema atrasaria o NOSSO
                // leitor e nós mesmos criaríamos a descontinuidade que queremos medir.
                try { mmcss = AvSetMmThreadCharacteristicsW("Pro Audio", out mmcssTask); } catch { }

                try { comInit = CoInitializeEx(IntPtr.Zero, 0) >= 0; } catch { }

                if (!OpenStream(out string err))
                {
                    lock (_lock) { _status = "sem áudio: " + err; _lastError = err; }
                    Stop_Internal();
                    return;
                }

                var sw = Stopwatch.StartNew();
                long lastPollTicks = sw.ElapsedTicks;
                long rateFreq = Stopwatch.Frequency;
                int pollsThisTick = 0;
                long oneSecTicks = rateFreq;
                long tickStart = lastPollTicks;

                while (_run)
                {
                    long now = sw.ElapsedTicks;
                    double gapMs = (now - lastPollTicks) * 1000.0 / rateFreq;
                    lastPollTicks = now;
                    lock (_lock)
                    {
                        _ownGapMs = gapMs;
                        if (gapMs > _ownWorstGapMs) _ownWorstGapMs = gapMs;
                    }

                    DrainPackets(gapMs);

                    // Recuperação automática: avaliada ~1x/segundo (não a cada leitura).
                    if (++_recoveryCheckTicks >= 100)
                    {
                        _recoveryCheckTicks = 0;
                        MaybeAutoRecover();
                    }

                    pollsThisTick++;
                    if (now - tickStart >= oneSecTicks)
                    {
                        lock (_lock) { _polls += pollsThisTick; _pollsPerSec = pollsThisTick; }
                        pollsThisTick = 0;
                        tickStart = now;
                    }

                    // Apps com sessão de áudio ativa: recalcula a cada ~2 s (não a cada 10 ms).
                    if (--_sessionRefreshTicks <= 0)
                    {
                        _sessionRefreshTicks = 200;
                        RefreshAudioApps();
                    }

                    Thread.Sleep(Math.Clamp(PollIntervalMs, 5, 1000));
                }
            }
            catch (Exception ex)
            {
                lock (_lock) { _lastError = ex.Message; _status = "erro: " + ex.Message; }
            }
            finally
            {
                CloseStream();
                if (mmcss != IntPtr.Zero) { try { AvRevertMmThreadCharacteristics(mmcss); } catch { } }
                if (comInit) { try { CoUninitialize(); } catch { } }
                lock (_lock) { if (_run) _status = "parado"; _run = false; }
            }
        }

        private void Stop_Internal()
        {
            lock (_lock) { _run = false; }
        }

        /// <summary>Lê todos os pacotes disponíveis de uma vez — o motor gera ~1 a cada período do buffer.</summary>
        private void DrainPackets(double ownGapMs)
        {
            if (_capture == IntPtr.Zero) return;

            // ── CONTEXTO DESTA LEVA DE PACOTES ────────────────────────────────
            // Estes dois sinais valem para TODOS os pacotes desta leva e são a base da
            // honestidade do diagnóstico:
            //
            //  lateRead  — o loop demorou para voltar aqui (ownGapMs alto). Se o motor
            //              encheu o buffer enquanto estávamos fora, a descontinuidade
            //              pode ter sido criada pelo NOSSO atraso, não pelo áudio. Antes
            //              eu calculava isso por pacote usando o gap do instante atual — o
            //              que deixava os pacotes do meio de uma leva atrasada saírem como
            //              "CONFIRMADO". O teste com o leitor deliberadamente parado
            //              (892 ms) pegou exatamente esse erro.
            //              O sinal tem de sobreviver às levas seguintes: depois de uma leitura
            //              atrasada, a leva de "recuperação" sai poucos ms depois com o gap
            //              normal do loop, mas carregando a posição que saltou durante o atraso
            //              (no teste: 778 ms de salto com 11 ms de intervalo). Sem um episódio
            //              que dure um pouco, esse primeiro pacote volta a ser acusado como
            //              glitch confirmado — foi o que o harness pegou na 2ª rodada.
            //              A janela do episódio se adapta ao intervalo configurado.
            //  restart   — o stream começou/trocou: é a PRIMEIRA leva, a posição voltou atrás
            //              ou não havia áudio tocando antes desta leva. Nesses casos o motor
            //              costuma sinalizar descontinuidade no arranque do dispositivo sem
            //              que o usuário tenha ouvido nada.
            long nowTicks = _clock.ElapsedTicks;
            double sinceLastPacketMs = _lastPacketTicks > 0
                ? (nowTicks - _lastPacketTicks) * 1000.0 / Stopwatch.Frequency
                : 0;

            bool lateRead = ownGapMs >= 60 || _backlogPending || _clock.ElapsedMilliseconds < _lateEpisodeUntilMs;
            _backlogPending = false;
            if (ownGapMs >= 60)
                _lateEpisodeUntilMs = _clock.ElapsedMilliseconds + Math.Max(250, PollIntervalMs * 2 + 100);
            bool wasActive = _audioActive;
            bool streamRestart = !_hasLastPos || !wasActive;
            bool anyPacket = false;
            bool queueEmpty = false;
            int guard = 0;
            while (guard++ < 64)
            {
                int nextHr = CaptureNextPacket(_capture, out uint pending);
                if (nextHr < 0 || pending == 0) { queueEmpty = true; break; }

                int hr = CaptureGetBuffer(_capture, out IntPtr data, out uint frames, out uint flags,
                                          out ulong devPos, out ulong qpcPos);
                if (hr == AUDCLNT_S_BUFFER_EMPTY) { queueEmpty = true; break; }
                if (hr < 0)
                {
                    // Dispositivo trocado/removido (plugou fone, driver reiniciou): reconecta.
                    queueEmpty = true;
                    Reopen();
                    return;
                }
                if (frames == 0) { CaptureReleaseBuffer(_capture, 0); continue; }
                anyPacket = true;

                // Posição voltou atrás = o stream recomeçou (cada stream tem a própria
                // contagem de quadros). Isso nunca é perda de áudio; é troca de linha do tempo.
                if (_hasLastPos && devPos < _lastDevPos)
                {
                    _streamRestarts++;
                    _hasLastPos = false;
                    streamRestart = true;
                }

                double peak = 0;
                bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                if (!silent && data != IntPtr.Zero) peak = ComputePeak(data, frames);

                double gapMs = 0;
                ulong lostFrames = 0;
                if (_hasLastPos)
                {
                    // pu64DevicePosition é a posição do PRIMEIRO quadro deste pacote. Sem
                    // perda, ele é exatamente onde o pacote anterior terminou (lastDevPos).
                    // Qualquer avanço além disso são quadros que não existem no stream —
                    // o "pedaço" que faltou do áudio. O salto JÁ É a perda: não se subtrai
                    // o tamanho do pacote (era um bug na primeira versão deste cálculo).
                    if (devPos > _lastDevPos)
                    {
                        ulong lost = devPos - _lastDevPos;
                        if (lost >= 2)   // <2 quadros = arredondamento do motor, não é glitch
                        {
                            lostFrames = lost;
                            gapMs = lost * 1000.0 / _sampleRate;
                            if (gapMs < SmallJumpMs) _smallJumps++;
                        }
                    }
                }
                _lastDevPos = devPos + frames;
                _hasLastPos = true;

                bool flagged = (flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0;
                bool tsError = (flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) != 0;

                lock (_lock)
                {
                    _packets++;
                    _peakWindow = Math.Max(_peakWindow, peak);
                    _packetsInWindow++;
                    if (_sinceLastPeakReset.ElapsedMilliseconds >= 500)
                    {
                        // SEM pacote nenhum na janela não há som: o loopback simplesmente para
                        // de entregar dados quando nada toca. Sem este decaimento o último
                        // pico lido ficava "congelado" e o Kit dizia que havia áudio tocando
                        // para sempre (peguei isso no teste de volta ao silêncio).
                        if (_packetsInWindow > 0)
                        {
                            _lastPeak = _peakWindow;
                            _audioActive = _peakWindow > 0.0008;   // ~-62 dBFS: som de verdade, não ruído
                        }
                        else
                        {
                            _lastPeak = 0;
                            _audioActive = false;
                        }
                        _peakWindow = 0;
                        _packetsInWindow = 0;
                        _sinceLastPeakReset.Restart();
                    }
                }

                // O piso de SmallJumpMs evita transformar ruído de quantização do motor em
                // "glitch". Os saltos abaixo desse piso são contados (e aparecem no relatório)
                // em vez de virar evento — o usuário não precisa de alarme falso.
                // Salto sem flag e sem contexto de leva suspeita também não vira evento:
                // é a assinatura de troca de stream, não de perda de áudio.
                bool worthReporting = flagged || tsError || (gapMs >= SmallJumpMs && !lateRead && !streamRestart);
                if (worthReporting)
                    Report(flagged, tsError, gapMs, lostFrames, peak, lateRead, streamRestart,
                           _hasLastPos ? sinceLastPacketMs : 0);

                CaptureReleaseBuffer(_capture, frames);
            }

            if (anyPacket)
            {
                // Só avança o relógio de "último áudio visto" quando veio pacote. É isso que
                // faz o intervalo medir de verdade uma travada do motor (motor parado = nenhum
                // pacote por centenas de ms) em vez de ser zerado em cada volta do loop.
                _lastPacketTicks = nowTicks;
            }
            else if (sinceLastPacketMs > 1200)
            {
                // Nenhum pacote por mais de um segundo: nada está tocando, ou o stream morreu.
                lock (_lock) { _audioActive = false; _lastPeak = 0; }
            }

            // Sobrou backlog (a leva bateu no teto de 64 pacotes): a próxima leva também é
            // leitura atrasada, mesmo que o intervalo do loop pareça normal.
            if (!queueEmpty) _backlogPending = true;
        }

        /// <summary>
        /// TABELA DE DECISÃO — pura, sem estado, para poder ser provada por asserção.
        /// Separa o que é MEDIÇÃO (a flag oficial do motor) do que é INFERÊNCIA (salto de posição)
        /// e rebaixa para "suspeito" tudo que puder ter sido causado pelo próprio Kit chegar tarde.
        /// </summary>
        public static (string Kind, string Detail) Classify(bool engineFlagged, bool timestampError, bool selfStarved,
                                                           bool streamRestart, double gapMs, ulong lostFrames)
        {
            // "CONFIRMADO" é reservado ao único caso em que a afirmação tem base sólida:
            // o Windows levantou a flag E o Kit não tem como ter causado aquilo
            // (não chegou tarde para ler o buffer nem pegou início/troca de stream).
            if (engineFlagged && !selfStarved && !streamRestart)
                return ("CONFIRMADO",
                        "O motor de áudio do Windows sinalizou descontinuidade no buffer " +
                        "(AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) e o Kit não estava atrasado — " +
                        "o áudio realmente perdeu quadros aqui.");

            if (engineFlagged && (selfStarved || streamRestart))
                return ("SUSPEITO",
                        "O Windows sinalizou descontinuidade, MAS " +
                        (selfStarved
                            ? "o próprio Kit ficou sem CPU no mesmo instante (quem chegou tarde para ler o buffer pode ter causado a perda)"
                            : "o áudio acabou de começar ou de trocar de stream — nesse arranque o driver/roteador costuma sinalizar " +
                              "descontinuidade sem que o usuário ouça nada") +
                        ". Trata como indício, não como prova.");

            if (timestampError && !selfStarved && !streamRestart)
                return ("SINALIZADO",
                        "O stream reportou erro de marcação de tempo (AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) — " +
                        "o áudio estava sendo entregue fora do ritmo esperado.");

            return ("SUSPEITO",
                    $"A posição do stream saltou {lostFrames} quadros (~{gapMs:F0} ms) sem a flag oficial " +
                    (selfStarved
                        ? "e com o Kit atrasado: muito provavelmente a leitura é que se atrasou."
                        : streamRestart
                            ? "e logo no início/troca de stream: provavelmente é o arranque do dispositivo, não uma perda audível."
                            : "do Windows. Isto costuma indicar perda real, mas não é confirmado pelo motor."));
        }

        private void Report(bool flagged, bool tsError, double gapMs, ulong lostFrames, double peak,
                            bool selfStarved, bool streamRestart, double interruptionMs)
        {
            // Janela de recuperação: o reset do próprio Kit cria uma descontinuidade REAL
            // no stream. Sem esta marcação o Kit interpretaria o próprio reset como "mais
            // um estalo", re-gatilharia a recuperação e entraria em loop infinito.
            bool provoked = false;
            lock (_lock)
            {
                if (_clock.ElapsedMilliseconds < _provokedUntilMs) provoked = true;
            }

            var (kind, kindDetail) = Classify(flagged, tsError, selfStarved, streamRestart, gapMs, lostFrames);

            var ev = new AudioGlitchEvent
            {
                Kind = kind,
                KindDetail = kindDetail,
                LostMs = Math.Round(gapMs, 1),
                LostFrames = lostFrames,
                InterruptionMs = Math.Round(Math.Min(interruptionMs, 60000), 0),
                EngineFlagged = flagged,
                SelfStarvation = selfStarved,
                StreamRestart = streamRestart,
                PeakBefore = Math.Round(peak, 4),
                Audible = peak > 0.0008 || _audioActive,
                DeviceName = _deviceName,
                ProvokedByKit = provoked,
            };
            if (provoked)
            {
                ev.Kind = "PROVOCADO";
                ev.KindDetail = "Evento criado pela própria recuperação do Kit (reset de ~" + RecoverySuspendMs +
                                " ms do motor de áudio para sincronizar de volta). NÃO conta como estalo real.";
            }
            lock (_lock) { ev.AudioApps = string.Join(", ", _audioApps); }

            Enrich(ev);

            lock (_lock)
            {
                if (_glitches.Count >= MaxGlitches) _glitches.RemoveAt(0);
                _glitches.Add(ev);
            }
            try { GlitchDetected?.Invoke(ev); } catch { }
        }

        /// <summary>Foto do resto do sistema no instante do glitch — é o que dá sentido ao evento.</summary>
        private void Enrich(AudioGlitchEvent ev)
        {
            try
            {
                var snap = StorageDiagnostics.Sample();
                var focus = snap.Disks.Where(d => d.Available)
                                      .OrderByDescending(d => d.ActivityPct).ThenByDescending(d => d.Queue)
                                      .FirstOrDefault();
                if (focus != null)
                {
                    ev.DiskLabel = focus.Display;
                    ev.DiskActivityPct = Math.Round(focus.ActivityPct, 1);
                    ev.DiskQueue = Math.Round(focus.Queue, 2);
                    ev.DiskLatencyMs = Math.Round(focus.LatencyMs, 1);
                    ev.DiskBps = focus.TotalBps;
                    ev.DiskMedia = focus.Device?.Media ?? "";
                }
            }
            catch { }

            try
            {
                var io = IoSnapshotProvider?.Invoke();
                if (io != null && io.Count > 0)
                    ev.TopIo = io.OrderByDescending(x => x.Bps).Take(3).ToList();
            }
            catch { }

            try
            {
                var ls = LatencyMonitor.Instance.GetSnapshot();
                ev.DpcPercent = Math.Round(ls.MaxCoreDpcPercent, 1);
                ev.TopDriver = ls.WorstDriver ?? "";
                ev.HardFaultsPerSec = Math.Round(ls.HardFaultsPerSec, 0);
                ev.SystemStallMs = ls.MaxTickMs;
                ev.CpuPct = Math.Round(NativeMetricsHelper.GetSystemCpuPercent(), 0);
                double availMb = NativeMetricsHelper.GetAvailableRamMb();
                ev.RamText = availMb >= 0
                    ? $"{NativeMetricsHelper.GetMemoryUsagePercent():F0}% em uso ({availMb:F0} MB disponíveis)"
                    : $"{NativeMetricsHelper.GetMemoryUsagePercent():F0}% em uso";
            }
            catch { }

            ev.PeakAfter = 0;
        }

        // ────────────────────────────────────────────────────────────────────
        //  APPS COM ÁUDIO ATIVO (quem estava tocando)
        // ────────────────────────────────────────────────────────────────────
        private void RefreshAudioApps()
        {
            var list = new List<string>();
            IntPtr mgr = IntPtr.Zero, en = IntPtr.Zero;
            try
            {
                if (_device == IntPtr.Zero) return;
                var iid = IID_IAudioSessionManager2;
                if (DeviceActivate(_device, ref iid, 1 /*CLSCTX_INPROC_SERVER*/, IntPtr.Zero, out mgr) < 0) return;
                if (SessionManagerGetEnumerator(mgr, out en) < 0) return;
                if (SessionEnumeratorGetCount(en, out int count) < 0) return;

                for (int i = 0; i < count && i < 24; i++)
                {
                    IntPtr ctl = IntPtr.Zero;
                    try
                    {
                        if (SessionEnumeratorGetSession(en, i, out ctl) < 0) continue;
                        if (SessionControl2GetState(ctl, out int state) < 0) continue;
                        if (state != 1 /*Active*/) continue;                   // só quem está realmente tocando
                        if (SessionControl2GetProcessId(ctl, out uint pid) < 0) continue;
                        if (pid == 0) continue;

                        string name;
                        try { name = Process.GetProcessById((int)pid).ProcessName; }
                        catch { name = "processo " + pid; }
                        list.Add($"{name} (PID {pid})");
                    }
                    catch { }
                    finally { if (ctl != IntPtr.Zero) Release(ctl); }
                }
            }
            catch { }
            finally
            {
                if (en != IntPtr.Zero) Release(en);
                if (mgr != IntPtr.Zero) Release(mgr);
            }

            lock (_lock) { _audioApps = list.Distinct().Take(6).ToList(); }
        }

        // ────────────────────────────────────────────────────────────────────
        //  ABRIR / FECHAR O STREAM
        // ────────────────────────────────────────────────────────────────────
        private bool OpenStream(out string error)
        {
            error = "";
            try
            {
                var clsid = CLSID_MMDeviceEnumerator;
                var iid = IID_IMMDeviceEnumerator;
                int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1 /*CLSCTX_INPROC_SERVER*/, ref iid, out _mmEnum);
                if (hr < 0 || _mmEnum == IntPtr.Zero) { error = "não foi possível falar com o serviço de áudio (0x" + hr.ToString("X8") + ")"; return false; }

                // eRender = 0, eConsole = 0 → dispositivo de saída padrão (os alto-falantes)
                hr = EnumGetDefaultEndpoint(_mmEnum, 0, 0, out _device);
                if (hr < 0 || _device == IntPtr.Zero)
                {
                    error = "nenhum dispositivo de saída de áudio encontrado (ou desativado)";
                    return false;
                }

                var iidClient = IID_IAudioClient;
                hr = DeviceActivate(_device, ref iidClient, 1, IntPtr.Zero, out _client);
                if (hr < 0 || _client == IntPtr.Zero) { error = "o Windows não liberou o cliente de áudio (0x" + hr.ToString("X8") + ")"; return false; }

                if (ClientGetMixFormat(_client, out IntPtr fmt) < 0 || fmt == IntPtr.Zero)
                {
                    error = "não foi possível ler o formato do dispositivo";
                    return false;
                }
                ReadFormat(fmt);

                // hnsBufferDuration = 0 (deixa o motor escolher), periodicity = 0 (shared),
                // flags = LOOPBACK: capturamos o que o dispositivo está TOCANDO.
                // O ponteiro do formato precisa continuar válido DURANTE o Initialize —
                // liberar antes fazia o Windows recusar a inicialização com formato inválido.
                try
                {
                    hr = ClientInitialize(_client, 0 /*SHARED*/, AUDCLNT_STREAMFLAGS_LOOPBACK, 0, 0, fmt, IntPtr.Zero);
                }
                finally { try { Marshal.FreeCoTaskMem(fmt); } catch { } }
                if (hr < 0)
                {
                    error = "o dispositivo não aceitou captura em loopback (0x" + hr.ToString("X8") + ")";
                    return false;
                }

                var iidCap = IID_IAudioCaptureClient;
                hr = ClientGetService(_client, ref iidCap, out _capture);
                if (hr < 0 || _capture == IntPtr.Zero) { error = "o dispositivo não forneceu o cliente de captura"; return false; }

                hr = ClientStart(_client);
                if (hr < 0) { error = "o stream de captura não iniciou (0x" + hr.ToString("X8") + ")"; return false; }

                _deviceName = ReadFriendlyName(_device);
                lock (_lock)
                {
                    _status = _deviceName.Length > 0
                        ? "escutando o áudio — " + _deviceName
                        : "escutando o áudio do dispositivo de saída padrão";
                    _lastError = "";
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Reabre o stream (o formato mudou, plugaram fone, o driver reiniciou…).</summary>
        private void Reopen()
        {
            CloseStream();
            _hasLastPos = false;
            Thread.Sleep(400);
            if (!OpenStream(out string err))
                lock (_lock) { _status = "áudio reconectando… (" + err + ")"; _lastError = err; }
        }

        private void CloseStream()
        {
            try { if (_client != IntPtr.Zero) ClientStop(_client); } catch { }
            if (_capture != IntPtr.Zero) { Release(_capture); _capture = IntPtr.Zero; }
            if (_client != IntPtr.Zero) { Release(_client); _client = IntPtr.Zero; }
            if (_device != IntPtr.Zero) { Release(_device); _device = IntPtr.Zero; }
            if (_mmEnum != IntPtr.Zero) { Release(_mmEnum); _mmEnum = IntPtr.Zero; }
            _hasLastPos = false;
        }

        private void ReadFormat(IntPtr fmt)
        {
            int tag = Marshal.ReadInt16(fmt, 0);
            int ch = Marshal.ReadInt16(fmt, 2);
            int rate = Marshal.ReadInt32(fmt, 4);
            int align = Marshal.ReadInt16(fmt, 12);
            int bits = Marshal.ReadInt16(fmt, 14);
            _sampleRate = rate > 0 ? rate : 48000;
            _blockAlign = align > 0 ? align : Math.Max(2, ch * (bits > 0 ? bits / 8 : 2));

            _isFloat = false;
            if (tag == 3 /*IEEE_FLOAT*/) _isFloat = true;
            else if (tag == 0xFFFE /*EXTENSIBLE*/)
            {
                // WAVEFORMATEXTENSIBLE: SubFormat (GUID) logo após cbSize/Samples/channelMask.
                try
                {
                    var g = new Guid(
                        Marshal.ReadInt32(fmt, 24), (short)Marshal.ReadInt16(fmt, 28), (short)Marshal.ReadInt16(fmt, 30),
                        (byte)Marshal.ReadByte(fmt, 32), (byte)Marshal.ReadByte(fmt, 33), (byte)Marshal.ReadByte(fmt, 34), (byte)Marshal.ReadByte(fmt, 35),
                        (byte)Marshal.ReadByte(fmt, 36), (byte)Marshal.ReadByte(fmt, 37), (byte)Marshal.ReadByte(fmt, 38), (byte)Marshal.ReadByte(fmt, 39));
                    _isFloat = g == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
                }
                catch { }
            }
        }

        /// <summary>Nível de pico do pacote (0..1). Só serve para saber se havia SOM de verdade.</summary>
        private double ComputePeak(IntPtr data, uint frames)
        {
            try
            {
                int n = (int)Math.Min((ulong)frames * (ulong)_blockAlign, 1u << 20);
                if (n <= 0) return 0;
                var buf = new byte[n];
                Marshal.Copy(data, buf, 0, n);
                double peak = 0;

                if (_isFloat)
                {
                    for (int i = 0; i + 4 <= n; i += 4)
                    {
                        float v = BitConverter.ToSingle(buf, i);
                        if (float.IsNaN(v) || float.IsInfinity(v)) continue;
                        double a = Math.Abs(v);
                        if (a > peak) peak = a;
                    }
                }
                else
                {
                    for (int i = 0; i + 2 <= n; i += 2)
                    {
                        short s = BitConverter.ToInt16(buf, i);
                        double a = Math.Abs(s / 32768.0);
                        if (a > peak) peak = a;
                    }
                }
                return Math.Min(1.0, peak);
            }
            catch { return 0; }
        }

        private string ReadFriendlyName(IntPtr device)
        {
            IntPtr store = IntPtr.Zero;
            try
            {
                if (DeviceOpenPropertyStore(device, 0 /*STGM_READ*/, out store) < 0 || store == IntPtr.Zero) return "";
                var key = PKEY_Device_FriendlyName;
                IntPtr pv = Marshal.AllocCoTaskMem(32);
                try
                {
                    for (int i = 0; i < 32; i++) Marshal.WriteByte(pv, i, 0);
                    if (PropStoreGetValue(store, ref key, pv) < 0) return "";
                    int vt = Marshal.ReadInt16(pv, 0);
                    if (vt != 31 /*VT_LPWSTR*/) return "";
                    IntPtr str = Marshal.ReadIntPtr(pv, 8);
                    string s = str != IntPtr.Zero ? Marshal.PtrToStringUni(str) ?? "" : "";
                    try { PropVariantClear(pv); } catch { }
                    return s;
                }
                finally { Marshal.FreeCoTaskMem(pv); }
            }
            catch { return ""; }
            finally { if (store != IntPtr.Zero) Release(store); }
        }

        // ────────────────────────────────────────────────────────────────────
        //  RELATÓRIOS
        // ────────────────────────────────────────────────────────────────────

        /// <summary>Conclusão didática (nível 0 ok, 1 atenção, 2 crítico) + texto em português.</summary>
        public (int Level, string Title, string Text) BuildConclusion()
        {
            List<AudioGlitchEvent> glitches;
            bool running;
            bool active;
            string status, device;
            int confirmed;
            lock (_lock)
            {
                // Eventos PROVOCADOS (o reset do próprio Kit) existem na lista para
                // transparência, mas NUNCA entram nas estatísticas e gatilhos reais.
                glitches = _glitches.Where(g => !g.ProvokedByKit).ToList();
                running = _run;
                active = _audioActive;
                status = _status;
                device = _deviceName;
                confirmed = _glitches.Count(g => g.Kind == "CONFIRMADO" && !g.ProvokedByKit);
            }

            if (!running)
                return (0, "Escuta de áudio desligada",
                        "Ligue a escuta do áudio para o Kit registrar quando o som estalar — com horário, duração, " +
                        "o que estava tocando e o que o sistema estava fazendo no mesmo instante.");

            if (glitches.Count == 0)
                return (0, "Áudio sem travadas",
                        $"Escutando {(device.Length > 0 ? device : "a saída de áudio padrão")} sem nenhuma descontinuidade " +
                        $"até agora. " + (active
                            ? "Há som tocando neste momento — deixe rodar enquanto o problema acontecer."
                            : "Nenhum som está tocando agora: o Kit só registra glitch audível quando há áudio de verdade."));

            // O "pior caso" prefere eventos confirmados com duração medida; só cai para os
            // demais (indício sem tamanho medível) se não houver nenhum confirmado.
            var confirmedList = glitches.Where(g => g.Kind == "CONFIRMADO").ToList();
            var worst = (confirmedList.Count > 0 ? confirmedList : glitches)
                        .OrderByDescending(g => Math.Max(g.LostMs, g.InterruptionMs)).First();
            bool disk = glitches.Any(g => g.DiskActivityPct >= 85 || g.DiskQueue >= 4 || g.DiskLatencyMs >= 25);
            bool dpc = glitches.Any(g => g.DpcPercent >= 8);
            bool ram = glitches.Any(g => g.HardFaultsPerSec >= 300);
            bool cpu = glitches.Any(g => g.CpuPct >= 85);
            bool unknown = glitches.All(g => g.DiskActivityPct < 85 && g.DiskQueue < 4 && g.DiskLatencyMs < 25
                                             && g.DpcPercent < 8 && g.HardFaultsPerSec < 300 && g.CpuPct < 85);

            string causes = "";
            if (disk) causes += "• DISCO — houve glitch com o armazenamento saturado (fila/latência altas). Use a aba Disco e o 'Testar impacto'.\n";
            if (dpc) causes += "• DRIVER/DPC — houve glitch com drivers consumindo tempo de CPU em rotina de kernel (aba Latência, tabela Drivers).\n";
            if (ram) causes += "• MEMÓRIA — houve glitch com paginação pesada: faltou RAM e o Windows foi ao disco (aba Resumo).\n";
            if (cpu) causes += "• CPU — houve glitch com o processador esgotado.\n";
            if (unknown) causes += "• NENHUM FATOR MEDIDO explica os glitches: disco, CPU, DPC e paginação estavam calmos. " +
                                   "Sobra a cadeia de áudio — driver do dispositivo, USB, modo exclusivo, fone/cabo.\n";

            int level = confirmed >= 3 || worst.LostMs >= 100 ? 2 : 1;
            string title = confirmed > 0
                ? $"{confirmed} travada(s) de áudio confirmada(s) pelo Windows"
                : $"{glitches.Count} descontinuidade(s) no stream de áudio";

            int restart = glitches.Count(g => g.StreamRestart);
            int suspect = glitches.Count(g => g.Kind == "SUSPEITO");
            var sb = new StringBuilder();
            sb.AppendLine($"O motor de áudio do Windows sinalizou {glitches.Count} descontinuidade(s) desde que a escuta começou " +
                          $"({confirmed} confirmada(s) pelo Windows, {suspect} indício(s) descartado(s) como prova).");
            // O pior caso mostra a medida que EXISTE: quadros perdidos quando o stream saltou,
            // ou o tempo em que o motor ficou parado quando ele congelou (aí não há salto).
            string worstDur = worst.LostMs >= 1
                ? $"{worst.LostMs:F0} ms de áudio perdidos"
                : worst.InterruptionMs >= 20 ? $"motor de áudio parado por {worst.InterruptionMs:F0} ms"
                : "descontinuidade sem duração medível";
            sb.AppendLine($"Pior caso: {worst.At:HH:mm:ss}, {worstDur} " +
                          $"({(worst.Audible ? "havia som tocando" : "estava em silêncio")}).");
            if (worst.DiskLabel.Length > 0)
                sb.AppendLine($"No momento do pior caso, o disco {worst.DiskLabel} estava em {worst.DiskActivityPct:F0}% de atividade, " +
                              $"fila {worst.DiskQueue:F1}, latência {worst.DiskLatencyMs:F1} ms.");
            if (worst.AudioApps.Length > 0) sb.AppendLine($"Tocando na hora: {worst.AudioApps}.");
            sb.AppendLine();
            sb.AppendLine("O que aparece junto com os glitches (hipóteses, não veredito):");
            sb.Append(causes.TrimEnd());
            return (level, title, sb.ToString());
        }

        public string BuildAiReport()
        {
            List<AudioGlitchEvent> all;
            lock (_lock) all = _glitches.ToList();
            // No relatório IA: EVENTOS REAIS e PROVOCADOS em seções SEPARADAS (o ChatGPT
            // pediu explicitamente essa separação — nenhum evento artificial entra nas
            // estatísticas de estalo real).
            var glitches = all.Where(g => !g.ProvokedByKit).ToList();
            var provoked = all.Where(g => g.ProvokedByKit).ToList();

            var (level, title, text) = BuildConclusion();
            var sb = new StringBuilder();
            sb.AppendLine("=== KitLugia — Travamentos de Áudio (glitch do motor WASAPI) ===");
            sb.AppendLine($"Gerado: {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
            sb.AppendLine($"Escuta ativa: {_run}   dispositivo: {(_deviceName.Length > 0 ? _deviceName : "(padrão)")}");
            sb.AppendLine($"Pacotes lidos: {_packets}   leituras: {_polls}   ritmo: {_pollsPerSec:F0}/s   " +
                          $"pior atraso do próprio Kit: {_ownWorstGapMs:F0} ms   " +
                          $"trocas/arranques de stream: {_streamRestarts}   " +
                          $"saltos abaixo do piso de {SmallJumpMs:F0}ms (ruído, não contam): {_smallJumps}");
            sb.AppendLine($"Som tocando agora: {_audioActive} (pico {_lastPeak:P1})   apps com sessão ativa: " +
                          $"{(_audioApps.Count > 0 ? string.Join(", ", _audioApps) : "nenhum")}");
            sb.AppendLine($"SO: {Environment.OSVersion.VersionString} | x64 | {Environment.ProcessorCount} núcleos lógicos");
            if (_lastError.Length > 0) sb.AppendLine($"Último erro: {_lastError}");
            if (glitches.Count == 0) sb.AppendLine("\nNenhuma descontinuidade registrada nesta sessão.");
            sb.AppendLine();

            sb.AppendLine($"--- CONCLUSÃO (nível {(level == 0 ? "OK" : level == 1 ? "ATENÇÃO" : "CRÍTICO")}) ---");
            sb.AppendLine(title);
            foreach (var line in text.Split('\n')) if (line.Trim().Length > 0) sb.AppendLine("  " + line.Trim());
            sb.AppendLine();

            if (glitches.Count > 0)
            {
                sb.AppendLine("--- GLITCHES (mais recente primeiro) ---");
                sb.AppendLine("  hora;tipo;ms_perdidos;ms_parada;quadros_perdidos;motor_sinalizou;kit_atrasado;arranque_stream;audivel;pico;disco;ativid%25;fila;latencia_ms;cpu%25;dpc%25;driver;hardfaults/s");
                foreach (var g in glitches.OrderByDescending(g => g.At))
                {
                    sb.AppendLine($"  {g.At:HH:mm:ss.fff};{g.Kind};{g.LostMs:F1};{g.InterruptionMs:F0};{g.LostFrames};{g.EngineFlagged};{g.SelfStarvation};{g.StreamRestart};" +
                                  $"{g.Audible};{g.PeakBefore:F4};{g.DiskLabel};{g.DiskActivityPct:F1};{g.DiskQueue:F2};{g.DiskLatencyMs:F1};" +
                                  $"{g.CpuPct:F0};{g.DpcPercent:F1};{g.TopDriver};{g.HardFaultsPerSec:F0}");
                }
                sb.AppendLine();

                sb.AppendLine("--- DETALHE DOS GLITCHES (o que estava acontecendo) ---");
                foreach (var g in glitches.OrderByDescending(g => g.At).Take(25))
                {
                    sb.AppendLine($"  [{g.At:dd/MM HH:mm:ss.fff}] {g.Kind} — {g.Titulo}");
                    sb.AppendLine($"    confiança: {g.KindDetail}");
                    if (g.DeviceName.Length > 0) sb.AppendLine($"    dispositivo: {g.DeviceName}");
                    if (g.AudioApps.Length > 0) sb.AppendLine($"    tocando: {g.AudioApps}");
                    if (g.TopIo.Count > 0)
                        sb.AppendLine("    top E/S no instante: " + string.Join(" | ",
                            g.TopIo.Select(t => $"{t.Name}(PID {t.Pid}) {FormatBps(t.Bps)}")));
                    if (g.TopDriver.Length > 0) sb.AppendLine($"    maior DPC: {g.TopDriver} ({g.DpcPercent:F1}% de um núcleo)");
                    if (g.RamText.Length > 0) sb.AppendLine($"    memória: {g.RamText}");
                    sb.AppendLine($"    hipótese: {g.Hipotese}");
                    sb.AppendLine();
                }
            }

            sb.AppendLine("--- METODOLOGIA (para a IA entender os números) ---");
            sb.AppendLine("  Glitch CONFIRMADO = o próprio motor de áudio do Windows levantou");
            sb.AppendLine("    AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY no pacote (API oficial de detecção de glitch, Win7+).");
            sb.AppendLine("  ms perdidos = quadros que faltaram na posição do dispositivo (pu64DevicePosition) ÷ sample rate.");
            sb.AppendLine("  ms parada = quanto tempo o motor ficou sem entregar áudio. Um motor CONGELADO não avança a posição,");
            sb.AppendLine("    então nesse caso a duração da travada só aparece aqui (ms perdidos fica 0 e isso é honesto, não um erro).");
            sb.AppendLine("  O stream é uma CAPTURA EM LOOPBACK do dispositivo de saída padrão (nada é gravado em disco).");
            sb.AppendLine("  'kit_atrasado' = o leitor do Kit também perdeu tempo no mesmo instante; nesse caso a descontinuidade");
            sb.AppendLine("    pode ter sido criada pelo próprio leitor e o evento NÃO deve ser tratado como prova.");
            sb.AppendLine("  audivel = havia sinal acima de ~-62 dBFS; glitch em silêncio não foi ouvido por ninguém.");
            sb.AppendLine("  As causas (disco/DPC/RAM/CPU) são HIPÓTESES: são medições do mesmo instante, não prova de causalidade.");
            sb.AppendLine("    Para transformar hipótese de disco em prova, use a aba Disco → 'Testar impacto'.");
            sb.AppendLine("  PROVOCADO = evento criado pela RECUPERAÇÃO do próprio Kit (reset de ~" + RecoverySuspendMs + " ms do motor");
            sb.AppendLine("    de áudio para sincronizar de volta). Nenhum evento artificial entra nas estatísticas acima.");

            if (provoked.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("--- EVENTOS PROVOCADOS PELO TESTE/RECUPERAÇÃO (não são estalos reais) ---");
                foreach (var g in provoked.OrderByDescending(g => g.At))
                    sb.AppendLine($"  [{g.At:dd/MM HH:mm:ss.fff}] reset do Kit — o motor voltou e sinalizou a descontinuidade esperada");
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("--- EVENTOS PROVOCADOS: nenhum (todas as entradas acima são eventos naturais) ---");
            }
            return sb.ToString();
        }

        private static string FormatBps(double bps)
            => bps >= 1024 * 1024 ? $"{bps / 1024 / 1024:F1} MB/s" : bps >= 1024 ? $"{bps / 1024:F0} KB/s" : $"{bps:F0} B/s";

        // ────────────────────────────────────────────────────────────────────
        //  INTEROP NATIVO (vtable explícita — sem surpresas de marshalling)
        //  Índices contam a partir de 0 já incluindo QueryInterface/AddRef/Release.
        // ────────────────────────────────────────────────────────────────────
        private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
        private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
        private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        private static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00AA00389B71");

        private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const uint AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY = 0x1;
        private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        private const uint AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR = 0x4;
        private const int AUDCLNT_S_BUFFER_EMPTY = unchecked((int)0x08890001);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY { public Guid FmtId; public uint Pid; }
        private static readonly PROPERTYKEY PKEY_Device_FriendlyName =
            new() { FmtId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Pid = 14 };

        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
        [DllImport("ole32.dll")] private static extern void CoUninitialize();
        [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, out IntPtr obj);
        [DllImport("ole32.dll")] private static extern int PropVariantClear(IntPtr pv);
        [DllImport("avrt.dll", CharSet = CharSet.Unicode)] private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, out uint taskIndex);
        [DllImport("avrt.dll")] private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

        private static IntPtr Vtbl(IntPtr pUnk, int slot) => Marshal.ReadIntPtr(Marshal.ReadIntPtr(pUnk, 0), slot * IntPtr.Size);

        private static int Release(IntPtr pUnk)
        {
            try
            {
                var f = Marshal.GetDelegateForFunctionPointer<D_Release>(Vtbl(pUnk, 2));
                return f(pUnk);
            }
            catch { return 0; }
        }

        // IMMDeviceEnumerator slot 4
        private delegate int D_EnumGetDefaultEndpoint(IntPtr self, int dataFlow, int role, out IntPtr device);
        private static int EnumGetDefaultEndpoint(IntPtr self, int flow, int role, out IntPtr device)
        {
            var f = Marshal.GetDelegateForFunctionPointer<D_EnumGetDefaultEndpoint>(Vtbl(self, 4));
            return f(self, flow, role, out device);
        }

        // IMMDevice slot 3: Activate(iid, ctx, PROPVARIANT*, void**)
        private delegate int D_Activate(IntPtr self, ref Guid iid, uint ctx, IntPtr props, out IntPtr itf);
        private static int DeviceActivate(IntPtr self, ref Guid iid, uint ctx, IntPtr props, out IntPtr itf)
        {
            var f = Marshal.GetDelegateForFunctionPointer<D_Activate>(Vtbl(self, 3));
            return f(self, ref iid, ctx, props, out itf);
        }

        // IMMDevice slot 4: OpenPropertyStore(access, IPropertyStore**)
        private delegate int D_OpenStore(IntPtr self, uint access, out IntPtr store);
        private static int DeviceOpenPropertyStore(IntPtr self, uint access, out IntPtr store)
        {
            var f = Marshal.GetDelegateForFunctionPointer<D_OpenStore>(Vtbl(self, 4));
            return f(self, access, out store);
        }

        // IPropertyStore slot 5: GetValue(REFPROPERTYKEY, PROPVARIANT*)
        private delegate int D_StoreGetValue(IntPtr self, ref PROPERTYKEY key, IntPtr value);
        private static int PropStoreGetValue(IntPtr self, ref PROPERTYKEY key, IntPtr value)
        {
            var f = Marshal.GetDelegateForFunctionPointer<D_StoreGetValue>(Vtbl(self, 5));
            return f(self, ref key, value);
        }

        // IAudioClient slot 3: Initialize(shareMode, flags, bufDur, periodicity, format, sessionGuid)
        private delegate int D_ClientInitialize(IntPtr self, int shareMode, uint flags, long bufDur, long periodicity, IntPtr fmt2, IntPtr sessionGuid);
        private static int ClientInitialize(IntPtr self, int shareMode, uint flags, long bufDur, long periodicity, IntPtr fmt2, IntPtr sessionGuid)
        {
            var f = Marshal.GetDelegateForFunctionPointer<D_ClientInitialize>(Vtbl(self, 3));
            return f(self, shareMode, flags, bufDur, periodicity, fmt2, sessionGuid);
        }

        // IAudioClient slot 8: GetMixFormat(WAVEFORMATEX**)
        private delegate int D_GetMixFormat(IntPtr self, out IntPtr fmt);
        private static int ClientGetMixFormat(IntPtr self, out IntPtr fmt)
        {
            var f = Marshal.GetDelegateForFunctionPointer<D_GetMixFormat>(Vtbl(self, 8));
            return f(self, out fmt);
        }

        // IAudioClient slot 10 / 11
        private delegate int D_NoArgs(IntPtr self);
        private static int ClientStart(IntPtr self)
        {
            var f = Marshal.GetDelegateForFunctionPointer<D_NoArgs>(Vtbl(self, 10));
            return f(self);
        }
        private static int ClientStop(IntPtr self)
        {
            var f = Marshal.GetDelegateForFunctionPointer<D_NoArgs>(Vtbl(self, 11));
            return f(self);
        }

        // IAudioClient slot 14: GetService(REFIID, void**)
        private delegate int D_GetService(IntPtr self, ref Guid iid, out IntPtr svc);
        private static int ClientGetService(IntPtr self, ref Guid iid, out IntPtr svc)
        {
            var f = Marshal.GetDelegateForFunctionPointer<D_GetService>(Vtbl(self, 14));
            return f(self, ref iid, out svc);
        }

        // IAudioCaptureClient slots 3/4/5
        private delegate int D_CaptureGetBuffer(IntPtr self, out IntPtr data, out uint frames, out uint flags, out ulong devPos, out ulong qpcPos);
        private static int CaptureGetBuffer(IntPtr self, out IntPtr data, out uint frames, out uint flags, out ulong devPos, out ulong qpcPos)
        {
            var f = Marshal.GetDelegateForFunctionPointer<D_CaptureGetBuffer>(Vtbl(self, 3));
            return f(self, out data, out frames, out flags, out devPos, out qpcPos);
        }

        private delegate int D_ReleaseBuffer(IntPtr self, uint frames);
        private static int CaptureReleaseBuffer(IntPtr self, uint frames)
        {
            var f = Marshal.GetDelegateForFunctionPointer<D_ReleaseBuffer>(Vtbl(self, 4));
            return f(self, frames);
        }

        private delegate int D_NextPacket(IntPtr self, out uint frames);
        private static int CaptureNextPacket(IntPtr self, out uint frames)
        {
            var f = Marshal.GetDelegateForFunctionPointer<D_NextPacket>(Vtbl(self, 5));
            return f(self, out frames);
        }

        // IAudioSessionManager2 slot 5 (GetSessionEnumerator é o 3º método depois dos 2 herdados)
        private delegate int D_SessionMgrGetEnumerator(IntPtr self, out IntPtr en);
        private static int SessionManagerGetEnumerator(IntPtr self, out IntPtr en)
        {
            var f = Marshal.GetDelegateForFunctionPointer<D_SessionMgrGetEnumerator>(Vtbl(self, 5));
            return f(self, out en);
        }

        // IAudioSessionEnumerator slots 3/4
        private delegate int D_SessionEnumGetCount(IntPtr self, out int count);
        private static int SessionEnumeratorGetCount(IntPtr self, out int count)
        {
            var f = Marshal.GetDelegateForFunctionPointer<D_SessionEnumGetCount>(Vtbl(self, 3));
            return f(self, out count);
        }

        private delegate int D_SessionEnumGetSession(IntPtr self, int index, out IntPtr ctl);
        private static int SessionEnumeratorGetSession(IntPtr self, int index, out IntPtr ctl)
        {
            var f = Marshal.GetDelegateForFunctionPointer<D_SessionEnumGetSession>(Vtbl(self, 4));
            return f(self, index, out ctl);
        }

        // IAudioSessionControl2: GetState (slot 3) e GetProcessId (slot 14 — 9 herdados + 2 identificadores + pid)
        private delegate int D_SessionGetState(IntPtr self, out int state);
        private static int SessionControl2GetState(IntPtr self, out int state)
        {
            var f = Marshal.GetDelegateForFunctionPointer<D_SessionGetState>(Vtbl(self, 3));
            return f(self, out state);
        }

        private delegate int D_SessionGetPid(IntPtr self, out uint pid);
        private static int SessionControl2GetProcessId(IntPtr self, out uint pid)
        {
            var f = Marshal.GetDelegateForFunctionPointer<D_SessionGetPid>(Vtbl(self, 14));
            return f(self, out pid);
        }

        private delegate int D_Release(IntPtr self);
    }
}
