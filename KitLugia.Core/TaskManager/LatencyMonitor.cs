using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KitLugia.Core.TaskManager
{
    /// <summary>
    /// Monitor de latência/travamentos estilo LatencyMon — SEM driver próprio.
    /// 4 fontes nativas combinadas (tudo validado empiricamente):
    ///   1. Jitter do próprio loop: se o tick de 1s demorar bem mais, o sistema
    ///      inteiro passou fome (CPU/IO) — evento "sistema travou".
    ///   2. NtQuerySystemInformation class 8: DpcTime/InterruptTime ACUMULADOS por
    ///      núcleo — delta/janela = % de tempo que DRIVERS monopolizaram cada CPU.
    ///   3. ETW kernel (GetIfTable-style, real-time): eventos DPC/ISR com o ENDEREÇO
    ///      da rotina → atribuído ao driver .sys via SystemModuleInformation.
    ///      Requer admin e não pode conflitar com LatencyMon (1 kernel logger só).
    ///   4. IsHungAppWindow: janelas "Não respondendo" — o travamento VISÍVEL do
    ///      usuário, com nome da janela e duração.
    ///   + Hard page faults por processo (delta de HardFaultsCount do NtQSI class 5):
    ///      paginação pesada = engasgos de áudio/vídeo (é a linha do LatencyMon).
    /// Conclusão em português simples + relatório técnico completo p/ colar em IA.
    /// </summary>
    public sealed class LatencyMonitor : IDisposable
    {
        // ═══════════════════════════════ Modelo público ═══════════════════════════════

        public sealed class LatencyEvent
        {
            public DateTime When { get; set; }
            public string Kind { get; set; } = "";        // DPC | ISR | Sistema | Janela | Memória
            public string Severity { get; set; } = "info"; // info | warn | alert
            public string Title { get; set; } = "";        // frase curta p/ a tabela
            public string Explanation { get; set; } = "";  // "o que isso significa" p/ o usuário
            public string TechDetail { get; set; } = "";   // linha técnica p/ debug/IA
            public string StatusLabel => Severity switch
            {
                "alert" => "CRÍTICO",
                "warn" => "ATENÇÃO",
                _ => "INFO",
            };
        }

        public sealed class DriverStats
        {
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
            public long DpcCount;
            public long IsrCount;
            public long MaxDpcUs;   // aproximação por pareamento no mesmo núcleo (teto 250ms)
            public long MaxIsrUs;
            public long TotalUs;
            public long Count => DpcCount + IsrCount;
        }

        public sealed class WindowFreeze
        {
            public DateTime StartedUtc;
            public IntPtr Hwnd;
            public int Pid;
            public string ProcessName = "";
            public string WindowTitle = "";
            public bool Reported;
        }

        /// <summary>DPC/ISR de UM núcleo lógico (aba Núcleos, como o LatencyMon "CPUs").</summary>
        public sealed class CoreStats
        {
            public int Index;
            public long DpcTime100ns;     // delta da última janela (tempo de DPC)
            public long IsrTime100ns;     // delta da última janela (tempo de ISR)
            public long InterruptCount;   // deltas = nº de interrupções atendidas
            public double DpcPercent;     // % do núcleo consumido por DPC na janela
            public double IsrPercent;     // % do núcleo consumido por ISR na janela
            public long MaxDpcUs;         // maior execução de DPC vista (teto físico 250ms; deltas de janela com stall são descartados)
            public long MaxIsrUs;
            /// <summary>Alguma janela de medida atravessou um stall — o "max" pode estar subestimado.</summary>
            public bool StallFlag;
            public long TotalDpcUs;
            public long TotalIsrUs;
        }

        /// <summary>Hard page faults por processo (aba Processos, como o LatencyMon).</summary>
        public sealed class ProcessHardFault
        {
            public int Pid;
            public string Name = "";
            public ulong Total;         // falhas duras acumuladas na sessão
            public double PerSec;       // taxa atual (falhas que foram ao DISCO por segundo)
            public double WorkingSetMB;
            public ulong SoftFaults;    // PageFaultCount total (duras + de RAM)
        }

        public sealed class Snapshot
        {
            public bool Running;
            public TimeSpan Elapsed;
            public List<LatencyEvent> Events = new();
            public List<DriverStats> Drivers = new();
            public List<WindowFreeze> ActiveFreezes = new();
            public long TotalHardFaults;
            public double HardFaultsPerSec;
            public double MaxCoreDpcPercent;      // pico atual (último tick)
            public double MaxCoreDpcPercent1Min;  // pico na janela de 60s
            public bool EtwActive;
            public string EtwStatus = "";
            // Só fica true após evidência real de bloqueio: a sessão própria dedicada do
            // 2º estágio do watchdog também entregou 0 eventos com SeSystemProfilePrivilege
            // OK. (Antes isso era setado pelo rc do EnableTrace legado — que devolve erro 5
            // mesmo com a sessão fluindo — e a sessão SAUDÁVEL era morta na hora.)
            public bool EtwProviderDeniedByWindows;
            public string EtwPrivilegeStatus = "";
            // Total de eventos de TODOS os tipos na sessão (não só PerfInfo). Diferencia
            // "sessão fluindo mas sem PerfInfo" de "sessão completamente morta".
            public long EtwSessionTotalEvents;
            // True entre a criação da sessão e a 1ª evidência de fluxo (~12s do watchdog).
            public bool EtwUnconfirmed;
            public int Cores;
            public List<CoreStats> PerCore = new();              // aba Núcleos
            public List<ProcessHardFault> TopHardFaults = new(); // aba Processos
            // Jitter do PRÓPRIO monitor: pedimos 1s de CPU; se demoramos muito mais,
            // o sistema inteiro ficou sem processador (aproximação honesta de
            // "interrupt to process latency" sem driver de kernel).
            public long LastTickMs;
            public long MaxTickMs;
            public int JitterEvents;      // quantas vezes o tick passou de 2500ms
            public long MaxDpcUsSession;  // maior DPC de qualquer driver (ETW)
            public long MaxIsrUsSession;
            public string WorstDriver = "";
            public long EtwEventsSeen;        // eventos PerfInfo recebidos
            public long EtwRoutinesSeen;      // com rotina válida no payload
            public long EtwUnmatchedRoutines; // rotina fora de qualquer módulo conhecido
            public string EtwOpcodes = "";
        }

        // ═══════════════════════════════ Estado ═══════════════════════════════

        public static LatencyMonitor Instance { get; } = new();

        private LatencyMonitor()
        {
            // A sessao "NT Kernel Logger" do ETW e um recurso GLOBAL: se o processo morrer
            // sem parar a sessao, ela pode ficar orfa (e nem o LatencyMon nem nos
            // conseguiriamos abrir de novo). Aqui garantimos o Stop no encerramento.
            try
            {
                AppDomain.CurrentDomain.ProcessExit += (_, __) =>
                {
                    try { EtwStopSession(); } catch { }
                };
            }
            catch { }
        }

        private CancellationTokenSource? _cts;
        private Task? _loopTask;
        private readonly object _lock = new();
        private readonly Queue<LatencyEvent> _events = new();
        private readonly Dictionary<string, DriverStats> _drivers = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<WindowFreeze> _freezes = new();
        private readonly Queue<(DateTime t, double pct)> _dpc1Min = new();
        private DateTime _startedUtc;
        private bool _running;
        private long _totalHardFaults;
        private double _hardFaultsPerSec;
        private double _maxCoreDpcPct;
        private double _maxCoreDpcPct1Min;
        private int _lastDpcIsrCores;
        private readonly List<CoreStats> _perCore = new();
        private readonly Dictionary<int, ProcessHardFault> _hfByProc = new();
        private long _lastTickMs;
        private long _maxTickMs;
        private int _jitterEvents;
        private long _maxDpcUsSession;
        private long _maxIsrUsSession;
        // Diagnóstico do ETW (vai no relatório p/ IA: prova que a sessão está entregando)
        private long _etwEventsSeen;
        private long _etwRoutinesSeen;
        private long _etwUnmatchedRoutines;
        private readonly Dictionary<byte, int> _etwOpcodeHisto = new();
        private DateTime _etwStartedUtc;
        private bool _etwRecoveryAttempted;
        private long _etwSessionTotalEvents; // TODOS os eventos da sessão (qualquer GUID)

        // amostras anteriores (deltas)
        private NativeMetricsHelper.KernelModule[] _kernelMods = Array.Empty<NativeMetricsHelper.KernelModule>();
        private DateTime _modsStamp = DateTime.MinValue;
        private Dictionary<int, ulong> _prevHardFaults = new();
        private Dictionary<int, (string Name, long Ticks)> _dpcPairByCore = new(); // pareamento p/ duração

        public event Action? Updated; // UI assina; dispara no contexto capturado no Start

        private SynchronizationContext? _uiCtx;

        // ═══════════════════════════════ Start/Stop ═══════════════════════════════

        public void Start()
        {
            lock (_lock)
            {
                if (_running) return;
                _running = true;
                _startedUtc = DateTime.UtcNow;
                _uiCtx = SynchronizationContext.Current;
                _cts = new CancellationTokenSource();
                _loopTask = Task.Run(() => LoopAsync(_cts.Token));
            }
            _etwRecoveryAttempted = false; // cada ciclo Start→Stop tem seus 2 estágios de watchdog
            EtwStartSession(); // best-effort (requer admin; falha graciosa)
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_running) return;
                _running = false;
                try { _cts?.Cancel(); } catch { }
            }
            try { _loopTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
            EtwStopSession();
            lock (_lock)
            {
                try { _cts?.Dispose(); } catch { }
                _cts = null;
                _loopTask = null;
                _etwSessionTotalEvents = 0;
                _etwProviderDenied = false;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _events.Clear();
                _drivers.Clear();
                _freezes.Clear();
                _dpc1Min.Clear();
                _totalHardFaults = 0;
                _prevHardFaults.Clear();
                _dpcPairByCore.Clear();
                _perCore.Clear();
                _hfByProc.Clear();
                _maxTickMs = 0;
                _jitterEvents = 0;
                _maxDpcUsSession = 0;
                _maxIsrUsSession = 0;
                _etwEventsSeen = 0;
                _etwRoutinesSeen = 0;
                _etwUnmatchedRoutines = 0;
                _etwOpcodeHisto.Clear();
            }
        }

        public bool IsRunning { get { lock (_lock) return _running; } }

        public Snapshot GetSnapshot()
        {
            lock (_lock)
            {
                return new Snapshot
                {
                    Running = _running,
                    Elapsed = _running ? DateTime.UtcNow - _startedUtc : TimeSpan.Zero,
                    Events = _events.ToList(),
                    Drivers = _drivers.Values.OrderByDescending(d => d.Count).Take(30).ToList(),
                    ActiveFreezes = _freezes.Where(f => !f.Reported || IsWindowHungNow(f)).ToList(),
                    TotalHardFaults = _totalHardFaults,
                    HardFaultsPerSec = _hardFaultsPerSec,
                    MaxCoreDpcPercent = _maxCoreDpcPct,
                    MaxCoreDpcPercent1Min = _maxCoreDpcPct1Min,
                    EtwActive = _etwActive,
                    EtwStatus = _etwStatus,
                    EtwProviderDeniedByWindows = _etwProviderDenied,
                    EtwSessionTotalEvents = Interlocked.Read(ref _etwSessionTotalEvents),
                    EtwUnconfirmed = _etwActive && Interlocked.Read(ref _etwSessionTotalEvents) == 0,
                    EtwPrivilegeStatus = _privStatus,
                    Cores = _lastDpcIsrCores > 0 ? _lastDpcIsrCores : Environment.ProcessorCount,
                    PerCore = _perCore.Select(c => new CoreStats
                    {
                        Index = c.Index, DpcTime100ns = c.DpcTime100ns, IsrTime100ns = c.IsrTime100ns,
                        InterruptCount = c.InterruptCount, DpcPercent = c.DpcPercent, IsrPercent = c.IsrPercent,
                        MaxDpcUs = c.MaxDpcUs, MaxIsrUs = c.MaxIsrUs, TotalDpcUs = c.TotalDpcUs, TotalIsrUs = c.TotalIsrUs,
                        StallFlag = c.StallFlag,
                    }).ToList(),
                    TopHardFaults = _hfByProc.Values.OrderByDescending(p => p.Total).Take(60).ToList(),
                    LastTickMs = _lastTickMs,
                    MaxTickMs = _maxTickMs,
                    JitterEvents = _jitterEvents,
                    MaxDpcUsSession = _maxDpcUsSession,
                    MaxIsrUsSession = _maxIsrUsSession,
                    WorstDriver = worstDriverHint(),
                    EtwEventsSeen = _etwEventsSeen,
                    EtwRoutinesSeen = _etwRoutinesSeen,
                    EtwUnmatchedRoutines = _etwUnmatchedRoutines,
                    EtwOpcodes = string.Join(", ", _etwOpcodeHisto.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"0x{kv.Key:X2}={kv.Value}")),
                };
            }
        }

        private bool IsWindowHungNow(WindowFreeze f)
        {
            try { return User32IsHungAppWindow(f.Hwnd); }
            catch { return false; }
        }

        // ═══════════════════════════════ Loop principal ═══════════════════════════════

        private async Task LoopAsync(CancellationToken ct)
        {
            int tick = 0;
            bool primed = false;
            while (!ct.IsCancellationRequested)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    tick++;

                    // ── 1) DPC/ISR por núcleo (classe 8 — exato, sem admin) ──
                    var cur = NativeMetricsHelper.GetPerCoreDpcIsrCumulative();
                    if (cur != null && _prevDpc != null && _prevDpc.DpcTime100ns.Length == cur.DpcTime100ns.Length)
                    {
                        double winMs = (cur.Stamp100ns - _prevDpc.Stamp100ns) / 10000.0;
                        if (winMs > 200)
                        {
                            double worstPct = 0; int worstCore = -1;
                            long worstCoreDpc100ns = 0, worstCoreInt100ns = 0;
                            lock (_lock)
                            {
                                // Reconstrói a tabela por núcleo a cada janela (aba Núcleos).
                                // Rolling: maior execução e totais ACUMULADOS por núcleo —
                                // o class 8 não dá contagem de DPC por núcleo (só tempo), então
                                // contamos "eventos" pelos deltas de interrupção e guardamos
                                // os picos de tempo como MaxDpcUs/MaxIsrUs.
                                if (_perCore.Count != cur.DpcTime100ns.Length)
                                {
                                    _perCore.Clear();
                                    for (int k = 0; k < cur.DpcTime100ns.Length; k++) _perCore.Add(new CoreStats { Index = k });
                                }
                                for (int i = 0; i < cur.DpcTime100ns.Length; i++)
                                {
                                    long dDpc = cur.DpcTime100ns[i] - _prevDpc.DpcTime100ns[i];
                                    long dInt = cur.InterruptTime100ns[i] - _prevDpc.InterruptTime100ns[i];
                                    long dCnt = (long)cur.InterruptCount[i] - _prevDpc.InterruptCount[i];
                                    if (dDpc < 0) dDpc = 0; if (dInt < 0) dInt = 0; if (dCnt < 0) dCnt = 0;
                                    long dpcUs = dDpc / 10, isrUs = dInt / 10;
                                    // TETO FÍSICO: a janela às vezes atravessa um stall (varredura do AV,
                                    // swap de centenas de MB) — o delta acumula o tempo de MUITAS rotinas e
                                    // não representa UMA execução. 250ms por rotina é o teto do relatório
                                    // (a quantização do relógio é 15,625ms); acima disso, rótulo honesto.
                                    const long CeilingUs = 250_000;
                                    var cs = _perCore[i];
                                    cs.DpcTime100ns = dDpc; cs.IsrTime100ns = dInt; cs.InterruptCount = dCnt;
                                    cs.DpcPercent = dDpc / 10000.0 / winMs * 100.0;
                                    cs.IsrPercent = dInt / 10000.0 / winMs * 100.0;
                                    cs.TotalDpcUs += dpcUs; cs.TotalIsrUs += isrUs;
                                    if (dpcUs > cs.MaxDpcUs && dpcUs <= CeilingUs) cs.MaxDpcUs = dpcUs;
                                    if (isrUs > cs.MaxIsrUs && isrUs <= CeilingUs) cs.MaxIsrUs = isrUs;
                                    if (dpcUs > CeilingUs || isrUs > CeilingUs) cs.StallFlag = true;
                                }
                            }
                            for (int i = 0; i < cur.DpcTime100ns.Length; i++)
                            {
                                long dDpc = cur.DpcTime100ns[i] - _prevDpc.DpcTime100ns[i];
                                long dInt = cur.InterruptTime100ns[i] - _prevDpc.InterruptTime100ns[i];
                                double pct = dDpc / 10000.0 / winMs * 100.0;
                                if (pct > worstPct) { worstPct = pct; worstCore = i; worstCoreDpc100ns = dDpc; worstCoreInt100ns = dInt; }
                            }
                            _maxCoreDpcPct = worstPct;

                            // janela deslizante de 60s p/ o veredito
                            var now = DateTime.UtcNow;
                            _dpc1Min.Enqueue((now, worstPct));
                            while (_dpc1Min.Count > 0 && (now - _dpc1Min.Peek().t).TotalSeconds > 60) _dpc1Min.Dequeue();
                            _maxCoreDpcPct1Min = _dpc1Min.Count > 0 ? _dpc1Min.Max(x => x.pct) : 0;

                            // >15% de UM núcleo em DPC = driver segurando a CPU (Limiar LatencyMon-ish)
                            if (primed && worstPct >= 15)
                            {
                                string drv = worstDriverHint();
                                AddEvent(new LatencyEvent
                                {
                                    When = DateTime.Now,
                                    Kind = "DPC",
                                    Severity = worstPct >= 40 ? "alert" : "warn",
                                    Title = $"Drivers monopolizaram o núcleo {worstCore} por {(int)(worstCoreDpc100ns / 10000.0)} ms ({worstPct:F0}% da janela)",
                                    Explanation = "Rotinas DPC são trabalhos de driver que rodam ANTES de qualquer programa. " +
                                        $"Quando passam de ~15% de um núcleo, áudio (clicks/pops), vídeo e jogos engasgam. " +
                                        (drv.Length > 0 ? $"Suspeito principal agora: {drv}." : "Rode como administrador para identificar o driver exato."),
                                    TechDetail = $"core={worstCore} dpc={worstCoreDpc100ns * 100}ns int={worstCoreInt100ns * 100}ns window={winMs:F0}ms etw={(_etwActive ? "on" : "off")}",
                                });
                            }
                        }
                    }
                    if (cur != null) { _prevDpc = cur; _lastDpcIsrCores = cur.DpcTime100ns.Length; }

                    // ── 1b) WATCHDOG DE SESSÃO ÓRFÃ ──────────────────────────────
                    // Sintoma real (máquina do usuário): anexamos a uma sessão "NT Kernel
                    // Logger" com flags DPC/ISR, mas ela entrega ZERO eventos PerfInfo
                    // para sempre (eventosPerdidos congelado). Origem: um StartTrace de
                    // uma execução anterior em que o EnableTrace falhou. Um consumidor
                    // anexado a isso nunca identifica driver. Se passaram >10s e nenhum
                    // evento PerfInfo chegou, a sessão está morta: paramos (ação global,
                    // mas ela já não serve para NINGUÉM) e recriamos a nossa — em builds
                    // onde o provedor funciona, a tabela Drivers enche de vez.
                    // ── 1b) WATCHDOG DE SESSÃO MORTA (2 estágios, decisão por EVIDÊNCIA) ──
                    // Uma sessão "NT Kernel Logger" pode estar viva mas não fluir (órfã congelada
                    // de execução anterior, criada por outro programa sem flags úteis). O teste
                    // não é o rc do EnableTrace (devolve erro 5 até com sessão saudável): é
                    // EXISTEM eventos chegando. Estágio 1 (12s sem NENHUM evento): se a sessão
                    // era alheia, paramos e criamos a NOSSA com flags DPC/ISR — sessões system
                    // logger adicionais são legítimas (SYSTEM_LOGGER_MODE). Estágio 2 (a nossa
                    // própria também 12s sem nada): aí sim declaramos o provedor bloqueado para
                    // processos de usuário (o que só um driver de kernel resolve, como o
                    // rspLLL64.sys do LatencyMon) — sem loop: cada estágio roda UMA vez.
                    long sessEv = Interlocked.Read(ref _etwSessionTotalEvents);
                    if (_etwActive && sessEv > 0)
                    {
                        // sessão fluindo — nada a fazer
                    }
                    else if (_etwActive)
                    {
                        if (!_etwRecoveryAttempted && (DateTime.UtcNow - _etwStartedUtc).TotalSeconds > 12)
                        {
                            _etwRecoveryAttempted = true; // vale para TODO o ciclo Start→Stop
                            try
                            {
                                bool eraAlheia = _etwSessionHandle == 0; // caminho "anexada" não tem handle local
                                try { if (_propsBuf != IntPtr.Zero) ControlTraceW(0, KernelLoggerName, _propsBuf, EVENT_TRACE_CONTROL_STOP); } catch { }
                                EtwStopSession();
                                if (eraAlheia)
                                {
                                    _etwStatus = "sessão alheia sem eventos — criando sessão própria";
                                    EtwStartSessionCore(skipWatchdogReset: true); // NÃO resetar a flag = sem loop
                                    if (_etwActive) _etwStartedUtc = DateTime.UtcNow;
                                }
                                else
                                {
                                    _etwProviderDenied = true;
                                    _etwStatus = "sem eventos mesmo com sessão própria dedicada — provedor de kernel " +
                                                 "bloqueado pelo Windows para processos de usuário (só um driver de kernel " +
                                                 "contorna; DPC/ISR por núcleo segue válido)";
                                }
                            }
                            catch { }
                        }
                    }

                    // ── 2) Hard page faults por processo (class 5 — HardFaultsCount) ──
                    if (tick % 2 == 0)
                    {
                        var procs = NativeMetricsHelper.EnumerateProcesses();
                        if (procs != null)
                        {
                            var curHf = new Dictionary<int, ulong>(procs.Count);
                            double dTotal = 0;
                            var perProc = new List<(int pid, string name, double rate)>(procs.Count);
                            foreach (var p in procs)
                            {
                                curHf[p.Pid] = p.HardFaults;
                                if (!primed && tick == 2) { }
                                if (_prevHardFaults.TryGetValue(p.Pid, out ulong prev) && p.HardFaults >= prev)
                                {
                                    double d = p.HardFaults - prev; // janela = 2 ticks ≈ 2s
                                    if (d > 0)
                                    {
                                        dTotal += d;
                                        perProc.Add((p.Pid, string.IsNullOrEmpty(p.Name) ? $"({p.Pid})" : p.Name, d / 2.0));
                                    }
                                }
                            }
                            _prevHardFaults = curHf;
                            _hardFaultsPerSec = dTotal / 2.0;
                            _totalHardFaults += (long)dTotal;

                            lock (_lock)
                            {
                                foreach (var p in procs)
                                {
                                    if (!_hfByProc.TryGetValue(p.Pid, out var hf))
                                    {
                                        hf = new ProcessHardFault { Pid = p.Pid, Name = string.IsNullOrEmpty(p.Name) ? "(" + p.Pid + ")" : p.Name };
                                        _hfByProc[p.Pid] = hf;
                                    }
                                    hf.Name = string.IsNullOrEmpty(p.Name) ? hf.Name : p.Name;
                                    hf.WorkingSetMB = p.WorkingSetBytes / 1048576.0;
                                    hf.SoftFaults = p.PageFaults;
                                    double rate = 0;
                                    if (_prevHardFaults.TryGetValue(p.Pid, out ulong pr) && p.HardFaults >= pr) rate = (p.HardFaults - pr) / 2.0;
                                    hf.PerSec = rate;
                                    hf.Total += (ulong)rate * 2;
                                    if (hf.Total == 0 && p.HardFaults > 0) hf.Total = p.HardFaults; // 1ª amostra: valor absoluto
                                }
                            }
                            var top = perProc.Where(x => x.rate >= 25).OrderByDescending(x => x.rate).Take(3).ToList();
                            if (primed && top.Count > 0)
                            {
                                string who = string.Join(", ", top.Select(t => $"{t.name} ({t.rate:F0}/s)"));
                                AddEvent(new LatencyEvent
                                {
                                    When = DateTime.Now,
                                    Kind = "Memória",
                                    Severity = top[0].rate >= 500 ? "warn" : "info",
                                    Title = $"Paginação pesada do disco: {who}",
                                    Explanation = "Hard page fault = o programa pediu memória que tinha sido mandada para o arquivo de paginação no DISCO. " +
                                        "Enquanto o Windows busca, o programa (e às vezes o áudio do sistema) espera. " +
                                        "Se repete muito: feche programas ou considere mais RAM.",
                                    TechDetail = string.Join("; ", top.Select(t => $"pid={t.pid} {t.rate:F1}/s")),
                                });
                            }
                        }
                    }

                    // ── 3) Janelas travadas (IsHungAppWindow — o "Não respondendo") ──
                    if (tick % 2 == 1)
                    {
                        ScanHungWindows(primed);
                    }

                    // ── 4) Jitter do próprio loop = sistema INTEIRO travou ──
                    _lastTickMs = sw.ElapsedMilliseconds;
                    if (_lastTickMs > _maxTickMs) _maxTickMs = _lastTickMs;
                    if (primed && sw.ElapsedMilliseconds > 2500)
                    {
                        lock (_lock) _jitterEvents++;
                        AddEvent(new LatencyEvent
                        {
                            When = DateTime.Now,
                            Kind = "Sistema",
                            Severity = "alert",
                            Title = $"O sistema inteiro congelou por {(int)(sw.ElapsedMilliseconds / 1000.0)} s",
                            Explanation = "Este monitor pede 1 segundo de CPU por vez — se ele demorou muito mais, TODOS os " +
                                "programas ficaram sem processador ao mesmo tempo. Causas típicas: tempestade de DPC de driver, " +
                                "page file explodindo ou disco 100%. Veja os eventos DPC/Memória no mesmo horário.",
                            TechDetail = $"tick levou {sw.ElapsedMilliseconds}ms (esperado ~1000ms)",
                        });
                    }

                    primed = true;
                }
                catch { /* monitor nunca derruba o app */ }

                var delay = (int)Math.Max(50, 1000 - sw.ElapsedMilliseconds);
                try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (TaskCanceledException) { break; }
            }
        }

        private NativeMetricsHelper.CoreDpcIsrSnapshot? _prevDpc;

        /// <summary>Suspeito atual: driver com mais eventos DPC/ISR na sessão ETW.</summary>
        private string worstDriverHint()
        {
            lock (_lock)
            {
                var top = _drivers.Values.Where(d => d.Count > 0).OrderByDescending(d => d.Count).FirstOrDefault();
                return top?.Name ?? "";
            }
        }

        private void ScanHungWindows(bool primed)
        {
            try
            {
                var hungNow = new List<(IntPtr hwnd, int pid, string title)>();
                EnumWindows((hwnd, _) =>
                {
                    if (!IsWindowVisible(hwnd)) return true;
                    if (!IsHungAppWindow(hwnd)) return true;
                    GetWindowThreadProcessId(hwnd, out int pid);
                    var sb = new StringBuilder(256);
                    GetWindowTextW(hwnd, sb, 256);
                    hungNow.Add((hwnd, pid, sb.ToString()));
                    return true;
                }, IntPtr.Zero);

                lock (_lock)
                {
                    // encerra freezes que sumiram
                    for (int i = _freezes.Count - 1; i >= 0; i--)
                    {
                        var f = _freezes[i];
                        if (!User32IsHungAppWindow(f.Hwnd))
                        {
                            var dur = DateTime.UtcNow - f.StartedUtc;
                            if (primed && dur.TotalMilliseconds > 1200)
                            {
                                AddEvent(new LatencyEvent
                                {
                                    When = DateTime.Now,
                                    Kind = "Janela",
                                    Severity = dur.TotalSeconds >= 5 ? "warn" : "info",
                                    Title = $"'{Trunc(f.WindowTitle ?? f.ProcessName, 48)}' travou por {(int)dur.TotalSeconds}s e voltou",
                                    Explanation = $"A janela do {f.ProcessName} parou de responder (o Windows marca como \"Não respondendo\"). " +
                                        "Isso acontece quando o programa não processa mensagens do Windows — geralmente esperando disco, rede ou preso em cálculo. " +
                                        (dur.TotalSeconds >= 5 ? "Mais de 5s já incomoda o usuário; vale investigar o que o processo fazia nesse horário." : ""),
                                    TechDetail = $"pid={f.Pid} proc={f.ProcessName} dur={(int)dur.TotalMilliseconds}ms title='{Trunc(f.WindowTitle, 60)}'",
                                });
                            }
                            _freezes.RemoveAt(i);
                            continue;
                        }
                    }
                    // registra novos freezes
                    foreach (var (hwnd, pid, title) in hungNow)
                    {
                        if (_freezes.Any(f => f.Hwnd == hwnd)) continue;
                        string proc = "";
                        try { using var p = System.Diagnostics.Process.GetProcessById(pid); proc = p.ProcessName; } catch { }
                        _freezes.Add(new WindowFreeze
                        {
                            StartedUtc = DateTime.UtcNow,
                            Hwnd = hwnd,
                            Pid = pid,
                            ProcessName = proc,
                            WindowTitle = title,
                        });
                    }
                }
            }
            catch { }
        }

        private static string Trunc(string? s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s!.Length <= max ? s : s[..(max - 3)] + "...");

        private void AddEvent(LatencyEvent e)
        {
            lock (_lock)
            {
                _events.Enqueue(e);
                while (_events.Count > 500) _events.Dequeue();
            }
            var ctx = _uiCtx;
            if (ctx != null) ctx.Post(_ => { try { Updated?.Invoke(); } catch { } }, null);
            else Updated?.Invoke();
        }

        // ═══════════════════════════════ Conclusão (caixa do LatencyMon) ═══════════════════════════════

        /// <summary>0=verde 1=amarelo 2=vermelho. Texto em português simples estilo LatencyMon.</summary>
        public (int level, string text) BuildConclusion()
        {
            var snap = GetSnapshot();
            if (!snap.Running)
                return (1, "Monitoramento parado. Clique em INICIAR e deixe rodar alguns minutos — a análise aparece aqui, como no LatencyMon.");

            var sb = new StringBuilder();
            int level = 0;

            if (snap.MaxCoreDpcPercent1Min >= 25)
            {
                level = 2;
                sb.AppendLine("PROBLEMA: um ou mais drivers estão segurando o processador por tempo demais (DPC). " +
                    "É a causa clássica de áudio estalando, vídeo engasgando e jogos dando micro-freeze. " +
                    (worstDriverHint().Length > 0 ? $"Suspeito principal: {worstDriverHint()} — atualize o driver pelo site do fabricante." : "Rode como administrador para ver o driver exato."));
            }
            else if (snap.MaxCoreDpcPercent1Min >= 8)
            {
                level = Math.Max(level, 1);
                sb.AppendLine("ATENÇÃO: picos moderados de DPC detectados. Ainda longe do crítico, mas se você usa áudio em tempo real " +
                    "pode perceber estalos ocasionais. Observe quais eventos aparecem juntos.");
            }
            else
            {
                sb.AppendLine("DPC saudável: nenhum núcleo passou de 8% em DPC na última janela — os drivers estão se comportando bem.");
            }

            var fz = snap.ActiveFreezes;
            if (fz.Count > 0)
            {
                level = Math.Max(level, 1);
                var f = fz[0];
                var dur = DateTime.UtcNow - f.StartedUtc;
                sb.AppendLine($"AGORA: a janela '{Trunc(f.WindowTitle ?? f.ProcessName, 40)}' ({f.ProcessName}) está SEM RESPONDER há {(int)dur.TotalSeconds}s.");
            }

            if (snap.HardFaultsPerSec > 200)
            {
                // Página dura só é problema quando a RAM ACABOU (aí o Windows tem de escrever
                // páginas sujas para abrir espaço). Com RAM disponível, paginação = dados frios
                // indo/dizendo do standby — barato em NVMe e NORMAL (ex.: 404/s com 29% em uso).
                double availMb = GetAvailableRamMb();
                bool ramApertada = availMb < 0 || availMb < 1024; // <1GB disponível = aperto real
                level = Math.Max(level, ramApertada ? (snap.HardFaultsPerSec > 1000 ? 2 : 1) : 0);
                if (ramApertada)
                {
                    sb.AppendLine($"PAGINAÇÃO PESADA COM RAM NO LIMITE: {snap.HardFaultsPerSec:F0} page faults de disco por segundo " +
                        $"e só {availMb:F0} MB de RAM disponíveis — a memória acabou. Isso congela programas em frações de " +
                        "segundo e também derruba o áudio. Feche programas ou instale mais RAM.");
                }
                else
                {
                    sb.AppendLine($"Paginação frequente mas SAUDÁVEL: {snap.HardFaultsPerSec:F0} page faults/s com RAM de sobra " +
                        $"({availMb:F0} MB disponíveis). É o Windows movendo dados frios entre RAM e disco — em NVMe isso é " +
                        "quase imperceptível. Só vira problema se a RAM disponível cair perto de 1 GB.");
                }
            }

            if (snap.JitterEvents > 0)
            {
                level = Math.Max(level, snap.MaxTickMs >= 5000 ? 2 : 1);
                sb.AppendLine($"TRAVADAS DO SISTEMA INTEIRO: em {snap.JitterEvents} momento(s) TODOS os programas pararam juntos " +
                    $"(o pior deles durou ~{snap.MaxTickMs / 1000.0:F1}s). Isso é diferente de um programa lento: é o sistema que " +
                    "não conseguiu entregar CPU para ninguém. Veja os eventos DPC/Memória no mesmo horário.");
            }

            if (level == 0) sb.AppendLine("Nenhum travamento relevante na janela monitorada. Pode fechar esta aba e deixar rodando em segundo plano.");

            return (level, sb.ToString().TrimEnd());
        }

        // ═══════════════════════════ Memória para calibrar paginação ═══════════════════════════

        /// <summary>RAM disponível em MB (o que sobrou para apps abrirem sem paginar); -1 = indisponível.</summary>
        private static double GetAvailableRamMb()
        {
            try { return NativeMetricsHelper.GetAvailableRamMb(); } catch { return -1; }
        }

        // ═══════════════════════════════ Relatório p/ IA ═══════════════════════════════

        /// <summary>Relatório técnico completo em texto — feito para colar direto numa IA/suporte.</summary>
        public string BuildAiReport()
        {
            var snap = GetSnapshot();
            var sb = new StringBuilder();
            sb.AppendLine("=== KitLugia — Relatório de Latência/Travamentos ===");
            sb.AppendLine($"Gerado: {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
            sb.AppendLine($"Monitoramento ativo: {snap.Running} (duração {snap.Elapsed:hh\\:mm\\:ss})");
            sb.AppendLine($"SO: {Environment.OSVersion.VersionString} | {(Environment.Is64BitOperatingSystem ? "x64" : "x86")} | {Environment.ProcessorCount} núcleos lógicos");
            try
            {
                double availMb = GetAvailableRamMb();
                sb.AppendLine(availMb >= 0
                    ? $"RAM física: {NativeMetricsHelper.GetMemoryUsagePercent():F0}% em uso ({availMb:F0} MB disponíveis)"
                    : $"RAM física: {NativeMetricsHelper.GetMemoryUsagePercent():F0}% em uso");
            }
            catch { }
            sb.AppendLine($"Sessão ETW kernel: {(snap.EtwActive ? (snap.EtwUnconfirmed ? "CONFIRMANDO — " : "ATIVA — ") + snap.EtwStatus : $"INDISPONÍVEL — {snap.EtwStatus}")}");
            if (snap.EtwActive)
            {
                sb.AppendLine($"  eventos PerfInfo recebidos={snap.EtwEventsSeen} com rotina válida={snap.EtwRoutinesSeen} " +
                              $"fora de módulo conhecido={snap.EtwUnmatchedRoutines} | eventos totais da sessão={snap.EtwSessionTotalEvents} | opcodes: {snap.EtwOpcodes}");
                if (snap.EtwEventsSeen == 0 && snap.Elapsed.TotalSeconds > 15)
                    sb.AppendLine("  ⚠ ATENÇÃO: sessão ativa mas SEM eventos PerfInfo de DPC/ISR — ver campo de eventos totais " +
                                  "acima: 0 total = sessão morta (o watchdog recria sozinho); >0 mas 0 PerfInfo = provedor de " +
                                  "kernel não entrega DPC/ISR para processos de usuário. A identificação por driver fica " +
                                  "indisponível; DPC/ISR por núcleo (acima) continua válido.");
            }
            sb.AppendLine();
            var (lvl, txt) = BuildConclusion();
            sb.AppendLine("--- CONCLUSÃO (nível " + (lvl == 0 ? "OK" : lvl == 1 ? "ATENÇÃO" : "CRÍTICO") + ") ---");
            sb.AppendLine(txt);
            sb.AppendLine();
            sb.AppendLine($"--- DRIVERS POR DPC/ISR (top {Math.Min(15, snap.Drivers.Count)}) ---");
            if (snap.Drivers.Count == 0)
                sb.AppendLine(snap.EtwActive && snap.EtwEventsSeen == 0 && snap.Elapsed.TotalSeconds > 15
                    ? "(nenhum driver identificado — a sessão não entrega eventos PerfInfo; ver aviso no topo)"
                    : "(nenhum driver identificado ainda — a tabela se preenche quando a sessão ETW entrega eventos de DPC/ISR)");
            foreach (var d in snap.Drivers.Take(15))
                sb.AppendLine($"  {d.Name,-28} DPC={d.DpcCount,-7} ISR={d.IsrCount,-7} maiorDPC={d.MaxDpcUs / 1000.0,8:F2}ms total={d.TotalUs / 1000.0,10:F1}ms {d.Description}");
            sb.AppendLine();
            sb.AppendLine("--- NÚCLEOS (DPC/ISR da última janela) ---");
            foreach (var c in snap.PerCore)
            {
                string maxDpc = c.MaxDpcUs > 0 ? $"{c.MaxDpcUs / 1000.0:F2}ms" : (c.StallFlag ? ">250ms(stall)" : "n/a");
                string maxIsr = c.MaxIsrUs > 0 ? $"{c.MaxIsrUs / 1000.0:F2}ms" : (c.StallFlag ? ">250ms(stall)" : "n/a");
                sb.AppendLine($"  CPU{c.Index,-3} dpc={c.DpcPercent,6:F2}% (max {maxDpc,14}) isr={c.IsrPercent,6:F2}% (max {maxIsr,14}) ints={c.InterruptCount,7} totalDpcUs={c.TotalDpcUs}");
            }
            sb.AppendLine();
            sb.AppendLine("--- PROCESSOS COM MAIS HARD PAGE FAULTS (disco) ---");
            foreach (var p in snap.TopHardFaults.Take(20))
                sb.AppendLine($"  pid={p.Pid,-7} {Trunc(p.Name, 28),-28} falhasNoDisco={p.Total,-9} atual={p.PerSec,7:F0}/s falhasTotais={p.SoftFaults,-10} ws={p.WorkingSetMB,8:F1}MB");
            sb.AppendLine();
            sb.AppendLine("--- EVENTOS (mais recentes primeiro, máx 60) ---");
            foreach (var e in snap.Events.Take(60).Reverse())
                sb.AppendLine($"  [{e.When:HH:mm:ss}] {e.Kind,-8} {e.Severity.ToUpper(),-6} {e.Title} | {e.TechDetail}");
            sb.AppendLine();
            sb.AppendLine("--- JANELAS TRAVADAS ATIVAS ---");
            foreach (var f in snap.ActiveFreezes)
                sb.AppendLine($"  pid={f.Pid} {f.ProcessName} desde {f.StartedUtc:HH:mm:ss} title='{Trunc(f.WindowTitle, 60)}'");
            if (snap.ActiveFreezes.Count == 0) sb.AppendLine("  (nenhuma)");
            sb.AppendLine();
            sb.AppendLine("--- MÉTRICAS ---");
            sb.AppendLine($"  Pico DPC 1 núcleo (60s): {snap.MaxCoreDpcPercent1Min:F1}%  |  atual: {snap.MaxCoreDpcPercent:F1}%");
            sb.AppendLine($"  Hard page faults: total sessão={snap.TotalHardFaults:N0}  atual={snap.HardFaultsPerSec:F0}/s");
            sb.AppendLine($"  Maior execução DPC (sessão)={snap.MaxDpcUsSession / 1000.0:F2}ms  ISR={snap.MaxIsrUsSession / 1000.0:F2}ms  driver suspeito='{snap.WorstDriver}'");
            sb.AppendLine($"  Resposta do sistema: último tick={snap.LastTickMs}ms  pior={snap.MaxTickMs}ms  travadas do sistema inteiro={snap.JitterEvents}");
            return sb.ToString();
        }

        // ═══════════════════════════════ ETW kernel (DPC/ISR → driver) ═══════════════════════════════
        // Sessão "NT Kernel Logger" em tempo real com EVENT_TRACE_FLAG_DPC|INTERRUPT.
        // Cada evento chega com o ENDEREÇO da rotina no payload → atribuímos ao .sys
        // carregado (GetKernelModules). 1 sessão de kernel logger por boot: se outro
        // programa (LatencyMon) estiver usando, NÃO matamos a sessão alheia.

        private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
        private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
        // OBRIGATORIO para o "NT Kernel Logger": sem esta flag o StartTrace ate funciona,
        // mas a sessao nasce como logger COMUM e o EnableTrace do provedor de kernel
        // devolve ERROR_ACCESS_DENIED (0x5) — era exatamente o sintoma relatado
        // ("kit ja e admin e mesmo assim pede admin"). evntrace.h: 0x02000000.
        private const uint EVENT_TRACE_SYSTEM_LOGGER_MODE = 0x02000000;
        private const uint EVENT_TRACE_CONTROL_QUERY = 0;
        private const uint ERROR_WMI_INSTANCE_NOT_FOUND = 4201;
        private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
        private const uint EVENT_TRACE_FLAG_DPC = 0x00000020;       // evntrace.h (SDK 26100)
        private const uint EVENT_TRACE_FLAG_INTERRUPT = 0x00000040; // evntrace.h
        private const uint EVENT_TRACE_CONTROL_STOP = 1;
        private static readonly Guid SystemTraceControlGuid = new("9e814aad-3204-11d2-9a82-006008a86939"); // evntrace.h
        private static readonly Guid PerfinfoGuid = new("ce1dbfb4-137e-4da6-87b0-3f48aa42bf39");           // grupo PerfInfo (DPC/ISR)
        private const string KernelLoggerName = "NT Kernel Logger";

        private ulong _etwSessionHandle;
        private ulong _etwTraceHandle;
        private IntPtr _propsBuf = IntPtr.Zero;
        private Thread? _etwThread;
        private volatile bool _etwActive;
        private string _etwStatus = "não iniciada";
        private string _privStatus = "";
        private bool _etwProviderDenied;
        private GCHandle _callbackHandle;
        private GCHandle _loggerNameHandle;

        // offsets no buffer de EVENT_TRACE_PROPERTIES (x64, conferidos campo a campo no
        // SDK evntrace.h/wmistr.h). O WNODE_HEADER começa em 0:
        //   Wnode.BufferSize@0 ProviderId@4 HistoricalContext@8 TimeStamp@16
        //   Wnode.Guid@24 ClientContext@40 Flags@44
        //   props: BufferSize@48 MinimumBuffers@52 MaximumBuffers@56 MaximumFileSize@60
        //   LogFileMode@64 FlushTimer@68 EnableFlags@72 AgeLimit@76 | NumberOfBuffers@80
        //   ... LoggerThreadId@104 LogFileNameOffset@112 LoggerNameOffset@116 | sizeof=120
        private const int Props_Size = 120;
        private const int Props_WnodeBufferSize = 0;      // Wnode.BufferSize (absoluto!)
        private const int Props_WnodeGuid = 24;           // Wnode.Guid
        private const int Props_WnodeClientContext = 40;  // Wnode.ClientContext (clock)
        private const int Props_WnodeFlags = 44;          // Wnode.Flags
        private const int Props_LogFileMode = 64;
        private const int Props_FlushTimer = 68;
        private const int Props_EnableFlags = 72;
        private const int Props_LogFileNameOffset = 112;
        private const int Props_LoggerNameOffset = 116;

        // EVENT_TRACE_LOGFILEW (x64): LogFileName@0 LoggerName@8 CurrentTime@16 BuffersRead@24
        // ProcessTraceMode@28 | CurrentEvent@32 (88) | LogfileHeader@120 (280) | BufferCallback@400
        // BufferSize@408 Filled@412 EventsLost@416 | EventRecordCallback@424 | IsKernelTrace@432 Context@440 | sizeof=448
        private const int LogFile_Size = 448;
        private const int LogFile_LoggerName = 8;
        private const int LogFile_ProcessTraceMode = 28;
        private const int LogFile_EventRecordCallback = 424;

        // EVENT_RECORD (x64): Header@0 (EVENT_HEADER 80B: Size@0 ThreadId@8 ProcessId@12 TimeStamp@16
        // ProviderId@24 EventDescriptor@40 → Opcode@45, Level@44) | BufferContext@80 | ExtendedDataCount@84
        // UserDataLength@86 | ExtendedData@88 | UserData@96 | UserContext@104
        // EVENT_RECORD (evntcons.h) no x64 — layout conferido byte a byte:
        //   EVENT_HEADER (80) | BufferContext(4)@80 | ExtendedDataCount(2)@84 |
        //   UserDataLength(2)@86 | UserData(8)@88 | UserContext(8)@96 | ExtendedData(8)@104
        // O UserData NÃO fica em 96 (lÃ¡ Ã© o UserContext, normalmente NULL — lido como
        // ponteiro nulo, o callback descartava TODOS os eventos de DPC/ISR e a lista de
        // drivers ficava vazia mesmo com a sessão ETW ativa).
        private const int Erec_ProcessId = 12;
        private const int Erec_ThreadId = 8;
        private const int Erec_TimeStamp = 16;
        private const int Erec_ProviderId = 24;
        private const int Erec_Opcode = 45;   // EVENT_DESCRIPTOR: Id(2)+Version+Channel+Level
        private const int Erec_UserDataLength = 86;
        private const int Erec_UserData = 88;

        private delegate void EventRecordCallback(IntPtr eventRecord);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint BufferCallbackRef(IntPtr logfile); // não usado (null)

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint ControlTraceW(ulong sessionHandle, string? sessionName, IntPtr properties, uint controlCode);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint EnableTrace(uint enable, uint flag, uint level, ref Guid controlGuid, ulong sessionHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ulong OpenTraceW(ref IntPtr logfile); // na prática passamos o ponteiro cru

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint ProcessTrace(ref ulong handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint CloseTrace(ulong traceHandle);

        private void EtwStartSession() => EtwStartSessionCore(skipWatchdogReset: false);

        /// <param name="skipWatchdogReset">
        /// true quando chamado PELO watchdog (estágio 1): o ciclo Start→Stop já gastou sua
        /// única recuperação — resetar a flag aqui recriaria o loop infinito de stop/start.
        /// </param>
        private void EtwStartSessionCore(bool skipWatchdogReset)
        {
            try
            {
                bool admin = IsElevated();
                if (!admin)
                {
                    _etwStatus = "requer executar como administrador";
                    return;
                }

                // ── CAUSA RAIZ do "kit e admin e mesmo assim pede admin" ────────────
                // A sessão acima (SYSTEM_LOGGER_MODE = "Receive events from
                // SystemTraceProvider", evntrace.h 0x02000000) está correta, mas o
                // provedor de sistema só pode ser habilitado por quem tem
                // **SeSystemProfilePrivilege** ("Profile system performance") HABILITADO
                // no token — e o Windows entrega essa permissão DESABILITADA, mesmo para
                // um processo já elevado (por isso o EnableTrace devolvia
                // ERROR_ACCESS_DENIED = 0x5 sem nada de errado com o resto).
                _privStatus = EnableKernelTracePrivileges();

                // Buffer de propriedades: props + logger name + log file name (1024 cada).
                // 2KB TOTAL dá ERROR_MORE_DATA (0x18): a doc exige >= sizeof(props) +
                // (offset do nome + 1024 chars de nome). Usar 4KB folgado.
                _propsBuf = Marshal.AllocHGlobal(Props_Size + 4096);
                ZeroMemory(_propsBuf, Props_Size + 4096);

                Marshal.WriteInt32(_propsBuf, Props_WnodeBufferSize, Props_Size + 4096);
                Marshal.WriteInt32(_propsBuf, Props_WnodeFlags, (int)WNODE_FLAG_TRACED_GUID);
                Marshal.WriteInt32(_propsBuf, Props_WnodeClientContext, 1); // QPC clock
                byte[] guid = SystemTraceControlGuid.ToByteArray();
                Marshal.Copy(guid, 0, _propsBuf + Props_WnodeGuid, 16); // Wnode.Guid @24
                Marshal.WriteInt32(_propsBuf, Props_LogFileMode,
                    (int)(EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_SYSTEM_LOGGER_MODE));
                Marshal.WriteInt32(_propsBuf, Props_FlushTimer, 1);
                Marshal.WriteInt32(_propsBuf, Props_EnableFlags, (int)(EVENT_TRACE_FLAG_DPC | EVENT_TRACE_FLAG_INTERRUPT));
                // Kernel logger exige LogFileNameOffset válido mesmo em real-time:
                // logger name em +120 (1024 bytes), log file name VAZIO em +120+1024.
                Marshal.WriteInt32(_propsBuf, Props_LogFileNameOffset, Props_Size + 1024);
                Marshal.WriteInt32(_propsBuf, Props_LoggerNameOffset, Props_Size);

                // Nome do logger no buffer (KERNEL_LOGGER_NAME)
                WriteUnicodeAt(_propsBuf, Props_Size, KernelLoggerName);

                // ── A sessão "NT Kernel Logger" só existe UMA por boot. Se já houver uma
                //    (LatencyMon, xperf, ou uma órfã de um processo morto), o StartTrace
                //    devolve ERROR_ALREADY_EXISTS — mas ETW aceita VÁRIOS consumidores em
                //    tempo real. Então, em vez de desistir, ANEXAMOS como consumidor (não
                //    mexemos na sessão alheia). Antes disso, uma QUERY nos diz se as flags
                //    de DPC/ISR estão habilitadas nela.
                bool attached = false;
                uint rc = ControlTraceW(0, KernelLoggerName, _propsBuf, EVENT_TRACE_CONTROL_QUERY);
                if (rc == 0)
                {
                    uint flags = (uint)Marshal.ReadInt32(_propsBuf, Props_EnableFlags);
                    bool temFlags = (flags & (EVENT_TRACE_FLAG_DPC | EVENT_TRACE_FLAG_INTERRUPT)) != 0;
                    attached = temFlags;
                    _etwStatus = temFlags
                        ? "anexada à sessão de kernel já existente"
                        : $"sessão de kernel existente SEM DPC/ISR (flags=0x{flags:X}) — feche o programa que a usa";
                    if (!temFlags) { CleanupEtwBuffers(); return; }
                }

                if (!attached)
                {
                    // Limpa qualquer resíduo da QUERY antes de reusar o buffer no StartTrace
                    ZeroMemory(_propsBuf, Props_Size + 4096);
                    Marshal.WriteInt32(_propsBuf, Props_WnodeBufferSize, Props_Size + 4096);
                    Marshal.WriteInt32(_propsBuf, Props_WnodeFlags, (int)WNODE_FLAG_TRACED_GUID);
                    Marshal.WriteInt32(_propsBuf, Props_WnodeClientContext, 1);
                    Marshal.Copy(guid, 0, _propsBuf + Props_WnodeGuid, 16);
                    Marshal.WriteInt32(_propsBuf, Props_LogFileMode,
                        (int)(EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_SYSTEM_LOGGER_MODE));
                    Marshal.WriteInt32(_propsBuf, Props_FlushTimer, 1);
                    Marshal.WriteInt32(_propsBuf, Props_EnableFlags, (int)(EVENT_TRACE_FLAG_DPC | EVENT_TRACE_FLAG_INTERRUPT));
                    Marshal.WriteInt32(_propsBuf, Props_LogFileNameOffset, Props_Size + 1024);
                    Marshal.WriteInt32(_propsBuf, Props_LoggerNameOffset, Props_Size);
                    WriteUnicodeAt(_propsBuf, Props_Size, KernelLoggerName);

                    rc = StartTraceW(out _etwSessionHandle, KernelLoggerName, _propsBuf);
                    if (rc == 183) // ERROR_ALREADY_EXISTS: alguém criou no meio do caminho — anexa
                    {
                        attached = true;
                        _etwSessionHandle = 0;
                    }
                    else if (rc != 0)
                    {
                        _etwStatus = $"Não foi possível abrir a sessão de kernel: {DescribeWin32(rc)}";
                        CleanupEtwBuffers();
                        return;
                    }
                    else
                    {
                        // PROVA EM BANCADA (máquina real, 18-19/09): a órfã encontrada no host
                        // tinha EnableFlags=0x60 e "escritos=1236" com 11,5 milhões de eventos
                        // gerados — ou seja, UMA SESSÃO CRIADA COM FLAGS DE KERNEL JÁ FLUI.
                        // Para system loggers, o EnableFlags no StartTrace É a habilitação do
                        // provedor de sistema (EnableTraceEx2 com SystemTraceControlGuid é
                        // redundante e o EnableTrace legado devolve ERROR_ACCESS_DENIED mesmo
                        // com a sessão saudável — era ISSO que matava a sessão boa aqui).
                        //
                        // Tentativa best-effort de habilitação explícita (alguns builds exigem);
                        // qualquer falha NÃO mata mais a sessão — o watchdog de 12s decide com
                        // base em EVIDÊNCIA (eventos chegando ou não), não em rc.
                        var ctl = SystemTraceControlGuid;
                        ulong kw = EVENT_TRACE_FLAG_DPC | EVENT_TRACE_FLAG_INTERRUPT;
                        uint rcEn2 = EnableTraceEx2(_etwSessionHandle, ref ctl, EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                            (byte)TRACE_LEVEL_VERBOSE, kw, 0, 0, IntPtr.Zero);
                        if (rcEn2 != 0)
                        {
                            EnableTrace(1, EVENT_TRACE_FLAG_DPC | EVENT_TRACE_FLAG_INTERRUPT, 0, ref ctl, _etwSessionHandle);
                        }
                        _etwStatus = rcEn2 == 0
                            ? "ativa (provedor de sistema habilitado via EnableTraceEx2)"
                            : "ativa (flags de kernel na criação; confirmação de fluxo em ~12s)";
                    }
                }

                // Consumidor: EVENT_TRACE_LOGFILEW preenchido em buffer cru (offsets acima)
                IntPtr lf = Marshal.AllocHGlobal(LogFile_Size);
                ZeroMemory(lf, LogFile_Size);
                _loggerNameHandle = GCHandle.Alloc(KernelLoggerName, GCHandleType.Pinned);
                Marshal.WriteIntPtr(lf, LogFile_LoggerName, _loggerNameHandle.AddrOfPinnedObject());
                Marshal.WriteInt32(lf, LogFile_ProcessTraceMode, (int)(EVENT_TRACE_REAL_TIME_MODE | PROCESS_TRACE_MODE_EVENT_RECORD));
                EventRecordCallback cb = OnEtwEvent;
                _callbackHandle = GCHandle.Alloc(cb);
                Marshal.WriteIntPtr(lf, LogFile_EventRecordCallback, Marshal.GetFunctionPointerForDelegate(cb));
                _etwTraceHandle = OpenTraceWFromPtr(lf);

                if (_etwTraceHandle == 0xFFFFFFFFFFFFFFFF || _etwTraceHandle == 0xFFFFFFFF)
                {
                    _etwStatus = "OpenTrace falhou (INVALID_HANDLE_VALUE)";
                    Marshal.FreeHGlobal(lf);
                    EtwStopSession();
                    return;
                }

                _etwThread = new Thread(() =>
                {
                    try
                    {
                        ulong h = _etwTraceHandle;
                        ProcessTrace(ref h, 1, IntPtr.Zero, IntPtr.Zero);
                    }
                    catch { }
                    finally
                    {
                        try { if (_etwTraceHandle != 0) CloseTrace(_etwTraceHandle); } catch { }
                    }
                })
                { IsBackground = true, Name = "KitLugia-ETW-Consumer" };
                _etwThread.Start();

                _etwActive = true;
                _etwStartedUtc = DateTime.UtcNow;
                if (attached && !_etwStatus.StartsWith("ativa")) _etwStatus = "ativa (anexada à sessão de kernel existente)";
                if (!string.IsNullOrEmpty(_privStatus)) _etwStatus += $" | privilegios: {_privStatus}";
                _etwStatus += $" | sessao: {QueryKernelSession()}";
                RefreshKernelModules();
            }
            catch (Exception ex)
            {
                _etwStatus = $"exceção: {ex.Message}";
                try { EtwStopSession(); } catch { }
            }
        }

        // EnableTraceEx2 (moderno): é o caminho documentado para habilitar o provedor de
        // sistema (SystemTraceProvider) usando as flags de kernel como KEYWORDS. O
        // EnableTrace legado devolve ERROR_ACCESS_DENIED (0x5) em system logger.
        private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
        private const ulong TRACE_LEVEL_VERBOSE = 5;

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode,
            byte level, ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

        // ═══════════════════ Privilégios do NT Kernel Logger ═══════════════════
        // SeSystemProfilePrivilege vem DESABILITADO no token até alguém pedir, mesmo
        // num processo elevado. Sem ele, EnableTrace/EnableTraceEx2 no provedor de
        // sistema devolve ERROR_ACCESS_DENIED (0x5) e o kernel não gera DPC/ISR.
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct LuidValue { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenPrivilegesOne
        {
            public uint PrivilegeCount;
            public LuidValue Luid;
            public uint Attributes;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValueW(string? systemName, string name, out LuidValue luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
            ref TokenPrivilegesOne newState, uint bufferLength, IntPtr previousState, out uint returnLength);

        /// <summary>
        /// Habilita SeSystemProfilePrivilege (+ SeDebugPrivilege) no token do processo.
        /// Devolve um resumo legível para o status/log — é o diagnóstico definitivo do
        /// "pede admin sendo admin".
        /// </summary>
        private static string EnableKernelTracePrivileges()
        {
            var partes = new List<string>();
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
                return $"OpenProcessToken falhou (0x{Marshal.GetLastWin32Error():X})";
            try
            {
                foreach (string nome in new[] { "SeSystemProfilePrivilege", "SeDebugPrivilege" })
                {
                    if (!LookupPrivilegeValueW(null, nome, out LuidValue luid))
                    {
                        partes.Add($"{nome}=ausente");
                        continue;
                    }
                    var tp = new TokenPrivilegesOne
                    {
                        PrivilegeCount = 1,
                        Luid = luid,
                        Attributes = SE_PRIVILEGE_ENABLED
                    };
                    bool ok = AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, out _);
                    // ERROR_NOT_ALL_ASSIGNED (1300) = a permissão não está no token do usuário
                    int err = Marshal.GetLastWin32Error();
                    partes.Add(err == 0
                        ? $"{nome}=OK"
                        : $"{nome}={(ok ? "naoAtribuida" : "falhou")}(0x{err:X})");
                }
            }
            finally { CloseHandle(token); }
            return string.Join(" ", partes);
        }

        /// <summary>
        /// Diagnóstico de suporte: o que a sessão 'NT Kernel Logger' tem AGORA (modo, flags de
        /// kernel e contadores). Se EnableFlags vier 0, o kernel nem gerou eventos de DPC/ISR;
        /// se vier 0x60 (DPC|INTERRUPT) e a lista seguir vazia, o problema está no consumidor.
        /// </summary>
        public static string QueryKernelSession()
        {
            IntPtr buf = IntPtr.Zero;
            try
            {
                int size = Props_Size + 4096;
                buf = Marshal.AllocHGlobal(size);
                ZeroMemory(buf, size);
                Marshal.WriteInt32(buf, Props_WnodeBufferSize, size);
                WriteUnicodeAt(buf, Props_Size, KernelLoggerName);
                Marshal.WriteInt32(buf, Props_LoggerNameOffset, Props_Size);
                uint rc = ControlTraceW(0, KernelLoggerName, buf, EVENT_TRACE_CONTROL_QUERY);
                if (rc != 0) return $"sem sessão ativa (rc=0x{rc:X}/{rc})";
                uint mode = (uint)Marshal.ReadInt32(buf, Props_LogFileMode);
                uint flags = (uint)Marshal.ReadInt32(buf, Props_EnableFlags);
                uint nbuf = (uint)Marshal.ReadInt32(buf, 80);   // NumberOfBuffers
                uint free = (uint)Marshal.ReadInt32(buf, 84);   // FreeBuffers
                uint lost = (uint)Marshal.ReadInt32(buf, 88);   // EventsLost
                uint written = (uint)Marshal.ReadInt32(buf, 92); // BuffersWritten
                return $"LogFileMode=0x{mode:X} (rt={(mode & EVENT_TRACE_REAL_TIME_MODE) != 0} sysLogger={(mode & EVENT_TRACE_SYSTEM_LOGGER_MODE) != 0}) " +
                       $"EnableFlags=0x{flags:X} (dpc={(flags & EVENT_TRACE_FLAG_DPC) != 0} isr={(flags & EVENT_TRACE_FLAG_INTERRUPT) != 0}) " +
                       $"buffers={nbuf} livres={free} escritos={written} eventosPerdidos={lost}";
            }
            catch (Exception ex) { return "erro: " + ex.Message; }
            finally { if (buf != IntPtr.Zero) { try { Marshal.FreeHGlobal(buf); } catch { } } }
        }

        [DllImport("advapi32.dll", EntryPoint = "OpenTraceW", CharSet = CharSet.Unicode)]
        private static extern ulong OpenTraceW_Raw(IntPtr logfile);
        private static ulong OpenTraceWFromPtr(IntPtr logfile) => OpenTraceW_Raw(logfile);

        private void EtwStopSession()
        {
            _etwActive = false;
            try
            {
                if (_etwSessionHandle != 0 && _propsBuf != IntPtr.Zero)
                {
                    ControlTraceW(_etwSessionHandle, KernelLoggerName, _propsBuf, EVENT_TRACE_CONTROL_STOP);
                }
            }
            catch { }
            try { if (_etwThread?.Join(3000) != true) { /* consumer sai quando a sessão para */ } } catch { }
            try { if (_etwTraceHandle != 0) CloseTrace(_etwTraceHandle); } catch { }
            _etwTraceHandle = 0;
            _etwSessionHandle = 0;
            try { if (_callbackHandle.IsAllocated) { _callbackHandle.Free(); } } catch { }
            try { if (_loggerNameHandle.IsAllocated) { _loggerNameHandle.Free(); } } catch { }
            CleanupEtwBuffers();
            if (_etwStatus.StartsWith("ativa")) _etwStatus = "parada";
        }

        private void CleanupEtwBuffers()
        {
            try { if (_propsBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_propsBuf); } } catch { }
            _propsBuf = IntPtr.Zero;
        }

        private static void ZeroMemory(IntPtr p, int len)
        {
            for (int i = 0; i < len; i += 8) Marshal.WriteInt64(p, i, 0);
        }

        private static void WriteUnicodeAt(IntPtr buf, int off, string s)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(s + "\0");
            Marshal.Copy(bytes, 0, buf + off, bytes.Length);
        }

        /// <summary>Tradutor dos erros do ETW/Win32 para texto que o usuário entende.</summary>
        private static string DescribeWin32(uint rc) => rc switch
        {
            0 => "ok",
            5 => "acesso negado (0x5) — o Windows recusou; abra o Kit como administrador",
            87 => "parâmetro inválido (0x57)",
            183 => "a sessão já existe (0xB7)",
            24 => "buffer de propriedades pequeno demais (0x18)",
            8 => "memória insuficiente (0x8)",
            4201 => "sessão não encontrada (0x1069)",
            _ => $"rc=0x{rc:X}",
        };

        private static bool IsElevated()
        {
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // ── Callback do consumidor (thread do ProcessTrace) ──

        private void OnEtwEvent(IntPtr rec)
        {
            try
            {
                if (rec == IntPtr.Zero) return;
                Interlocked.Increment(ref _etwSessionTotalEvents); // QUALQUER evento = sessão fluindo
                // Só nos interessam eventos do grupo PerfInfo (kernel DPC/ISR)
                byte[] g = new byte[16];
                Marshal.Copy(rec + Erec_ProviderId, g, 0, 16);
                if (!new Guid(g).Equals(PerfinfoGuid)) return;

                _etwEventsSeen++;
                byte opcode = Marshal.ReadByte(rec, Erec_Opcode);
                lock (_lock) { _etwOpcodeHisto[opcode] = _etwOpcodeHisto.TryGetValue(opcode, out int c) ? c + 1 : 1; }
                int udLen = Marshal.ReadInt16(rec, Erec_UserDataLength);
                IntPtr ud = Marshal.ReadIntPtr(rec, Erec_UserData);
                if (ud == IntPtr.Zero || udLen < 16) return;

                // PERFINFO_DPC/ISR: { ULONGLONG InitialTime; PVOID Routine; }
                long routine = Marshal.ReadInt64(ud, 8);
                if (routine <= 0x10000) return;
                _etwRoutinesSeen++;

                long ts = Marshal.ReadInt64(rec, Erec_TimeStamp);
                int pid = Marshal.ReadInt32(rec, Erec_ProcessId);
                int tid = Marshal.ReadInt32(rec, Erec_ThreadUid(Erec_ThreadId));
                bool isDpc = opcode == 0x27 || opcode == 0x2B || opcode == 0x2C || opcode == 0x2D; // DPC/TimerDPC/DpcComplete variants conhecidas; não-DPC cai em ISR-like
                RecordDriverHit(routine, isDpc, ts, pid, tid);
            }
            catch { }
        }

        private static int Erec_ThreadUid(int baseOff) => baseOff; // clareza: ThreadId@8 no EVENT_HEADER

        private void RecordDriverHit(long routine, bool isDpc, long ts100ns, int pid, int tid)
        {
            RefreshKernelModules();
            string mod = FindModule(routine);
            if (mod.Length == 0) { _etwUnmatchedRoutines++; mod = $"0x{routine:X12}"; }

            // duração aproximada: pareia com o evento anterior no MESMO núcleo
            // (núcleo não vem no EVENT_HEADER clássico — usa tid como chave de pareamento pior-caso)
            long durUs = 0;
            var st = GetOrAddDriver(mod); // I/O de descrição fora do lock
            lock (_lock)
            {
                if (_dpcPairByCore.TryGetValue(tid, out var prev) && ts100ns > prev.Ticks)
                    durUs = Math.Min((ts100ns - prev.Ticks) / 10, 100_000); // teto 100ms (ruído vira outlier)
                _dpcPairByCore[tid] = (mod, ts100ns);
                if (_dpcPairByCore.Count > 256) _dpcPairByCore.Clear(); // defesa contra flood de tids

                if (isDpc) { st.DpcCount++; if (durUs > st.MaxDpcUs) st.MaxDpcUs = durUs; }
                else { st.IsrCount++; if (durUs > st.MaxIsrUs) st.MaxIsrUs = durUs; }
                st.TotalUs += durUs;
                if (st.MaxDpcUs > _maxDpcUsSession) _maxDpcUsSession = st.MaxDpcUs;
                if (st.MaxIsrUs > _maxIsrUsSession) _maxIsrUsSession = st.MaxIsrUs;
            }
        }

        private void RefreshKernelModules()
        {
            if ((DateTime.UtcNow - _modsStamp).TotalSeconds < 5) return;
            _modsStamp = DateTime.UtcNow;
            try { _kernelMods = NativeMetricsHelper.GetKernelModules()?.ToArray() ?? Array.Empty<NativeMetricsHelper.KernelModule>(); }
            catch { }
        }

        private string FindModule(long addr)
        {
            foreach (var m in _kernelMods)
                if (addr >= m.Base && addr < m.Base + (long)m.Size)
                    return m.Name;
            return "";
        }

        private static string DescribeDriverManual(string name) => name.ToLowerInvariant() switch
        {
            "nvlddmkm.sys" => "Driver de vídeo NVIDIA",
            "hdaudbus.sys" => "Barramento de áudio HD (placa-mãe)",
            "usbxhci.sys" or "usbport.sys" => "Controladora USB",
            "wdf01000.sys" or "wdf01100.sys" => "Framework de drivers do Windows (KMDF)",
            "ndis.sys" => "Rede (Windows)",
            "wdfilter.sys" or "WdFilter.sys" => "Filtro do Microsoft Defender",
            "tcpip.sys" => "Pilha TCP/IP",
            "storport.sys" or "stornvme.sys" => "Armazenamento (NVMe/SATA)",
            "ACPI.sys" => "Gerenciamento de energia ACPI",
            "dxgkrnl.sys" => "Subsistema gráfico do Windows",
            "ntoskrnl.exe" => "Núcleo do Windows",
            "hal.dll" => "Camada de abstração de hardware",
            "classpnp.sys" => "Classe de dispositivos de armazenamento",
            "usbhub.sys" or "usbhub3.sys" => "Hub USB",
            "dxgmms2.sys" => "Gerenciamento de memória gráfica",
            "afd.sys" => "Sockets de rede (Winsock)",
            "netio.sys" => "I/O de rede",
            "volmgr.sys" => "Gerenciador de volumes",
            "volsnap.sys" => "Cópias de sombra (pontos de restauração)",
            "mup.sys" => "Redirecionador de rede (UNC)",
            _ => "",
        };

        private readonly Dictionary<string, string> _driverDescCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Nome amigável do driver. Para os conhecidos, texto escrito à mão; para o resto,
        /// a descrição/empresa do PRÓPRIO .sys em System32\drivers (FileVersionInfo) —
        /// resolvido UMA vez por driver e cacheado, sempre FORA do lock do ETW.
        /// </summary>
        private string DescribeDriver(string name)
        {
            if (_driverDescCache.TryGetValue(name, out var cached)) return cached;

            string result = DescribeDriverManual(name);
            if (result.Length == 0)
            {
                try
                {
                    string sys32 = Environment.SystemDirectory;
                    foreach (var dir in new[] { sys32 + "\\drivers\\", sys32 + "\\" })
                    {
                        string p = dir + name;
                        if (!System.IO.File.Exists(p)) continue;
                        var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(p);
                        string fd = (vi.FileDescription ?? "").Trim();
                        string comp = (vi.CompanyName ?? "").Trim();
                        if (fd.Length > 0) { result = comp.Length > 0 && comp != fd ? $"{fd} ({comp})" : fd; break; }
                    }
                }
                catch { }
            }
            if (result.Length == 0) result = "Driver de kernel do sistema";
            _driverDescCache[name] = result;
            return result;
        }

        /// <summary>Pega (ou cria) o acumulador do driver SEM fazer I/O de disco sob o lock.</summary>
        private DriverStats GetOrAddDriver(string mod)
        {
            lock (_lock) { if (_drivers.TryGetValue(mod, out var s)) return s; }
            string desc = DescribeDriver(mod);
            lock (_lock)
            {
                if (_drivers.TryGetValue(mod, out var s2)) return s2;
                var st = new DriverStats { Name = mod, Description = desc };
                _drivers[mod] = st;
                return st;
            }
        }

        // ═══════════════════════════════ Win32 (janelas travadas) ═══════════════════════════════

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int max);
        private static bool User32IsHungAppWindow(IntPtr h) => IsHungAppWindow(h);

        public void Dispose() => Stop();
    }

    // Extensão pontual: ponteiro + offset (o Core usa em vários parsers)
    internal static class PtrExt
    {
        public static IntPtr Add(this IntPtr p, int off) => (IntPtr)(p.ToInt64() + off);
    }
}
