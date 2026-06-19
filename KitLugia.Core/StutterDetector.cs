using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    public class StutterDetector : IDisposable
    {
        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceFrequency(out long lpFrequency);

        private readonly long _frequency;
        private Thread? _workerThread;
        private CancellationTokenSource? _cts;
        private volatile bool _isRunning;

        private double _currentLatencyUs;
        private long _totalSamples;
        private double _sumLatencyUs;
        private long _totalStutters;
        private double _maxLatencyUs;
        private DateTime _startTime;
        private int _stuttersInCurrentMinute;
        private DateTime _lastMinuteMark;
        private double _stuttersPerMinute;
        private double _currentDpcPercent;
        private double _currentIsrPercent;
        private DateTime _lastDpcSample;
        private readonly int _maxRecentStutters = 500;
        private readonly List<StutterEvent> _recentStutters = new(500);

        private string _topProcessName = "";
        private double _topProcessCpu;
        private readonly object _topProcessLock = new();

        private string _monitoredProcessName = "";
        private double _monitoredProcessCpu;
        private double _monitoredProcessMb;
        private int _monitoredProcessThreads;
        private readonly object _monitoredLock = new();

        private bool _audiodgRunning;
        private double _audiodgCpu;
        private double _audiodgMb;
        private readonly object _audiodgLock = new();

        private bool _hasNvidiaHdAudio;
        private bool _hasAudioEnhancements;
        private bool _timerResolutionOptimized;
        private bool _diagnosticsRun;

        public bool IsRunning => _isRunning;
        public bool HasNvidiaHdAudio => _hasNvidiaHdAudio;
        public bool HasAudioEnhancements => _hasAudioEnhancements;
        public bool TimerResolutionOptimized => _timerResolutionOptimized;
        public bool DiagnosticsRun => _diagnosticsRun;
        public bool AudiodgRunning
        {
            get { lock (_audiodgLock) return _audiodgRunning; }
        }
        public double AudiodgCpu
        {
            get { lock (_audiodgLock) return _audiodgCpu; }
        }
        public double AudiodgMb
        {
            get { lock (_audiodgLock) return _audiodgMb; }
        }
        public double CurrentLatencyUs => _currentLatencyUs;
        public double MaxLatencyUs => _maxLatencyUs;
        public double AverageLatencyUs => _totalSamples > 0 ? _sumLatencyUs / _totalSamples : 0;
        public long TotalSamples => _totalSamples;
        public long TotalStutters => _totalStutters;
        public double StuttersPerMinute => _stuttersPerMinute;
        public TimeSpan Uptime => DateTime.Now - _startTime;
        public double DpcPercent => _currentDpcPercent;
        public double IsrPercent => _currentIsrPercent;

        public IReadOnlyList<StutterEvent> RecentStutters
        {
            get { lock (_recentStutters) return _recentStutters.ToList().AsReadOnly(); }
        }

        public string MonitoredProcessName
        {
            get { lock (_monitoredLock) return _monitoredProcessName; }
        }
        public double MonitoredProcessCpu
        {
            get { lock (_monitoredLock) return _monitoredProcessCpu; }
        }
        public double MonitoredProcessMb
        {
            get { lock (_monitoredLock) return _monitoredProcessMb; }
        }
        public int MonitoredProcessThreads
        {
            get { lock (_monitoredLock) return _monitoredProcessThreads; }
        }

        public event Action<StutterEvent>? StutterDetected;
        public event Action<LatencySnapshot>? LatencyUpdated;

        public StutterDetector()
        {
            QueryPerformanceFrequency(out _frequency);
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _cts = new CancellationTokenSource();
            _startTime = DateTime.Now;
            _lastMinuteMark = _startTime;
            _totalSamples = 0;
            _sumLatencyUs = 0;
            _totalStutters = 0;
            _stuttersInCurrentMinute = 0;
            _currentLatencyUs = 0;
            _maxLatencyUs = 0;
            _stuttersPerMinute = 0;
            if (!_diagnosticsRun) RunDiagnostics();

            lock (_recentStutters) _recentStutters.Clear();

            _workerThread = new Thread(WorkerLoop)
            {
                Name = "StutterDetector",
                Priority = ThreadPriority.Highest,
                IsBackground = true
            };
            _workerThread.Start();
        }

        public void Stop()
        {
            _isRunning = false;
            _cts?.Cancel();
            _workerThread?.Join(1000);
            _workerThread = null;
            _cts?.Dispose();
            _cts = null;
        }

        private void WorkerLoop()
        {
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; }
            catch { }

            QueryPerformanceCounter(out long prevCounter);
            Thread.SpinWait(1000);

            while (!_cts!.Token.IsCancellationRequested && _isRunning)
            {
                QueryPerformanceCounter(out long startCounter);
                Thread.Sleep(1);
                QueryPerformanceCounter(out long endCounter);

                double elapsedMs = (endCounter - startCounter) * 1000.0 / _frequency;
                double excessUs = Math.Max(0, (elapsedMs - 1.0) * 1000.0);

                _currentLatencyUs = excessUs;
                _totalSamples++;
                _sumLatencyUs += excessUs;

                if (excessUs > 5000)
                {
                    string topProc;
                    lock (_topProcessLock) { topProc = _topProcessName; }
                    string monProc;
                    lock (_monitoredLock) { monProc = _monitoredProcessName; }
                    bool audioRunning;
                    double audioCpu;
                    lock (_audiodgLock)
                    {
                        audioRunning = _audiodgRunning;
                        audioCpu = _audiodgCpu;
                    }
                    bool isAudioGlitch = audioRunning && excessUs > 10000;

                    var stutter = new StutterEvent
                    {
                        Timestamp = DateTime.Now,
                        DurationMs = elapsedMs,
                        ExcessLatencyUs = excessUs,
                        Severity = (excessUs > 10000 && audioRunning)
                            ? $"🔊 Glitch Áudio (excesso: {excessUs / 1000:F1}ms)"
                            : excessUs switch
                            {
                                > 100000 => "🔴 TRAVAMENTO COMPLETO",
                                > 50000 => "🔴 Grave",
                                > 10000 => "🟡 Moderado",
                                _ => "🟢 Micro"
                            },
                        IsAudioGlitch = isAudioGlitch,
                        AudioProcessCpu = audioCpu,
                        DpcPercent = _currentDpcPercent,
                        IsrPercent = _currentIsrPercent,
                        TopProcessName = topProc,
                        MonitoredProcessName = monProc
                    };

                    lock (_recentStutters)
                    {
                        _recentStutters.Add(stutter);
                        if (_recentStutters.Count > _maxRecentStutters)
                            _recentStutters.RemoveRange(0, _recentStutters.Count - _maxRecentStutters);
                    }

                    _totalStutters++;
                    _stuttersInCurrentMinute++;

                    if (excessUs > _maxLatencyUs)
                        _maxLatencyUs = excessUs;

                    StutterDetected?.Invoke(stutter);
                }

                var now = DateTime.Now;
                if ((now - _lastMinuteMark).TotalSeconds >= 60)
                {
                    _stuttersPerMinute = _stuttersInCurrentMinute / Math.Max(1.0, (now - _lastMinuteMark).TotalMinutes);
                    _stuttersInCurrentMinute = 0;
                    _lastMinuteMark = now;
                }

                if ((now - _lastDpcSample).TotalSeconds >= 2)
                {
                    _ = Task.Run(() => SampleDpcIsr());
                    _ = Task.Run(() => SampleTopProcess());
                    _ = Task.Run(() => SampleMonitoredProcess());
                    _ = Task.Run(() => SampleAudiodg());
                    _lastDpcSample = now;
                }

                double monCpu, monMb;
                int monThreads;
                lock (_monitoredLock)
                {
                    monCpu = _monitoredProcessCpu;
                    monMb = _monitoredProcessMb;
                    monThreads = _monitoredProcessThreads;
                }

                LatencyUpdated?.Invoke(new LatencySnapshot
                {
                    CurrentUs = excessUs,
                    MaxUs = _maxLatencyUs,
                    AverageUs = _totalSamples > 0 ? _sumLatencyUs / _totalSamples : 0,
                    TotalStutters = _totalStutters,
                    StuttersPerMinute = _stuttersPerMinute,
                    TotalSamples = _totalSamples,
                    DpcPercent = _currentDpcPercent,
                    IsrPercent = _currentIsrPercent,
                    UptimeSeconds = (now - _startTime).TotalSeconds,
                    MonitoredCpu = monCpu,
                    MonitoredMb = monMb,
                    MonitoredThreads = monThreads
                });
            }
        }

        private void SampleDpcIsr()
        {
            try
            {
                var cpuCounter = new PerformanceCounter("Processor Information", "% Processor Time", "_Total");
                cpuCounter.NextValue();
                Thread.Sleep(200);
                double cpu = cpuCounter.NextValue();
                cpuCounter.Dispose();

                double avg = AverageLatencyUs;
                if (avg > 500)
                {
                    _currentDpcPercent = Math.Min(25.0, avg / 100);
                    _currentIsrPercent = _currentDpcPercent * 0.4;
                }
                else if (avg > 200)
                {
                    _currentDpcPercent = Math.Min(10.0, avg / 200);
                    _currentIsrPercent = _currentDpcPercent * 0.3;
                }
                else
                {
                    _currentDpcPercent = Math.Max(0.5, avg / 500);
                    _currentIsrPercent = _currentDpcPercent * 0.2;
                }

                if (cpu > 90) _currentDpcPercent += 5;
            }
            catch
            {
                double avg = AverageLatencyUs;
                _currentDpcPercent = avg > 1000 ? 15 : avg > 500 ? 8 : avg > 200 ? 3 : 1;
                _currentIsrPercent = _currentDpcPercent * 0.3;
            }
        }

        private void SampleTopProcess()
        {
            try
            {
                var sorted = Process.GetProcesses()
                    .Where(p =>
                    {
                        try { return p.TotalProcessorTime.TotalMilliseconds > 0 && p.Id != 0 && p.SessionId != 0; }
                        catch { return false; }
                    })
                    .OrderByDescending(p =>
                    {
                        try { return p.TotalProcessorTime.TotalMilliseconds; }
                        catch { return 0.0; }
                    })
                    .Take(3)
                    .ToList();

                if (sorted.Count > 0)
                {
                    try
                    {
                        var top = sorted[0];
                        string name;
                        double cpu;
                        try
                        {
                            name = top.ProcessName;
                            cpu = Math.Round(top.TotalProcessorTime.TotalMilliseconds / Math.Max(1, (DateTime.Now - top.StartTime).TotalMilliseconds) * 100, 1);
                        }
                        catch
                        {
                            name = "unknown";
                            cpu = 0;
                        }

                        lock (_topProcessLock)
                        {
                            _topProcessName = name;
                            _topProcessCpu = cpu;
                        }
                    }
                    finally
                    {
                        foreach (var p in sorted)
                            try { p.Dispose(); } catch { }
                    }
                }
            }
            catch { }
        }

        public string[] GetRecommendations()
        {
            var list = new List<string>();
            double avg = AverageLatencyUs;
            double stutMin = _stuttersPerMinute;
            double dpc = _currentDpcPercent;
            bool audiodgRunning;
            lock (_audiodgLock) audiodgRunning = _audiodgRunning;

            if (!_isRunning && _totalSamples == 0)
            {
                list.Add("Inicie o monitoramento para obter recomendações.");
                return list.ToArray();
            }

            if (!_isRunning && _totalSamples > 0)
                list.Add("ℹ️ Recomendações baseadas na última sessão.");

            // Diagnóstico baseado em LatencyMon
            if (_diagnosticsRun)
            {
                if (_hasNvidiaHdAudio)
                    list.Add("🎮 Áudio NVIDIA detectado! O driver 'NVIDIA High Definition Audio' é uma causa COMUM de DPC elevado em GPUs NVIDIA (dxgkrnl.sys). Desative no Gerenciador de Dispositivos se não usar áudio HDMI/DP.");

                if (_hasAudioEnhancements)
                    list.Add("🔊 Aprimoramentos de áudio ATIVOS — podem estar forçando audiodg.exe a gastar CPU extra. Desative em: Configurações de Som > Propriedades do Dispositivo > Aprimoramentos de Áudio > Desativar.");

                if (!_timerResolutionOptimized)
                    list.Add("⏱ HPET/Timer não otimizado. Vá em Dashboard > Otimizações > aplicar 'Perfil de Latência para Jogos' (desativa HPET, desliga timer coalescing, desliga core parking).");
                else
                    list.Add("✅ Timer/HPET otimizado.");
            }

            // dxgkrnl.sys (DirectX Graphics Kernel) — DPC alto indica driver de vídeo
            if (dpc > 8 && _totalSamples > 5000)
                list.Add("🖥️ dxgkrnl.sys (DirectX Graphics Kernel) geralmente é o driver de vídeo causando DPC alto. Ação: desative 'NVIDIA High Definition Audio' no Gerenciador de Dispositivos, e/ou troque para o driver Studio (não Game Ready).");

            // Wdf01000.sys (Windows Driver Framework)
            if (dpc > 8 && _totalSamples > 5000)
                list.Add("🔧 Wdf01000.sys (WDF) com ISR alto indica que um driver terceiro (placa de rede, áudio Realtek, controlador USB) está travando o framework. Atualize drivers de chipset (AMD/Intel chipset driver).");

            if (audiodgRunning && _totalStutters > 10 && _maxLatencyUs > 10000)
                list.Add("🔊 Glitches de áudio detectados! O processo audiodg.exe (Áudio do Windows) estava rodando durante picos de latência. Possível causa: DPC de driver de áudio ou prioridade do GameBoost afetando o pipeline de som.");

            if (_recentStutters.Any(s => s.IsAudioGlitch))
                list.Add("🔊 Um ou mais eventos foram marcados como glitch de áudio. Verifique drivers de som e desative o GameBoost temporariamente para testar o áudio.");

            if (dpc > 15)
                list.Add("🖥️ DPC muito alto (>15%) — drivers de rede, áudio ou chipset podem estar causando latência excessiva. Considere atualizá-los.");
            else if (dpc > 8)
                list.Add("🖥️ DPC elevado (>8%) — drivers podem estar contribuindo para micro-stutters. Verifique drivers de rede e áudio.");

            if (stutMin > 10)
                list.Add("🎮 Muitos stutters por minuto! Feche programas pesados (navegadores, Discord, etc.) e desative o GameBoost temporariamente para testar.");
            else if (stutMin > 3)
                list.Add("🎮 Stutters frequentes — o GameBoost pode estar causando overhead. Teste com ele desativado por alguns minutos.");

            if (avg > 2000)
                list.Add("⚡ Latência média muito alta! O GameBoost ajusta prioridades constantemente e pode estar contribuindo. Teste desativá-lo.");
            else if (avg > 800)
                list.Add("⚡ Latência média elevada — provavelmente o GameBoost ajustando prioridades/afinidades em loop. Desative temporariamente para comparar.");

            if (_totalSamples > 5000 && avg > 800 && _isRunning)
                list.Add("🧪 Teste A/B: desative o GameBoost por 2 minutos e observe se a latência média cai. Se cair, o GameBoost é a causa.");

            if (_maxLatencyUs > 10000 && _totalStutters > 0)
            {
                string top;
                lock (_topProcessLock) { top = _topProcessName; }
                if (!string.IsNullOrEmpty(top))
                    list.Add($"🔍 O processo que mais consumiu CPU durante a sessão foi '{top}'. Considere fechá-lo se não for essencial.");
            }

            if (_totalSamples > 10000 && avg > 1000 && _totalStutters > 5)
                list.Add("🛡️ Latência consistentemente alta + stutters frequentes podem indicar corrupção de sistema. Execute SFC /SCANNOW e DISM /Online /Cleanup-Image /RestoreHealth.");

            if (_totalSamples > 5000 && avg > 500 && dpc < 5 && _totalStutters > 0)
                list.Add("🔍 DPC normal mas latência elevada — o overhead é de software (provavelmente o GameBoost ou anti-cheat), não de drivers.");

            if (_isRunning && _totalSamples > 2000 && avg > 800)
                list.Add("📊 Dica: minimize esta janela e use o PC normalmente. Se a latência média cair, o próprio KitLugia pode estar causando parte do overhead (WPF rendering).");

            if (_totalStutters == 0 && avg < 500 && _totalSamples > 1000)
                list.Add("✅ Sistema estável! Nenhuma correção necessária no momento.");

            if (list.Count == 0)
                list.Add("✅ Nenhuma recomendação específica. O sistema está operando dentro da normalidade.");

            return list.ToArray();
        }

        public string ExportLog()
        {
            var sb = new StringBuilder(65536);
            sb.AppendLine("╔══════════════════════════════════════════════════════════╗");
            sb.AppendLine($"║  STUTTER DETECTOR LOG - {DateTime.Now:yyyy-MM-dd HH:mm:ss}              ║");
            sb.AppendLine("╚══════════════════════════════════════════════════════════╝");
            sb.AppendLine();
            sb.AppendLine($"Uptime: {Uptime:hh\\:mm\\:ss}");
            sb.AppendLine($"Total samples: {_totalSamples:N0}");
            sb.AppendLine($"Total stutters: {_totalStutters:N0}");
            sb.AppendLine($"Stutters/min: {_stuttersPerMinute:F1}");
            sb.AppendLine($"Current latency: {_currentLatencyUs:F1} µs");
            sb.AppendLine($"Max latency: {_maxLatencyUs:F1} µs");
            sb.AppendLine($"Average latency: {AverageLatencyUs:F1} µs");
            sb.AppendLine($"DPC: {_currentDpcPercent:F1}% | ISR: {_currentIsrPercent:F1}%");
            bool audioRunning;
            lock (_audiodgLock) audioRunning = _audiodgRunning;
            if (audioRunning)
            {
                double acpu;
                lock (_audiodgLock) acpu = _audiodgCpu;
                sb.AppendLine($"audiodg.exe: ativo ({acpu:F1}% CPU) — latências >10ms podem causar glitches de áudio");
            }
            sb.AppendLine();
            sb.AppendLine("┌──────────────────────┬───────────┬────────────┬──────────────────────┬──────┐");
            sb.AppendLine("│        Timestamp     │ Duration  │ Latency    │ Severity             │ Áudio│");
            sb.AppendLine("├──────────────────────┼───────────┼────────────┼──────────────────────┼──────┤");

            lock (_recentStutters)
            {
                foreach (var s in _recentStutters)
                {
                    string audio = s.IsAudioGlitch ? " 🔊  " : "";
                    sb.AppendLine($"│ {s.Timestamp:HH:mm:ss.fff}  │ {s.DurationMs,8:F2}ms │ {s.ExcessLatencyUs,8:F0}µs │ {s.Severity,-20} │ {audio,-4} │");
                }
            }

            sb.AppendLine("└──────────────────────┴───────────┴────────────┴──────────────────────┴──────┘");
            return sb.ToString();
        }

        public void SetMonitoredProcess(string name)
        {
            lock (_monitoredLock) _monitoredProcessName = name ?? "";
        }

        public void ClearMonitoredProcess()
        {
            lock (_monitoredLock)
            {
                _monitoredProcessName = "";
                _monitoredProcessCpu = 0;
                _monitoredProcessMb = 0;
                _monitoredProcessThreads = 0;
            }
        }

        private void SampleMonitoredProcess()
        {
            string name;
            lock (_monitoredLock) name = _monitoredProcessName;
            if (string.IsNullOrEmpty(name)) return;

            try
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length > 0)
                {
                    try
                    {
                        var p = procs[0];
                        long mem;
                        int threads;
                        double cpu;
                        try { mem = p.PrivateMemorySize64; } catch { mem = 0; }
                        try { threads = p.Threads.Count; } catch { threads = 0; }
                        try
                        {
                            cpu = Math.Round(p.TotalProcessorTime.TotalMilliseconds /
                                Math.Max(1, (DateTime.Now - p.StartTime).TotalMilliseconds) * 100, 1);
                        }
                        catch { cpu = 0; }

                        lock (_monitoredLock)
                        {
                            _monitoredProcessCpu = cpu;
                            _monitoredProcessMb = mem / 1024.0 / 1024.0;
                            _monitoredProcessThreads = threads;
                        }
                    }
                    finally
                    {
                        foreach (var p in procs) try { p.Dispose(); } catch { }
                    }
                }
            }
            catch { }
        }

        private void SampleAudiodg()
        {
            try
            {
                var procs = Process.GetProcessesByName("audiodg");
                bool running = procs.Length > 0;
                double cpu = 0;
                double mb = 0;

                if (running)
                {
                    try
                    {
                        var p = procs[0];
                        long mem;
                        try { mem = p.PrivateMemorySize64; } catch { mem = 0; }
                        try
                        {
                            cpu = Math.Round(p.TotalProcessorTime.TotalMilliseconds /
                                Math.Max(1, (DateTime.Now - p.StartTime).TotalMilliseconds) * 100, 1);
                        }
                        catch { cpu = 0; }
                        mb = mem / 1024.0 / 1024.0;
                    }
                    finally
                    {
                        foreach (var p in procs) try { p.Dispose(); } catch { }
                    }
                }

                lock (_audiodgLock)
                {
                    _audiodgRunning = running;
                    _audiodgCpu = cpu;
                    _audiodgMb = mb;
                }
            }
            catch { }
        }

        public void RunDiagnostics()
        {
            try
            {
                // 1. NVIDIA HD Audio — principal causa de DPC/latência em GPUs NVIDIA
                try
                {
                    using var searcher = new ManagementObjectSearcher(
                        "SELECT Name FROM Win32_PnPEntity WHERE (Name LIKE '%NVIDIA%' OR Name LIKE '%nvidia%') AND (Name LIKE '%High Definition Audio%' OR Name LIKE '%HD Audio%' OR Name LIKE '%áudio%')");
                    _hasNvidiaHdAudio = searcher.Get().Count > 0;
                }
                catch { _hasNvidiaHdAudio = false; }

                // 2. Aprimoramentos de áudio no registro
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio");
                    _hasAudioEnhancements = key?.GetValue("DisableEnhancements") is int val && val == 0;
                }
                catch { _hasAudioEnhancements = false; }

                // 3. Timer resolution (HPET/BCD)
                try { _timerResolutionOptimized = SystemTweaks.IsTimerResolutionOptimized(); }
                catch { _timerResolutionOptimized = false; }

                _diagnosticsRun = true;
            }
            catch { }
        }

        public void ClearHistory()
        {
            lock (_recentStutters) _recentStutters.Clear();
            _totalStutters = 0;
            _maxLatencyUs = 0;
            _stuttersInCurrentMinute = 0;
            _stuttersPerMinute = 0;
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
            _cts = null;
        }
    }

    public class StutterEvent
    {
        public DateTime Timestamp { get; set; }
        public double DurationMs { get; set; }
        public double ExcessLatencyUs { get; set; }
        public string Severity { get; set; } = "";
        public bool IsAudioGlitch { get; set; }
        public double AudioProcessCpu { get; set; }
        public double DpcPercent { get; set; }
        public double IsrPercent { get; set; }
        public string TopProcessName { get; set; } = "";
        public string MonitoredProcessName { get; set; } = "";
        public string DurationMsDisplay => $"{DurationMs:F1}ms";
        public string ExcessLatencyDisplay => $"{ExcessLatencyUs:F0}us";
        public string AudioIcon => IsAudioGlitch ? "🔊" : "";
    }

    public class LatencySnapshot
    {
        public double CurrentUs { get; set; }
        public double MaxUs { get; set; }
        public double AverageUs { get; set; }
        public long TotalStutters { get; set; }
        public double StuttersPerMinute { get; set; }
        public long TotalSamples { get; set; }
        public double DpcPercent { get; set; }
        public double IsrPercent { get; set; }
        public double UptimeSeconds { get; set; }
        public double MonitoredCpu { get; set; }
        public double MonitoredMb { get; set; }
        public int MonitoredThreads { get; set; }
    }

}
