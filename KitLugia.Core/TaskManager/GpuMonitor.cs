using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace KitLugia.Core.TaskManager
{
    /// <summary>
    /// GPU utilization via PDH (Performance Data Helper).
    /// TECNICA TMOG (confirmada nos strings do binario + empiricamente):
    /// UM unico contador curinga "\GPU Engine(*)\Utilization Percentage" adicionado via
    /// PdhAddEnglishCounterW (resolve o nome localizado internamente) e lido COMPLETO com
    /// PdhGetFormattedCounterArrayW — todas as instancias (pid_NNNN_luid_..._engtype_...) em
    /// UMA chamada, sem 567 handles individuais e sem reexpand periodico (o curinga cobre
    /// instancias criadas depois automaticamente).
    /// O dict por PID inclui engines OCIOSOS (0,0%) — mesmo comportamento do Task Manager/
    /// TMOG: engine presente = "0.0%", sem engine (exited) = "Unavail"/"—".
    /// </summary>
    public static class GpuMonitor
    {
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQuery(string? szDataSource, uint dwUserData, out IntPtr phQuery);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounter(IntPtr hQuery, string szFullCounterPath, uint dwUserData, out IntPtr phCounter);
        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr hQuery);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArrayW(IntPtr phCounter, uint dwFormat, ref uint pdwBufferSize, out uint lpdwBufferCount, IntPtr ItemBuffer);
        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr hQuery);
        [DllImport("pdh.dll")]
        private static extern uint PdhRemoveCounter(IntPtr hCounter);

        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE { public uint CStatus; public double DoubleValue; }

        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const uint PDH_MORE_DATA = 0x800007D2;
        private const uint PDH_CSTATUS_VALID_DATA = 0x00000000;
        private const uint PDH_CSTATUS_NEW_DATA = 0x00000001;

        private static IntPtr _queryHandle = IntPtr.Zero;
        private static IntPtr _wildcardCounter = IntPtr.Zero; // \GPU Engine(*)\Utilization Percentage
        private static bool _initialized = false;
        private static bool _gpuAvailable = true;
        private static volatile bool _initializing = false;
        private static DateTime _lastCollectTime = DateTime.MinValue;
        private static double _lastTotalValue = -1;
        private static readonly object _initLock = new();
        // FIX: falha unica na inicializacao NAO desabilita para sempre — re-tenta apos cooldown.
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

                    // TECNICA TMOG: um unico contador curinga. O English path e resolvido
                    // internamente pelo PDH (funciona em SO localizado — testado pt-BR).
                    r = PdhAddEnglishCounter(_queryHandle, @"\GPU Engine(*)\Utilization Percentage", 0, out _wildcardCounter);
                    if (r == 0 && _wildcardCounter != IntPtr.Zero)
                    {
                        PdhCollectQueryData(_queryHandle);
                        doWarmup = true;
                        _initialized = true;
                    }
                    else
                    {
                        MarkUnavailableAndScheduleRetry();
                        try { PdhRemoveCounter(_wildcardCounter); } catch { }
                        _wildcardCounter = IntPtr.Zero;
                        if (_queryHandle != IntPtr.Zero) { try { PdhCloseQuery(_queryHandle); } catch { } _queryHandle = IntPtr.Zero; }
                    }
                }
                catch
                {
                    MarkUnavailableAndScheduleRetry();
                    try { PdhRemoveCounter(_wildcardCounter); } catch { }
                    _wildcardCounter = IntPtr.Zero;
                    if (_queryHandle != IntPtr.Zero) { try { PdhCloseQuery(_queryHandle); } catch { } _queryHandle = IntPtr.Zero; }
                }
                finally { _initializing = false; }
            }
            // Warmup: segunda coleta ~150ms depois para o PDH ter delta entre amostras
            if (doWarmup)
            {
                var captured = _queryHandle;
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try { await System.Threading.Tasks.Task.Delay(150); } catch { }
                    lock (_initLock)
                    {
                        try
                        {
                            if (captured != IntPtr.Zero && _queryHandle == captured)
                                PdhCollectQueryData(captured);
                        }
                        catch { }
                    }
                });
            }
        }

        /// <summary>
        /// Le o array completo do contador curinga (padrao 2 chamadas: probe de tamanho + leitura).
        /// Retorna dict PID -> soma das engines e o maior engine individual (total estilo Task Manager).
        /// A leitura e confinada ao buffer devolvido pela propria PDH (ponteiros de nome apontam
        /// para dentro dele); todos os acessos sao validados contra os limites do buffer.
        /// </summary>
        private static Dictionary<uint, double> ReadEngineArray()
        {
            var result = new Dictionary<uint, double>();
            IntPtr wc;
            IntPtr query;
            lock (_initLock) { wc = _wildcardCounter; query = _queryHandle; }
            if (wc == IntPtr.Zero || query == IntPtr.Zero) return result;

            var now = DateTime.UtcNow;
            lock (_initLock)
            {
                if ((now - _lastCollectTime).TotalMilliseconds > 700)
                {
                    PdhCollectQueryData(_queryHandle);
                    _lastCollectTime = now;
                }
            }

            try
            {
                uint size = 0;
                // 1a chamada: PDH_MORE_DATA devolve o tamanho necessario
                uint rc = PdhGetFormattedCounterArrayW(wc, PDH_FMT_DOUBLE, ref size, out _, IntPtr.Zero);
                if (size == 0 || (rc != 0 && rc != PDH_MORE_DATA)) return result;

                IntPtr buf = Marshal.AllocHGlobal((int)size);
                try
                {
                    rc = PdhGetFormattedCounterArrayW(wc, PDH_FMT_DOUBLE, ref size, out uint count, buf);
                    if (rc != 0) return result;

                    double maxInstance = 0;
                    // PDH_FMT_COUNTERVALUE_ITEM_W x64: LPWSTR szName @0, DWORD CStatus @8, pad, double @16, stride 24
                    const int itemStride = 24;
                    for (uint i = 0; i < count; i++)
                    {
                        IntPtr item = buf + (int)(i * itemStride);
                        if (item + itemStride > buf + size) break; // defesa: nunca passar do buffer
                        IntPtr namePtr = Marshal.ReadIntPtr(item);
                        if (namePtr < buf || namePtr >= buf + size) continue; // nome fora do buffer = lixo
                        uint cstatus = (uint)Marshal.ReadInt32(item, 8);
                        if (cstatus != PDH_CSTATUS_VALID_DATA && cstatus != PDH_CSTATUS_NEW_DATA) continue;
                        double val = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                        if (val < 0) val = 0;
                        if (val > maxInstance) maxInstance = val;

                        string name = Marshal.PtrToStringUni(namePtr) ?? "";
                        uint pid = ExtractPidFromEnginePath(name);
                        if (pid == 0) continue;
                        // SOMA por PID (multi-engine), clamp no final. Inclui 0,0 — engine presente.
                        double acc = result.TryGetValue(pid, out var a) ? a : 0;
                        result[pid] = acc + val;
                    }

                    if (result.Count > 0)
                    {
                        foreach (var k in result.Keys.ToList())
                        {
                            if (result[k] > 100) result[k] = 100;
                        }
                        _lastTotalValue = Math.Clamp(maxInstance, 0, 100);
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// GPU% por PID (tecnica TMOG): inclui TODOS os processos com engine GPU atribuido,
        /// mesmo ociosos (0,0) — a UI mostra "0%" para eles e "—"/"Unavail" para exited.
        /// Tambem atualiza o total interno (maior engine ativo, comportamento do Task Manager).
        /// </summary>
        public static Dictionary<uint, double> GetGpuUtilizationPerPid()
        {
            try
            {
                EnsureInitialized();
                if (!_initialized) return new Dictionary<uint, double>();
                return ReadEngineArray();
            }
            catch { return new Dictionary<uint, double>(); }
        }

        public static double GetTotalGpuUtilization()
        {
            if (!_gpuAvailable && DateTime.UtcNow < _nextRetryTime)
            {
                // PDH indisponivel no cooldown — tenta fallback NVIDIA antes de desistir
                return GetNvidiaSmiUtilization();
            }
            EnsureInitialized();
            if (!_initialized) return -1;
            try
            {
                // O array walk atualiza _lastTotalValue com o maior engine (estilo Task Manager)
                var dict = ReadEngineArray();
                if (dict.Count == 0) return _lastTotalValue >= 0 ? _lastTotalValue : -1;
                return _lastTotalValue;
            }
            catch { return _lastTotalValue; }
        }

        /// <summary>Extrai o PID do caminho "\GPU Engine(pid_1234_luid_...)".</summary>
        private static uint ExtractPidFromEnginePath(string path)
        {
            try
            {
                int i = path.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
                if (i < 0) return 0;
                i += 4;
                int j = i;
                while (j < path.Length && char.IsDigit(path[j])) j++;
                return j > i && uint.TryParse(path.Substring(i, j - i), out var pid) ? pid : 0u;
            }
            catch { return 0; }
        }

        /// <summary>Marca indisponivel mas agenda re-tentativa (backoff: 5s, 15s, 45s, max 2min).</summary>
        private static void MarkUnavailableAndScheduleRetry()
        {
            _gpuAvailable = false;
            _failedAttempts++;
            int seconds = Math.Min(120, 5 * (int)Math.Pow(3, Math.Min(_failedAttempts - 1, 4)));
            _nextRetryTime = DateTime.UtcNow.AddSeconds(seconds);
        }

        /// <summary>
        /// Fallback NVIDIA: utilizacao via nvidia-smi (tecnica do FreeToken).
        /// Throttled a 1x/2s — o processo custa ~100ms. Retorna -1 se nao houver NVIDIA.
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
                if (_wildcardCounter != IntPtr.Zero) { try { PdhRemoveCounter(_wildcardCounter); } catch { } _wildcardCounter = IntPtr.Zero; }
                if (_queryHandle != IntPtr.Zero) { try { PdhCloseQuery(_queryHandle); } catch { } _queryHandle = IntPtr.Zero; }
                _initialized = false;
                _initializing = false;
            }
        }
    }
}
