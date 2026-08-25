using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KitLugia.Core.TaskManager
{
    /// <summary>
    /// GPU utilization via PDH (Performance Data Helper) counters.
    /// FIX: corrige wildcard, remove Thread.Sleep do lock e limpa handles.
    /// </summary>
    public static class GpuMonitor
    {
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQuery(string? szDataSource, uint dwUserData, out IntPtr phQuery);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounter(IntPtr hQuery, string szFullCounterPath, uint dwUserData, out IntPtr phCounter);
        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr hQuery);
        [DllImport("pdh.dll")]
        private static extern uint PdhGetFormattedCounterValue(IntPtr phCounter, uint dwFormat, out IntPtr lpdwType, out PDH_FMT_COUNTERVALUE pdValue);
        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr hQuery);
        [DllImport("pdh.dll")]
        private static extern uint PdhRemoveCounter(IntPtr hCounter);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhExpandWildCardPath(string? szDataSource, string szWildCardPath, StringBuilder? mszExpandedPathList, ref uint pcchPathListLength, uint dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE { public uint CStatus; public double DoubleValue; }

        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const uint PDH_NO_DATA = 0x800007D5;
        private const uint PDH_CSTATUS_VALID_DATA = 0x00000000;
        private const uint PDH_CSTATUS_NEW_DATA = 0x00000001;
        private static IntPtr _queryHandle = IntPtr.Zero;
        private static readonly List<IntPtr> _engineCounters = new();
        private static readonly List<string> _enginePaths = new(); // paralelo a _engineCounters p/ mapear LUID por handle
        private static HashSet<string> _activePaths = new(StringComparer.OrdinalIgnoreCase);
        private static IntPtr _totalUtilCounter = IntPtr.Zero; // fallback single
        private static bool _initialized = false;
        private static bool _gpuAvailable = true;
        private static volatile bool _initializing = false;
        private static DateTime _lastCollectTime = DateTime.MinValue;
        private static double _lastTotalValue = -1;
        private static readonly object _initLock = new();
        // FIX: falha única na inicialização NÃO desabilita para sempre — re-tenta após cooldown.
        private static int _failedAttempts = 0;
        private static DateTime _nextRetryTime = DateTime.MinValue;

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            if (!_gpuAvailable && DateTime.UtcNow < _nextRetryTime) return;
            if (_initializing) return; // avoid re-entrancy storm
            bool doWarmup = false;
            lock (_initLock)
            {
                if (_initialized || _initializing) return;
                _initializing = true;
                try
                {
                    uint r = PdhOpenQuery(null, 0, out _queryHandle);
                    if (r != 0) { MarkUnavailableAndScheduleRetry(); _initializing = false; return; }

                    // Expand wildcard to ALL engine instances (PDH requires expansion, wildcard alone fails)
                    string wildcard = @"\GPU Engine(*)\Utilization Percentage";
                    uint len = 0;
                    // First call to get required length — may return PDH_MORE_DATA or success
                    PdhExpandWildCardPath(null, wildcard, null, ref len, 0);
                    List<string> paths = new();
                    if (len > 1)
                    {
                        var sb = new StringBuilder((int)len);
                        if (PdhExpandWildCardPath(null, wildcard, sb, ref len, 0) == 0)
                        {
                            string all = sb.ToString();
                            // Double-null terminated multi-string
                            foreach (var s in all.Split('\0'))
                            {
                                if (!string.IsNullOrWhiteSpace(s)) paths.Add(s);
                            }
                        }
                    }
                    // If expansion gave nothing, fallback to single path attempt
                    if (paths.Count == 0) paths.Add(wildcard);

                    int added = 0;
                    foreach (var p in paths)
                    {
                        if (PdhAddEnglishCounter(_queryHandle, p, 0, out var h) == 0 && h != IntPtr.Zero)
                        {
                            _engineCounters.Add(h);
                            added++;
                        }
                    }
                    // If we managed to add at least one engine counter
                    if (added > 0)
                    {
                        _totalUtilCounter = _engineCounters[0];
                        _activePaths = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
                        PdhCollectQueryData(_queryHandle);
                        doWarmup = true;
                        _initialized = true;
                        _lastExpandTime = DateTime.UtcNow;
                    }
                    else
                    {
                        // No engine counters — try generic _Total or disable
                        MarkUnavailableAndScheduleRetry();
                        foreach (var h in _engineCounters) try { PdhRemoveCounter(h); } catch { }
                        _engineCounters.Clear();
                        _activePaths.Clear();
                        _totalUtilCounter = IntPtr.Zero;
                        if (_queryHandle != IntPtr.Zero) { try { PdhCloseQuery(_queryHandle); } catch { } _queryHandle = IntPtr.Zero; }
                    }
                }
                catch
                {
                    MarkUnavailableAndScheduleRetry();
                    foreach (var h in _engineCounters) try { PdhRemoveCounter(h); } catch { }
                    _engineCounters.Clear();
                    _activePaths.Clear();
                    _totalUtilCounter = IntPtr.Zero;
                    if (_queryHandle != IntPtr.Zero) { try { PdhCloseQuery(_queryHandle); } catch { } _queryHandle = IntPtr.Zero; }
                }
                finally { _initializing = false; }
            }
            // Warmup MUST be off the UI lock — protegido por lock evita colisão com Shutdown/Reexpand
            if (doWarmup)
            {
                var captured = _queryHandle;
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try { await System.Threading.Tasks.Task.Delay(120); } catch { }
                    lock (_initLock)
                    {
                        try
                        {
                            if (captured != IntPtr.Zero && _queryHandle == captured && _queryHandle != IntPtr.Zero)
                                PdhCollectQueryData(captured);
                        }
                        catch { }
                    }
                });
            }
        }

        private static DateTime _lastExpandTime = DateTime.MinValue;

        private static void TryReexpandIfNeeded()
        {
            // Re-expande a cada 30s para capturar engines criados após o boot (jogo iniciado depois)
            if ((DateTime.UtcNow - _lastExpandTime).TotalSeconds < 30) return;
            lock (_initLock)
            {
                if ((DateTime.UtcNow - _lastExpandTime).TotalSeconds < 30) return;
                if (!_initialized || _queryHandle == IntPtr.Zero) return;
                try
                {
                    string wildcard = @"\GPU Engine(*)\Utilization Percentage";
                    uint len = 0;
                    PdhExpandWildCardPath(null, wildcard, null, ref len, 0);
                    if (len <= 1) return;
                    var sb = new StringBuilder((int)len);
                    if (PdhExpandWildCardPath(null, wildcard, sb, ref len, 0) != 0) return;
                    var freshPaths = new HashSet<string>(sb.ToString().Split('\0', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
                    if (_activePaths.SetEquals(freshPaths)) { _lastExpandTime = DateTime.UtcNow; return; }
                    foreach (var h in _engineCounters) try { PdhRemoveCounter(h); } catch { }
                    _engineCounters.Clear();
                    _activePaths = freshPaths;
                    foreach (var p in freshPaths)
                    {
                        if (PdhAddEnglishCounter(_queryHandle, p, 0, out var h) == 0 && h != IntPtr.Zero) _engineCounters.Add(h);
                    }
                    _totalUtilCounter = _engineCounters.Count > 0 ? _engineCounters[0] : IntPtr.Zero;
                    _lastExpandTime = DateTime.UtcNow;
                    try { PdhCollectQueryData(_queryHandle); } catch { }
                }
                catch { }
            }
        }

        public static double GetTotalGpuUtilization()
        {
            if (!_gpuAvailable && DateTime.UtcNow < _nextRetryTime)
            {
                // PDH indisponível no cooldown — tenta fallback NVIDIA antes de desistir
                return GetNvidiaSmiUtilization();
            }
            EnsureInitialized();
            if (!_initialized) return -1;
            lock (_initLock)
            {
                if (_queryHandle == IntPtr.Zero) return -1;
            }
            try
            {
                TryReexpandIfNeeded();
                var now = DateTime.UtcNow;
                lock (_initLock)
                {
                    if ((now - _lastCollectTime).TotalMilliseconds > 700)
                    {
                        PdhCollectQueryData(_queryHandle);
                        _lastCollectTime = now;
                    }
                }
                // Windows Task Manager mostra o maior engine ativo (não soma). Leitura atômica sob lock evita handle inválido após re-expand.
                if (_engineCounters.Count > 1)
                {
                    double max = 0;
                    bool anyValid = false;
                    lock (_initLock)
                    {
                        foreach (var h in _engineCounters)
                        {
                            var rc = PdhGetFormattedCounterValue(h, PDH_FMT_DOUBLE, out _, out var v);
                            if (rc == 0 && (v.CStatus == PDH_CSTATUS_VALID_DATA || v.CStatus == PDH_CSTATUS_NEW_DATA))
                            {
                                if (v.DoubleValue > max) max = v.DoubleValue;
                                anyValid = true;
                            }
                        }
                    }
                    if (!anyValid) return _lastTotalValue >= 0 ? _lastTotalValue : -1;
                    _lastTotalValue = Math.Clamp(max, 0, 100);
                    return _lastTotalValue;
                }
                IntPtr single;
                lock (_initLock) single = _totalUtilCounter;
                if (single == IntPtr.Zero) return _lastTotalValue >= 0 ? _lastTotalValue : -1;
                var fmtResult = PdhGetFormattedCounterValue(single, PDH_FMT_DOUBLE, out _, out var value);
                if (fmtResult != 0) { _lastTotalValue = -1; return -1; }
                if (value.CStatus != PDH_CSTATUS_VALID_DATA && value.CStatus != PDH_CSTATUS_NEW_DATA) return _lastTotalValue >= 0 ? _lastTotalValue : -1;
                _lastTotalValue = Math.Clamp(value.DoubleValue, 0, 100);
                return _lastTotalValue;
            }
            catch { return -1; }
        }

        /// <summary>Marca indisponível mas agenda re-tentativa (backoff: 5s, 15s, 45s, máx 2min).</summary>
        private static void MarkUnavailableAndScheduleRetry()
        {
            _gpuAvailable = false;
            _failedAttempts++;
            int seconds = Math.Min(120, 5 * (int)Math.Pow(3, Math.Min(_failedAttempts - 1, 4)));
            _nextRetryTime = DateTime.UtcNow.AddSeconds(seconds);
        }

        /// <summary>
        /// Fallback NVIDIA: utilização via nvidia-smi (técnica do FreeToken).
        /// Throttled a 1x/2s — o processo custa ~100ms. Retorna -1 se não houver NVIDIA.
        /// </summary>
        private static DateTime _lastSmiQuery = DateTime.MinValue;
        private static double _lastSmiValue = -1;
        private static double GetNvidiaSmiUtilization()
        {
            try
            {
                if ((DateTime.UtcNow - _lastSmiQuery).TotalSeconds < 2) return _lastSmiValue;
                _lastSmiQuery = DateTime.UtcNow;
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=utilization.gpu --format=csv,noheader,nounits",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return _lastSmiValue;
                string outp = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(2000);
                if (double.TryParse(outp.Split('\n')[0].Trim(), out var pct))
                    _lastSmiValue = Math.Clamp(pct, 0, 100);
            }
            catch { }
            return _lastSmiValue;
        }

        public static bool IsAvailable() { EnsureInitialized(); return _gpuAvailable && _initialized; }

        public static void Shutdown()
        {
            lock (_initLock)
            {
                foreach (var h in _engineCounters) try { PdhRemoveCounter(h); } catch { }
                _engineCounters.Clear();
                _activePaths.Clear();
                if (_totalUtilCounter != IntPtr.Zero) { try { PdhRemoveCounter(_totalUtilCounter); } catch { } _totalUtilCounter = IntPtr.Zero; }
                if (_queryHandle != IntPtr.Zero) { try { PdhCloseQuery(_queryHandle); } catch { } _queryHandle = IntPtr.Zero; }
                _initialized = false;
                _initializing = false;
            }
        }
    }
}
