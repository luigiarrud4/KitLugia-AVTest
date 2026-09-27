using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;

namespace KitLugia.Core.TaskManager
{
    /// <summary>
    /// Coleta de métricas estilo TMOG (Dave Plummer Task Manager), extraída por engenharia
    /// reversa com IDA Pro (docs/TMOG_TASKMANAGER_ANALYSIS.md):
    ///
    /// 1. Caminho primário: NtQuerySystemInformation(SystemProcessInformation) resolvida
    ///    DINAMICAMENTE via GetModuleHandle/GetProcAddress em ntdll (ponto Gemini #3 —
    ///    mesma técnica do TMOG: API fora dos imports estáticos, robustez + menos falso
    ///    positivo de AV).
    /// 2. Fallback: CreateToolhelp32Snapshot + OpenProcess(0x1010 → 0x1000) — direitos
    ///    MÍNIMOS (PROCESS_QUERY_LIMITED_INFORMATION), nunca PROCESS_QUERY_INFORMATION
    ///    (0x0400) — é isso que permite ler tempos/memória de TODOS os processos sem admin.
    /// 3. Delta de CPU com memória de estado por PID (ponto Gemini #1): GetProcessTimes
    ///    retorna tempo ACUMULADO; o tracker retém o tick anterior por PID, divide pelo
    ///    tempo decorrido e descarta PIDs mortos.
    /// 4. Graceful degradation: cada métrica é opcional; processo com acesso parcial
    ///    permanece na lista (padrão "overlay" do TMOG).
    /// </summary>
    public static class NativeMetricsHelper
    {
        // ──────────────────────────── Row pública ────────────────────────────

        public sealed class ProcMetrics
        {
            public int Pid;
            public int ParentPid;
            public string Name = "";
            public ulong ThreadCount;
            public ulong HandleCount;
            public ulong WorkingSetBytes;      // WS visível sem admin (SystemProcessInformation)
            public ulong PrivateBytes;         // PrivatePageCount (commit privado)
            public long KernelTime100ns;       // acumulado
            public long UserTime100ns;         // acumulado
            public long CreationTimeFiletime;  // 0 = indisponível (acesso limitado)
            public ulong IoReadBytesTotal;     // IO_COUNTERS.ReadTransferCount (NtQSI; 0 no fallback)
            public ulong IoWriteBytesTotal;    // IO_COUNTERS.WriteTransferCount
            public ulong IoReadOpsTotal;
            public ulong IoWriteOpsTotal;
            public ulong PeakWorkingSetBytes;  // PeakWorkingSetSize (paridade TMOG: coluna Peak memory)
            public ulong PageFaults;           // PageFaultCount (paridade TMOG: coluna Page faults)
            public ulong HardFaults;           // HardFaultsCount (falha de página que foi ao DISCO —LatencyMon)
            public bool IsFullAccess;          // true = veio de OpenProcess com VM_READ (fallback)
            public bool HasMetrics;            // pelo menos uma métrica legível
        }

        /// <summary>Tracker de delta de CPU — memória de estado por PID (ponto Gemini #1).</summary>
        public sealed class CpuDeltaTracker
        {
            private sealed class State { public long Total100ns; public long Stamp100ns; }
            private readonly Dictionary<int, State> _prev = new();
            private readonly object _lock = new();
            private int _coreCount = Environment.ProcessorCount;

            public void SetCoreCount(int cores) { if (cores > 0) _coreCount = cores; }

            /// <summary>Percent 0-100 por núcleo multiplicado; 0 = primeira amostra.</summary>
            public double Compute(int pid, long kernel100ns, long user100ns, long now100ns)
            {
                lock (_lock)
                {
                    long total = (long)((ulong)kernel100ns + (ulong)user100ns); // wrap-safe
                    double pct = 0;
                    if (_prev.TryGetValue(pid, out var s) && now100ns > s.Stamp100ns)
                    {
                        double dTicks = total - s.Total100ns;
                        double dMs = (now100ns - s.Stamp100ns) / 10000.0;
                        if (dMs > 50 && dTicks > 0)
                            pct = dTicks / 10000.0 / dMs / _coreCount * 100.0;
                    }
                    _prev[pid] = new State { Total100ns = total, Stamp100ns = now100ns };
                    return pct > 100 ? 100 : pct;
                }
            }

            public void RemoveDead(int[] alivePids)
            {
                lock (_lock)
                {
                    var alive = new HashSet<int>(alivePids);
                    var dead = new List<int>();
                    foreach (var k in _prev.Keys) if (!alive.Contains(k)) dead.Add(k);
                    foreach (var k in dead) _prev.Remove(k);
                }
            }

            public void Clear() { lock (_lock) { _prev.Clear(); } }
        }

        /// <summary>
        /// Snapshot de processos em uma passada. Caminho primário é o
        /// NtQuerySystemInformation(SystemProcessInformation) resolvido dinamicamente em
        /// ntdll (ponto Gemini #3). Se indisponível, cai para Toolhelp com OpenProcess de
        /// direitos mínimos. Zero handles vazam (fechados em finally).
        /// </summary>
        public static List<ProcMetrics> EnumerateProcesses()
        {
            if (TryResolveNtQuerySystemInformation())
            {
                try
                {
                    var list = EnumerateViaSystemInformation();
                    if (list != null) return list;
                }
                catch { /* cai para Toolhelp */ }
            }
            return EnumerateViaToolhelp();
        }

        // ──────────────── Caminho 1: NtQuerySystemInformation (dinâmico) ────────────────

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int NtQuerySystemInformationDelegate(
            int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);

        private static NtQuerySystemInformationDelegate? _ntqsi;
        private static bool _ntqsiResolved;

        private static bool TryResolveNtQuerySystemInformation()
        {
            if (_ntqsiResolved) return _ntqsi != null;
            _ntqsiResolved = true;
            try
            {
                // GetModuleHandle (não LoadLibrary): ntdll já está mapeada em qualquer processo.
                IntPtr ntdll = GetModuleHandleW("ntdll.dll");
                if (ntdll == IntPtr.Zero) return false;
                IntPtr proc = GetProcAddress(ntdll, "NtQuerySystemInformation");
                if (proc == IntPtr.Zero) return false;
                _ntqsi = Marshal.GetDelegateForFunctionPointer<NtQuerySystemInformationDelegate>(proc);
            }
            catch { _ntqsi = null; }
            return _ntqsi != null;
        }

        /// <summary>Diagnóstico da última tentativa (útil para suporte/log).</summary>
        public static string LastDiag = "";

        // ═════════════ MÉTRICAS GLOBAIS (TMOG: GetSystemTimes / K32GetPerformanceInfo / PDH / POWRPROF) ═════════════

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool K32GetPerformanceInfo(out PERFORMANCE_INFORMATION pPerformanceInformation, uint cb);

        [StructLayout(LayoutKind.Sequential)]
        private struct PERFORMANCE_INFORMATION
        {
            // campos SIZE_T (8 bytes no x64 — sizeof=104; uint quebrava com ERROR_BAD_LENGTH=24)
            public uint cb;
            public UIntPtr CommitTotal;
            public UIntPtr CommitLimit;
            public UIntPtr CommitPeak;
            public UIntPtr PhysicalTotal;
            public UIntPtr PhysicalAvailable;
            public UIntPtr SystemCache;
            public UIntPtr KernelTotal;
            public UIntPtr KernelPaged;
            public UIntPtr KernelNonPaged;
            // PageSize fica ENTRE KernelNonPaged e HandleCount na struct real (winnt.h).
            // Omiti-lo deslocava HandleCount/ProcessCount/ThreadCount em 8 bytes —
            // "handles" lia o tamanho da página (4096) e "processos" lia lixo.
            public UIntPtr PageSize;
            // HandleCount/ProcessCount/ThreadCount são DWORD na struct real (winnt.h),
            // NÃO SIZE_T — lê-los como UIntPtr desalinhava tudo e devolvia lixo
            // ("handles" virava 0x190000000FC). Com DWORD o sizeof fecha em 104.
            public uint HandleCount;
            public uint ProcessCount;
            public uint ThreadCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern int CallNtPowerInformation(int informationLevel, IntPtr inputBuffer, uint inputBufferLength, IntPtr outputBuffer, uint outputBufferLength);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int PdhOpenQueryW(string? dataSource, uint userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int PdhAddEnglishCounterW(IntPtr query, string counterPath, uint userData, out IntPtr counter);

        // Wildcards expandem para caminhos LOCALIZADOS ("Medidor de Energia") — AddEnglish
        // rejeita; estes exigem PdhAddCounterW com o caminho expandido do próprio locale.
        [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int PdhAddCounterW(IntPtr query, string counterPath, uint userData, out IntPtr counter);

        [DllImport("pdh.dll", SetLastError = true)]
        private static extern int PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", SetLastError = true)]
        private static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr counterType, out PDH_FMT_COUNTERVALUE value);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int PdhExpandWildCardPathW(string? dataSource, string wildCardPath, System.Text.StringBuilder? expandedPathList, ref uint pcchPathListLength, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE { public uint CStatus; public double DoubleValue; }

        private const uint PDH_FMT_DOUBLE = 0x00000200;

        // CPU total: GetSystemTimes (TMOG) com tracker — 1 syscall, sem PDH, sem nomes
        private static long _sysPrevIdle, _sysPrevKernel, _sysPrevUser;
        private static DateTime _sysPrevStamp = DateTime.MinValue;
        private static readonly object _sysLock = new();

        // CPU total do sistema 0-100 via GetSystemTimes (imune a locale/instances).
        // Obs: _sysPrev* é EXCLUSIVO deste método — o kernel% tem estado próprio
        // (compartilhar fazia o segundo a ser chamado no tick receber dMs < 200 e
        // devolver valor obsoleto para sempre).
        public static double GetSystemCpuPercent()
        {
            lock (_sysLock)
            {
                try
                {
                    if (!GetSystemTimes(out long idle, out long kernel, out long user)) return -1;
                    var now = DateTime.UtcNow;
                    if (_sysPrevStamp == DateTime.MinValue) { _sysPrevIdle = idle; _sysPrevKernel = kernel; _sysPrevUser = user; _sysPrevStamp = now; return -1; }
                    double dMs = (now - _sysPrevStamp).TotalMilliseconds;
                    if (dMs < 200) { return _lastSysCpu; }
                    double dIdle = idle - _sysPrevIdle;
                    double dKernel = kernel - _sysPrevKernel; // kernel inclui idle no Windows
                    double dUser = user - _sysPrevUser;
                    _sysPrevIdle = idle; _sysPrevKernel = kernel; _sysPrevUser = user; _sysPrevStamp = now;
                    double busy = dKernel + dUser;
                    _lastSysCpu = busy <= 0 ? 0 : Math.Clamp((1 - dIdle / busy) * 100.0, 0, 100);
                    return _lastSysCpu;
                }
                catch { return -1; }
            }
        }
        private static double _lastSysCpu = -1;

        // % Processor Utility (a métrica "real" do TM, normalizada por frequência) — PDH em inglês, query persistente (TMOG)
        private static IntPtr _utilityQuery, _utilityCounter;
        private static bool _utilityTried;

        /// <summary>% Processor Utility via PDH inglês; fallback = GetSystemCpuPercent.</summary>
        public static double GetCpuUtilityPercent()
        {
            try
            {
                if (!_utilityTried)
                {
                    _utilityTried = true;
                    if (PdhOpenQueryW(null, 0, out _utilityQuery) == 0)
                        PdhAddEnglishCounterW(_utilityQuery, @"\Processor Information(_Total)\% Processor Utility", 0, out _utilityCounter);
                    if (_utilityCounter != IntPtr.Zero) PdhCollectQueryData(_utilityQuery); // primeira amostra = primer
                }
                if (_utilityCounter == IntPtr.Zero) return GetSystemCpuPercent();
                if (PdhCollectQueryData(_utilityQuery) != 0) return GetSystemCpuPercent();
                if (PdhGetFormattedCounterValue(_utilityCounter, PDH_FMT_DOUBLE, IntPtr.Zero, out var v) != 0) return GetSystemCpuPercent();
                if (v.DoubleValue < 0 || v.DoubleValue > 10000) return GetSystemCpuPercent();
                return v.DoubleValue;
            }
            catch { return GetSystemCpuPercent(); }
        }

        /// <summary>Uso de memória física 0-100 via K32GetPerformanceInfo (1 chamada, TMOG); fallback GlobalMemoryStatusEx.</summary>
        public static double GetMemoryUsagePercent()
        {
            try
            {
                if (K32GetPerformanceInfo(out var pi, (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>()) && pi.cb > 0)
                {
                    ulong total = pi.PhysicalTotal.ToUInt64();
                    if (total > 0)
                        return (total - pi.PhysicalAvailable.ToUInt64()) * 100.0 / total;
                }
                // Fallback: dwMemoryLoad É a porcentagem (0-100), sem matemática de páginas
                var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref ms)) return ms.dwMemoryLoad;
                return -1;
            }
            catch { return -1; }
        }

        /// <summary>RAM física disponível em MB (o que sobra para apps); -1 = indisponível. Calibra a leitura de paginação: página dura com RAM de sobra é tráfego de dados frios, não pressão de memória.</summary>
        public static double GetAvailableRamMb()
        {
            try
            {
                var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref ms)) return ms.ullAvailPhys / 1048576.0;
                return -1;
            }
            catch { return -1; }
        }

        /// <summary>Frequência média atual da CPU em MHz via CallNtPowerInformation (TMOG); -1 = indisponível.</summary>
        public static double GetCpuFrequencyMhz()
        {
            try
            {
                int cores = Environment.ProcessorCount;
                IntPtr buf = Marshal.AllocHGlobal(24 * cores);
                try
                {
                    // ProcessorInformation = 11; PROCESSOR_POWER_INFORMATION = 24 bytes/núcleo
                    if (CallNtPowerInformation(11, IntPtr.Zero, 0, buf, (uint)(24 * cores)) != 0) return -1;
                    long sum = 0; int n = 0;
                    for (int i = 0; i < cores; i++)
                    {
                        // +0 Number, +4 MaxMhz, +8 CurrentMhz
                        uint cur = (uint)Marshal.ReadInt32(buf, i * 24 + 8);
                        if (cur > 0) { sum += cur; n++; }
                    }
                    return n > 0 ? sum / (double)n : -1;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { return -1; }
        }

        private static IntPtr _thermalQuery, _thermalCounter;
        private static bool _thermalTried;

        // CPU package power (W) — paridade TMOG "CPU Power": o provider RAPL expõe
        // ENERGIA CUMULATIVA (nanojoules desde boot; PKG+PP0 == _Total confirmado).
        // Watts = delta(energia)/delta(tempo) — mesmo esquema do delta de CPU.
        // PdhAddEnglishCounterW resolve o nome EM INGLÊS no locale que for ("Medidor de
        // Energia" no pt-BR); wildcard NÃO funciona com AddEnglish nem expansão inglesa.
        private static IntPtr _powerQuery, _powerCounter;
        private static bool _powerTried;
        private static bool _powerIsEnergyCounter;
        private static double _prevEnergyNj;
        private static long _prevStamp100ns;
        private static bool _hasPrevEnergy;
        private static double _lastPowerW = -1;

        /// <summary>Potência do pacote CPU em watts (RAPL via PDH); null = sensor indisponível/1ª amostra.</summary>
        public static double? GetCpuPowerWatts()
        {
            try
            {
                if (!_powerTried)
                {
                    _powerTried = true;
                    if (PdhOpenQueryW(null, 0, out _powerQuery) == 0)
                    {
                        // O nome do objeto/contador é LOCALIZADO no banco do PDH — ExpandWildCard
                        // só casa com o nome do próprio locale. Tentamos candidatos (en + pt-BR);
                        // preferir Energy (cumulativa, o RAPL expõe), fallback Power (já em W).
                        string[] candidates =
                        {
                            @"\Medidor de Energia(_Total)\Energia",   // pt-BR, _Total, energia
                            @"\Power Meter(_Total)\Energy",           // en, _Total, energia
                            @"\Medidor de Energia(*)\Energia",        // pt-BR, 1ª instância
                            @"\Power Meter(*)\Energy",                // en, 1ª instância
                            @"\Medidor de Energia(_Total)\Potência",  // pt-BR, potência direta
                            @"\Power Meter(_Total)\Power",            // en, potência direta
                        };
                        foreach (var cand in candidates)
                        {
                            string? path = ExpandFirst(cand);
                            if (path == null) continue;
                            if (PdhAddCounterW(_powerQuery, path, 0, out _powerCounter) == 0 && _powerCounter != IntPtr.Zero)
                            {
                                _powerIsEnergyCounter = !path.Contains("Potência") && !path.Contains("Power");
                                break;
                            }
                        }
                    }
                    if (_powerCounter != IntPtr.Zero) PdhCollectQueryData(_powerQuery); // primer
                }
                if (_powerCounter == IntPtr.Zero) return null;
                if (PdhCollectQueryData(_powerQuery) != 0) return null;
                if (PdhGetFormattedCounterValue(_powerCounter, PDH_FMT_DOUBLE, IntPtr.Zero, out var v) != 0) return null;
                if (v.DoubleValue < 0) return null;
                long now = DateTime.UtcNow.ToFileTime();

                if (!_powerIsEnergyCounter)
                {
                    // contador direto de potência
                    if (v.DoubleValue > 5000) return _lastPowerW >= 0 ? _lastPowerW : (double?)null;
                    _lastPowerW = v.DoubleValue;
                    return v.DoubleValue;
                }

                // contador de ENERGIA cumulativa (nJ) → watts por delta
                if (!_hasPrevEnergy)
                {
                    _prevEnergyNj = v.DoubleValue; _prevStamp100ns = now; _hasPrevEnergy = true;
                    return _lastPowerW >= 0 ? _lastPowerW : (double?)null;
                }
                double dMs = (now - _prevStamp100ns) / 1e4;
                if (dMs < 200) return _lastPowerW >= 0 ? _lastPowerW : (double?)null;
                double dNj = v.DoubleValue - _prevEnergyNj;
                _prevEnergyNj = v.DoubleValue; _prevStamp100ns = now;
                if (dNj < 0) return _lastPowerW >= 0 ? _lastPowerW : (double?)null; // reset do contador
                double w = dNj / 1e9 / (dMs / 1000.0);
                if (w < 0 || w > 5000) return _lastPowerW >= 0 ? _lastPowerW : (double?)null;
                _lastPowerW = w;
                return w;
            }
            catch { return null; }
        }

        /// <summary>Temperatura CPU via PDH Thermal Zone (Kelvin→°C); null = sensor indisponível.</summary>
        public static double? GetCpuTemperatureC()
        {
            try
            {
                if (!_thermalTried)
                {
                    _thermalTried = true;
                    if (PdhOpenQueryW(null, 0, out _thermalQuery) == 0)
                    {
                        // O nome do objeto/contador é LOCALIZADO no banco do PDH — o caminho
                        // em inglês puro falha no pt-BR (typeperf: "nenhum contador válido").
                        // 1º candidato = mapeado via Perflib (vale p/ qualquer idioma).
                        string obj = LocalizePerfName("Thermal Zone Information");
                        string ctr = LocalizePerfName("Temperature");
                        string[] wildcards =
                        {
                            $@"\{obj}(*)\{ctr}",                            // locale atual (Perflib)
                            @"\Thermal Zone Information(*)\Temperature",    // inglês direto
                            @"\Informações de Zona Termal(*)\Temperatura",  // pt-BR conhecido
                        };
                        foreach (var wildcard in wildcards)
                        {
                            uint len = 0;
                            PdhExpandWildCardPathW(null, wildcard, null, ref len, 0);
                            if (len <= 1) continue;
                            var sb = new System.Text.StringBuilder((int)len);
                            if (PdhExpandWildCardPathW(null, wildcard, sb, ref len, 0) != 0) continue;
                            var first = sb.ToString().Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                            if (string.IsNullOrEmpty(first)) continue;
                            if (PdhAddCounterW(_thermalQuery, first, 0, out _thermalCounter) == 0 && _thermalCounter != IntPtr.Zero)
                                break; // caminho expandido já é localizado
                            _thermalCounter = IntPtr.Zero;
                        }
                    }
                    if (_thermalCounter != IntPtr.Zero) PdhCollectQueryData(_thermalQuery);
                }
                if (_thermalCounter == IntPtr.Zero) return null;
                if (PdhCollectQueryData(_thermalQuery) != 0) return null;
                if (PdhGetFormattedCounterValue(_thermalCounter, PDH_FMT_DOUBLE, IntPtr.Zero, out var v) != 0) return null;
                if (v.DoubleValue < 0 || v.DoubleValue > 1000) return null;
                double c = v.DoubleValue > 200 ? v.DoubleValue - 273.15 : v.DoubleValue; // Kelvin → °C
                return c;
            }
            catch { return null; }
        }

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int WTSEnumerateProcessesW(IntPtr hServer, uint Reserved, uint Version, out IntPtr ppProcessInfo, out int pCount);
        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr pMemory);

        [StructLayout(LayoutKind.Sequential)]
        private struct WTS_PROCESS_INFOW
        {
            public uint SessionId;
            public uint ProcessId;
            public IntPtr pProcessName;   // LPWSTR
            public IntPtr pUserSid;       // PSID (pode ser 0)
        }

        private static readonly Dictionary<int, string> _userNameCache = new();
        private static DateTime _userNameCacheStamp = DateTime.MinValue;

        /// <summary>
        /// Usuário proprietário de cada processo via WTSEnumerateProcesses (TMOG: coluna
        /// "User name"). Cache de 15s (WTS enumera rápido, mas não custa re-ler a cada refresh).
        /// </summary>
        public static Dictionary<int, string> GetUserNames()
        {
            var result = new Dictionary<int, string>();
            try
            {
                if ((DateTime.UtcNow - _userNameCacheStamp).TotalSeconds < 15)
                {
                    lock (_userNameCache) return new Dictionary<int, string>(_userNameCache);
                }
                // WTS_CURRENT_SERVER_HANDLE = 0; Version TEM que ser 1 (0 = erro 87)
                if (WTSEnumerateProcessesW(IntPtr.Zero, 0, 1, out IntPtr ptr, out int count) == 0 || ptr == IntPtr.Zero)
                    return result;
                try
                {
                    int size = Marshal.SizeOf<WTS_PROCESS_INFOW>();
                    var local = new Dictionary<int, string>();
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr p = new IntPtr(ptr.ToInt64() + i * (long)size);
                        var wi = Marshal.PtrToStructure<WTS_PROCESS_INFOW>(p);
                        string user = "";
                        if (wi.pUserSid != IntPtr.Zero)
                        {
                            try
                            {
                                var sid = new System.Security.Principal.SecurityIdentifier(wi.pUserSid);
                                try { user = sid.Translate(typeof(System.Security.Principal.NTAccount)).Value ?? ""; }
                                catch { user = sid.Value ?? ""; }
                            }
                            catch { }
                        }
                        local[(int)wi.ProcessId] = user;
                    }
                    lock (_userNameCache)
                    {
                        _userNameCache.Clear();
                        foreach (var kv in local) _userNameCache[kv.Key] = kv.Value;
                        _userNameCacheStamp = DateTime.UtcNow;
                    }
                    result = local;
                }
                finally { WTSFreeMemory(ptr); }
            }
            catch { }
            return result;
        }

        /// <summary>Nome curto do usuário ("DOMINIO\\user" -> "user"); SYSTEM fica SYSTEM.</summary>
        public static string ShortenUserName(string account)
        {
            try
            {
                if (string.IsNullOrEmpty(account)) return "";
                int bs = account.LastIndexOf('\\');
                string name = bs >= 0 ? account[(bs + 1)..] : account;
                if (name.EndsWith(".0", StringComparison.Ordinal) && account.StartsWith("NT AUTHORITY", StringComparison.OrdinalIgnoreCase)) return "SYSTEM";
                return name;
            }
            catch { return account ?? ""; }
        }

        /// <summary>Lista de usuários REAIS (conta interativa — para a aba Usuários). Exclui
        /// SYSTEM/serviços (en + pt-BR), DWM-*/UMFD-* e SIDs de máquina/GUID.</summary>
        public static List<string> GetActiveUserNames()
        {
            var names = new List<string>();
            try
            {
                var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "SYSTEM", "SISTEMA", "LOCAL SERVICE", "SERVIçO LOCAL", "SERVIÇO LOCAL", "NETWORK SERVICE", "SERVIçO DE REDE", "SERVIÇO DE REDE",
                  "ANONYMOUS LOGON", "LOGON ANôNIMO", "LOGON ANÔNIMO", "DWM-0", "DWM-1", "DWM-2", "UMFD-0", "UMFD-1", "UMFD-2", "WINDOW MANAGER", "GERENCIADOR DE JANELAS" };
                foreach (var kv in GetUserNames())
                {
                    var acct = kv.Value;
                    if (string.IsNullOrEmpty(acct)) continue;
                    // prefixos de conta de serviço/máquina
                    if (acct.StartsWith("NT AUTHORITY\\", StringComparison.OrdinalIgnoreCase) ||
                        acct.StartsWith("NT SERVICE\\", StringComparison.OrdinalIgnoreCase) ||
                        acct.StartsWith("SERVIçO\\", StringComparison.OrdinalIgnoreCase) ||
                        acct.StartsWith("WINDOW MANAGER\\", StringComparison.OrdinalIgnoreCase) ||
                        acct.StartsWith("GERENCIADOR DE JANELAS\\", StringComparison.OrdinalIgnoreCase)) continue;
                    var s = ShortenUserName(acct);
                    if (string.IsNullOrEmpty(s)) continue;
                    if (excluded.Contains(s)) continue;
                    if (s.StartsWith("DWM-", StringComparison.OrdinalIgnoreCase) || s.StartsWith("UMFD-", StringComparison.OrdinalIgnoreCase)) continue;
                    // SID puro (S-1-5-...) ou GUID do UserProfileService → não é nome exibível
                    if (s.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) || (s.Length == 36 && s.Count(c => c == '-') == 4)) continue;
                    if (!names.Contains(s)) names.Add(s);
                }
            }
            catch { }
            return names;
        }

        private static readonly Dictionary<string, string> _perfNameCache = new();

        /// <summary>Mapeia um nome de objeto/contador PDH em inglês para o nome localizado
        /// via ÍNDICE do banco Perflib (009 = inglês; a chave do locale atual é o LCID em
        /// hex, ex.: 0416 = pt-BR). Sem isso o ExpandWildCard só casa no próprio locale e
        /// o sensor "some" — foi exatamente o caso do Thermal Zone no pt-BR (o TMOG lê
        /// 28 °C na mesma máquina onde o Kit mostrava "—"). Cache por nome.</summary>
        private static string LocalizePerfName(string english)
        {
            try
            {
                lock (_perfNameCache)
                {
                    if (_perfNameCache.TryGetValue(english, out var hit)) return hit;
                }
                string result = english;
                int lcid = System.Globalization.CultureInfo.CurrentUICulture.LCID;
                using var kEn = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib\009");
                using var kLoc = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib\" + lcid.ToString("X4"));
                var en = kEn?.GetValue("Counter") as string[];
                var loc = kLoc?.GetValue("Counter") as string[];
                if (en != null && loc != null)
                {
                    var locById = new Dictionary<string, string>(StringComparer.Ordinal);
                    for (int j = 1; j < loc.Length; j += 2)
                    {
                        if (!string.IsNullOrEmpty(loc[j]) && !locById.ContainsKey(loc[j - 1]))
                            locById[loc[j - 1]] = loc[j];
                    }
                    // Um nome em inglês pode ter VÁRIOS ids (ex.: "Temperature" = 5504 e
                    // 3702, de providers diferentes); usa o 1º id que existir no locale.
                    for (int i = 1; i < en.Length; i += 2) // [id, nome, id, nome...]
                    {
                        if (!string.Equals(en[i], english, StringComparison.OrdinalIgnoreCase)) continue;
                        if (locById.TryGetValue(en[i - 1], out var mapped) && !string.IsNullOrEmpty(mapped))
                        { result = mapped; break; }
                    }
                }
                lock (_perfNameCache) _perfNameCache[english] = result;
                return result;
            }
            catch { return english; }
        }

        /// <summary>Expande um caminho PDH wildcard e devolve o 1º caminho válido (ou null).</summary>
        private static string? ExpandFirst(string wildcard)
        {
            try
            {
                uint len = 0;
                PdhExpandWildCardPathW(null, wildcard, null, ref len, 0);
                if (len <= 1) return null;
                var sb = new System.Text.StringBuilder((int)len);
                if (PdhExpandWildCardPathW(null, wildcard, sb, ref len, 0) != 0) return null;
                return sb.ToString().Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            }
            catch { return null; }
        }

        private static List<ProcMetrics>? EnumerateViaSystemInformation()
        {
            const int SystemProcessInformation = 5;
            int len = 0x10000;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr buf = Marshal.AllocHGlobal(len);
                byte[]? copy = null;
                long baseAddr = buf.ToInt64();
                try
                {
                    int status = _ntqsi!(SystemProcessInformation, buf, len, out int needed);
                    LastDiag = $"attempt={attempt} len=0x{len:X} status=0x{status:X8} needed=0x{needed:X}";
                    const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
                    if ((uint)status == STATUS_INFO_LENGTH_MISMATCH)
                    {
                        len = Math.Max(needed, len * 2);
                        continue; // retry com buffer maior (lista de processos mudou)
                    }
                    if (status < 0) return null; // NTSTATUS de erro -> fallback Toolhelp

                    // needed = bytes realmente escritos pelo kernel. Copiamos para memoria
                    // GERENCIADA imediatamente: o parse roda em byte[] (AV-proof — leitura
                    // fora dos limites lanca IndexOutOfRangeException capturavel) e o buffer
                    // nativo vive o minimo necessario. Marshal.Read* em ponteiro cru podia
                    // dar AccessViolation FATAL (Internal CLR error 0x80131506 — nem handler
                    // global pega; era essa a classe de crash que "quebrava o kit inteiro").
                    int copyLen = needed > 0 ? Math.Min(needed, len) : len;
                    copy = new byte[copyLen];
                    Marshal.Copy(buf, copy, 0, copyLen);
                }
                finally { Marshal.FreeHGlobal(buf); }

                if (copy != null)
                {
                    var parsed = ParseSystemProcessInformation(copy, copy.Length, baseAddr);
                    LastDiag += $" parsed={parsed.Count}";
                    return parsed;
                }
            }
            return null;
        }

        private static List<ProcMetrics> ParseSystemProcessInformation(byte[] data, int dataLen, long baseAddr)
        {
            // Layout x64 VALIDADO EMPIRICAMENTE no Windows 26100 (%TEMP%\opencode\tmog\debug_entry0.ps1):
            // e o layout PUBLICADO no winternl.h — sem divergencias SRCORE:
            //   0x00 NextEntryOffset | 0x04 NumberOfThreads | 0x08 WorkingSetPrivateSize
            //   0x10 HardFaultsCount(4)+HighWatermark(4) | 0x18 CycleTime | 0x20 CreateTime
            //   0x28 UserTime | 0x30 KernelTime | 0x38 ImageName(UNICODE_STRING)
            //   0x48 BasePriority | 0x50 UniqueProcessId | 0x58 InheritedFrom | 0x60 HandleCount(4)+SessionId(4)
            //   0x68 UniqueProcessKey | 0x70 PeakVirtualSize | 0x78 VirtualSize | 0x80 PageFaultCount(4)
            //   0x88 PeakWorkingSetSize | 0x90 WorkingSetSize | ... | 0xC8 PrivatePageCount
            //   0xD0 IO_COUNTERS (48) | threads @ header (varia 624-656 por entrada — NAO e fixo;
            //   irrelevante: todos os campos lidos ficam antes do array de threads)
            //
            // Parse 100% em array gerenciado: toda leitura passa pelos helpers Read* com
            // bounds-check (lancam IndexOutOfRangeException CAPTURAVEL — nunca AV fatal).
            var list = new List<ProcMetrics>(512);
            const int Off_Next = 0x00, Off_Threads = 0x04;
            const int Off_Create = 0x20, Off_User = 0x28, Off_Kernel = 0x30;
            const int Off_ImageName = 0x38, Off_Pid = 0x50, Off_Ppid = 0x58;
            const int Off_Handles = 0x60;           // ULONG (SessionId em 0x64 — nao misturar)
            const int Off_PageFaults = 0x80;        // ULONG PageFaultCount
            const int Off_PeakWorkingSet = 0x88;    // SIZE_T PeakWorkingSetSize
            const int Off_WorkingSet = 0x90;        // WorkingSetSize
            const int Off_Private = 0xC8;           // PrivatePageCount (commit privado)
            // IO_COUNTERS (48B em 0xD0) — ordem interna (winternl.h): Read/Write/Other,
            // primeiro os COUNTS depois os BYTES. Ler fora de ordem trocava E/S lida/escrita
            // com OtherOps/ReadBytes (campos "—" ou valores absurdos no painel de detalhes).
            // (0xE0 = OtherOperationCount: não exposto no ProcMetrics — a lista de processos
            //  usa só leitura/escrita; o painel de detalhes lê os 3 via GetProcessIoCounters)
            const int Off_IoReadOps = 0xD0, Off_IoWriteOps = 0xD8;
            const int Off_IoReadBytes = 0xE8, Off_IoWriteBytes = 0xF0;

            int pos = 0;
            int iter = 0;
            while (dataLen >= Off_WorkingSet + 8 && iter < 4096)
            {
                try
                {
                    uint next = ReadU32(data, pos + Off_Next, dataLen);
                    uint threads = ReadU32(data, pos + Off_Threads, dataLen);
                    long create = ReadI64(data, pos + Off_Create, dataLen);
                    long user = ReadI64(data, pos + Off_User, dataLen);
                    long kernel = ReadI64(data, pos + Off_Kernel, dataLen);
                    // 0x10: ULONG HardFaultsCount + ULONG HighWatermark (o HighWatermark
                    // misturado como bits altos do faults multiplicava o valor por ~4300)
                    ulong hardFaults = ReadU32(data, pos + 0x10, dataLen);

                    // UNICODE_STRING: Length e USHORT (2 bytes). Lido como Int64 misturava
                    // MaximumLength nos bits altos (12 | 16<<16) e o guard rejeitava TODOS os
                    // nomes. Buffer fica em +8 (0x40) e aponta para DENTRO do snapshot.
                    int lenUs = ReadU16(data, pos + Off_ImageName, dataLen);      // Length (bytes)
                    long bufUs = ReadI64(data, pos + Off_ImageName + 8, dataLen); // Buffer
                    string name = "";
                    int nameIdx = (int)(bufUs - baseAddr); // indice do nome dentro do byte[]
                    if (lenUs > 0 && lenUs <= 520 && (lenUs & 1) == 0 &&
                        nameIdx >= 0 && nameIdx + lenUs <= dataLen)
                    {
                        try { name = System.Text.Encoding.Unicode.GetString(data, nameIdx, lenUs); }
                        catch { name = ""; }
                    }

                    var m = new ProcMetrics
                    {
                        Pid = (int)ReadI64(data, pos + Off_Pid, dataLen),
                        ParentPid = (int)ReadI64(data, pos + Off_Ppid, dataLen),
                        Name = name,
                        ThreadCount = threads,
                        KernelTime100ns = kernel,
                        UserTime100ns = user,
                        CreationTimeFiletime = create,
                        HandleCount = ReadU32(data, pos + Off_Handles, dataLen), // ULONG puro
                        PageFaults = ReadU32(data, pos + Off_PageFaults, dataLen),
                        PeakWorkingSetBytes = (ulong)ReadI64(data, pos + Off_PeakWorkingSet, dataLen),
                        WorkingSetBytes = (ulong)ReadI64(data, pos + Off_WorkingSet, dataLen),
                        PrivateBytes = (ulong)ReadI64(data, pos + Off_Private, dataLen),
                        IoReadBytesTotal = (ulong)ReadI64(data, pos + Off_IoReadBytes, dataLen),   // ReadTransferCount
                        IoWriteBytesTotal = (ulong)ReadI64(data, pos + Off_IoWriteBytes, dataLen), // WriteTransferCount
                        IoReadOpsTotal = (ulong)ReadI64(data, pos + Off_IoReadOps, dataLen),       // ReadOperationCount
                        IoWriteOpsTotal = (ulong)ReadI64(data, pos + Off_IoWriteOps, dataLen),     // WriteOperationCount
                        HardFaults = hardFaults,
                        IsFullAccess = false, // dados do kernel: nenhuma OpenProcess foi feita
                        HasMetrics = true,
                    };
                    if (m.Pid > 0) list.Add(m);

                    if (next == 0) break;
                    // NextEntryOffset e RELATIVO A ENTRADA ATUAL (nao a base). Hard bounds:
                    // a proxima base precisa caber o header minimo dentro dos dados.
                    if (next < 0x100 || (long)pos + next + 0xF0 > dataLen) break;
                    pos += (int)next;
                    iter++;
                }
                catch (IndexOutOfRangeException) { break; } // dados corrompidos -> devolve o que ja parseou (capturavel, nunca AV)
            }
            return list;
        }

        // -- Leitura bounds-checked em array gerenciado --
        // Todas as leituras do parse passam por aqui: start/size fora dos dados =
        // IndexOutOfRangeException (capturavel). E a defesa estrutural contra a classe
        // AccessViolationException/CLR 0x80131506 (fatal, nao-capturavel).
        private static void EnsureRange(int start, int size, int dataLen)
        {
            if (start < 0 || size < 0 || (long)start + size > dataLen)
                throw new IndexOutOfRangeException($"NtQSI parse fora dos limites: start={start} size={size} len={dataLen}");
        }
        private static uint ReadU32(byte[] d, int off, int len) { EnsureRange(off, 4, len); return BitConverter.ToUInt32(d, off); }
        private static long ReadI64(byte[] d, int off, int len) { EnsureRange(off, 8, len); return BitConverter.ToInt64(d, off); }
        private static int ReadU16(byte[] d, int off, int len) { EnsureRange(off, 2, len); return BitConverter.ToUInt16(d, off); }

        /// <summary>
        /// PIDs que possuem janela de topo visível — equivalente barato do p.MainWindowHandle
        /// sem abrir Process: uma passada de EnumWindows por refresh.
        /// </summary>
        public static HashSet<int> GetPidsWithVisibleWindows()
        {
            var set = new HashSet<int>();
            try
            {
                // Callback cruza fronteira nativa: exceção dentro dele NÃO é capturável
                // (fail-fast do CLR — derruba o processo inteiro). Corpo 100% defensivo:
                // nada aqui pode lançar em operação normal.
                EnumWindows((hWnd, lParam) =>
                {
                    try
                    {
                        if (IsWindowVisible(hWnd))
                        {
                            if (GetWindowThreadProcessId(hWnd, out uint pid) != 0 && pid != 0)
                                set.Add((int)pid);
                        }
                    }
                    catch (Exception) { }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception) { }
            return set;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        // ──────── Caminho 2: Toolhelp + OpenProcess de direitos mínimos (fallback) ────────

        private static List<ProcMetrics> EnumerateViaToolhelp()
        {
            var result = new List<ProcMetrics>(512);
            IntPtr snap = CreateToolhelp32Snapshot(0x2 /*TH32CS_SNAPPROCESS*/, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return result;

            var pe = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            try
            {
                if (!Process32FirstW(snap, ref pe)) return result;
                while (true)
                {
                    var m = new ProcMetrics
                    {
                        Pid = (int)pe.th32ProcessID,
                        ParentPid = (int)pe.th32ParentProcessID,
                        Name = pe.szExeFile ?? "",
                        ThreadCount = pe.cntThreads,
                        IsFullAccess = false,
                    };

                    // Rights mínimos estilo TMOG: 0x1010 → 0x1000 (NUNCA 0x0400)
                    IntPtr h = OpenProcess(0x1010, false, m.Pid);
                    if (h == IntPtr.Zero) h = OpenProcess(0x1000, false, m.Pid);
                    if (h != IntPtr.Zero)
                    {
                        try
                        {
                            if (GetProcessTimes(h, out long create, out _, out long kernel, out long user))
                            {
                                m.KernelTime100ns = kernel;
                                m.UserTime100ns = user;
                                m.CreationTimeFiletime = create;
                                m.HasMetrics = true;
                            }
                            if (GetProcessMemoryInfo(h, out MEMORY_COUNTERS mc, (uint)Marshal.SizeOf<MEMORY_COUNTERS>()))
                            {
                                m.WorkingSetBytes = (ulong)mc.WorkingSetSize;
                                m.HasMetrics = true;
                                // QuotaPagedPoolUsage não é commit; commit real só via NtQSI.
                            }
                        }
                        finally { CloseHandle(h); }
                    }
                    result.Add(m);

                    pe.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>();
                    if (!Process32NextW(snap, ref pe)) break;
                }
            }
            finally { CloseHandle(snap); }
            return result;
        }

        // ──────────────────────────── P/Invoke ────────────────────────────

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string lpModuleName);

        // GetProcAddress é ANSI-only: com CharSet.Unicode o lookup falha silenciosamente (retorna 0).
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr hProcess, out MEMORY_COUNTERS ppsmemCounters, uint cb);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public long pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        // PROCESS_MEMORY_COUNTERS (cb = 80 bytes no x64 — offsets batem com decomp.txt do TMOG)
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_COUNTERS
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  CPU POR NÚCLEO — paridade TMOG ("CPU Expandida" com 1 gráfico por
        //  processador lógico). NtQuerySystemInformation class 8
        //  (SystemProcessorPerformanceInformation) devolve Idle/Kernel/User
        //  ACUMULADOS por núcleo lógico — 48 bytes cada, 1 syscall para todos
        //  os núcleos, sem PDH e sem instâncias localizadas.
        // ═════════════════════════════════════════════════════════════════════

        private const int SystemProcessorPerformanceInformation = 8;

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
        {
            public long IdleTime;         // 100ns acumulado
            public long KernelTime;       // inclui idle (padrão Windows)
            public long UserTime;
            public long DpcTime;
            public long InterruptTime;
            public uint InterruptCount;
        }

        private static long[] _prevCoreIdle = Array.Empty<long>();
        private static long[] _prevCoreKernel = Array.Empty<long>();
        private static long[] _prevCoreUser = Array.Empty<long>();
        private static DateTime _prevCoreStamp = DateTime.MinValue;
        private static double[] _lastCorePct = Array.Empty<double>();
        private static readonly object _coreLock = new();

        /// <summary>
        /// Uso (%) de CADA núcleo lógico desde a amostra anterior. Array vazio =
        /// indisponível (primeira chamada, sem ntdll ou classe não suportada).
        /// </summary>
        public static double[] GetPerCoreCpuPercent()
        {
            lock (_coreLock)
            {
                try
                {
                    if (!TryResolveNtQuerySystemInformation() || _ntqsi == null) return _lastCorePct;
                    int cores = Environment.ProcessorCount;
                    if (cores <= 0) return _lastCorePct;
                    int stride = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
                    int size = stride * cores;
                    IntPtr buf = Marshal.AllocHGlobal(size);
                    try
                    {
                        int st = _ntqsi(SystemProcessorPerformanceInformation, buf, size, out _);
                        if (st < 0) return _lastCorePct;

                        var idle = new long[cores];
                        var kernel = new long[cores];
                        var user = new long[cores];
                        for (int i = 0; i < cores; i++)
                        {
                            var spi = Marshal.PtrToStructure<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(buf + i * stride);
                            idle[i] = spi.IdleTime; kernel[i] = spi.KernelTime; user[i] = spi.UserTime;
                        }

                        var now = DateTime.UtcNow;
                        if (_prevCoreStamp == DateTime.MinValue || _prevCoreIdle.Length != cores)
                        {
                            _prevCoreIdle = idle; _prevCoreKernel = kernel; _prevCoreUser = user;
                            _prevCoreStamp = now;
                            return _lastCorePct = new double[cores]; // primeiro tick é primer
                        }
                        double dMs = (now - _prevCoreStamp).TotalMilliseconds;
                        if (dMs < 200) return _lastCorePct; // amostra duplicada: mantém a anterior

                        var pct = new double[cores];
                        for (int i = 0; i < cores; i++)
                        {
                            double dIdle = idle[i] - _prevCoreIdle[i];
                            double dK = kernel[i] - _prevCoreKernel[i];
                            double dU = user[i] - _prevCoreUser[i];
                            double busy = dK + dU; // kernel inclui idle no Windows
                            pct[i] = busy <= 0 ? 0 : Math.Clamp((1 - dIdle / busy) * 100.0, 0, 100);
                        }
                        _prevCoreIdle = idle; _prevCoreKernel = kernel; _prevCoreUser = user;
                        _prevCoreStamp = now;
                        return _lastCorePct = pct;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                catch { return _lastCorePct; }
            }
        }

        // ── DPC/ISR acumulado por núcleo (mesma classe 8; usado pelo monitor de latência) ──
        public sealed class CoreDpcIsrSnapshot
        {
            public long[] DpcTime100ns = Array.Empty<long>();
            public long[] InterruptTime100ns = Array.Empty<long>();
            public uint[] InterruptCount = Array.Empty<uint>();
            public long Stamp100ns;
        }

        private static CoreDpcIsrSnapshot? _lastDpcIsr;

        /// <summary>
        /// DpcTime/InterruptTime/InterruptCount ACUMULADOS por núcleo (classe 8 do NtQSI).
        /// O chamador calcula os deltas — contadores são monotônicos e sobrevivem a wraps
        /// via comparação ulong. Null = indisponível (sem ntdll/classe não suportada).
        /// </summary>
        public static CoreDpcIsrSnapshot? GetPerCoreDpcIsrCumulative()
        {
            lock (_coreLock)
            {
                try
                {
                    if (!TryResolveNtQuerySystemInformation() || _ntqsi == null) return null;
                    int cores = Environment.ProcessorCount;
                    if (cores <= 0) return null;
                    int stride = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
                    IntPtr buf = Marshal.AllocHGlobal(stride * cores);
                    try
                    {
                        int st = _ntqsi(SystemProcessorPerformanceInformation, buf, stride * cores, out _);
                        if (st < 0) return null;
                        var snap = new CoreDpcIsrSnapshot
                        {
                            DpcTime100ns = new long[cores],
                            InterruptTime100ns = new long[cores],
                            InterruptCount = new uint[cores],
                            Stamp100ns = DateTime.UtcNow.ToFileTime(),
                        };
                        for (int i = 0; i < cores; i++)
                        {
                            var spi = Marshal.PtrToStructure<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(buf + i * stride);
                            snap.DpcTime100ns[i] = spi.DpcTime;
                            snap.InterruptTime100ns[i] = spi.InterruptTime;
                            snap.InterruptCount[i] = spi.InterruptCount;
                        }
                        return _lastDpcIsr = snap;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                catch { return null; }
            }
        }

        /// <summary>
        /// Módulos carregados no KERNEL (SystemModuleInformation=11): nome, base e tamanho —
        /// usado para atribuir endereços de rotinas DPC/ISR (ETW) ao driver .sys de origem.
        /// Null = indisponível. Parse em byte[] gerenciado (mesma defesa AV do parse de processos).
        /// </summary>
        public static List<KernelModule>? GetKernelModules()
        {
            try
            {
                if (!TryResolveNtQuerySystemInformation() || _ntqsi == null) return null;
                const int SystemModuleInformation = 11;
                int len = 0x10000;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    IntPtr buf = Marshal.AllocHGlobal(len);
                    byte[]? copy = null;
                    try
                    {
                        int st = _ntqsi(SystemModuleInformation, buf, len, out int needed);
                        const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
                        if ((uint)st == STATUS_INFO_LENGTH_MISMATCH) { len = Math.Max(needed, len * 2); continue; }
                        if (st < 0) return null;
                        int copyLen = needed > 0 ? Math.Min(needed, len) : len;
                        copy = new byte[copyLen];
                        Marshal.Copy(buf, copy, 0, copyLen);
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                    if (copy == null) return null;
                    return ParseKernelModules(copy, copy.Length);
                }
                return null;
            }
            catch { return null; }
        }

        public sealed class KernelModule
        {
            public string Name = "";
            public long Base;
            public ulong Size;
        }

        // RTL_PROCESS_MODULES: ULONG count + RTL_PROCESS_MODULE_INFORMATION[] (296 bytes x64:
        // Section(8) MappedBase(8) ImageBase(8) ImageSize(4) Flags(4) LoadOrder(2) InitOrder(2)
        // LoadCount(2) OffsetToFileName(2) FullPathName[256]).
        private static List<KernelModule> ParseKernelModules(byte[] d, int len)
        {
            var list = new List<KernelModule>(256);
            try
            {
                uint count = BitConverter.ToUInt32(d, 0);
                if (count == 0 || count > 4096) return list;
                int pos = 8; // padding de alinhamento após o count
                const int EntrySize = 296;
                for (int i = 0; i < count && pos + EntrySize <= len; i++, pos += EntrySize)
                {
                    // Layout da entrada: Section(8) MappedBase(8) ImageBase@16(8) ImageSize@24(4)
                    // Flags(4) LoadOrder(2) InitOrder(2) LoadCount(2) OffsetToFileName@38(2)
                    // FullPathName[256]@40. (OffsetToFileName está em 0x26=38 — ler em 36
                    // pegava metade do LoadCount e o nome saía truncado/"g.sys".)
                    int offName = pos + 40 + BitConverter.ToUInt16(d, pos + 38);
                    int nameLen = 0;
                    while (offName + nameLen < len && nameLen < 260 && d[offName + nameLen] != 0) nameLen++;
                    string name = nameLen > 0 ? System.Text.Encoding.ASCII.GetString(d, offName, nameLen) : "";
                    int slash = name.LastIndexOf('\\');
                    if (slash >= 0) name = name[(slash + 1)..];
                    list.Add(new KernelModule
                    {
                        Name = name,
                        Base = BitConverter.ToInt64(d, pos + 16),
                        Size = (ulong)BitConverter.ToUInt32(d, pos + 24),
                    });
                }
            }
            catch { }
            return list;
        }

        /// <summary>
        /// Uso do MODO KERNEL (0-100) — a linha vermelha "Kernel" do TMOG.
        /// GetSystemTimes: KernelTime inclui Idle, então kernel-busy = dKernel - dIdle
        /// e o total = dKernel + dUser.
        /// </summary>
        private static long _sysKPrevIdle, _sysKPrevKernel, _sysKPrevUser;
        private static DateTime _sysKPrevStamp = DateTime.MinValue;
        private static double _lastSysKernel = -1;

        public static double GetSystemKernelPercent()
        {
            lock (_sysKLock)
            {
                try
                {
                    if (!GetSystemTimes(out long idle, out long kernel, out long user)) return -1;
                    var now = DateTime.UtcNow;
                    if (_sysKPrevStamp == DateTime.MinValue) { _sysKPrevIdle = idle; _sysKPrevKernel = kernel; _sysKPrevUser = user; _sysKPrevStamp = now; return -1; }
                    double dMs = (now - _sysKPrevStamp).TotalMilliseconds;
                    if (dMs < 200) return _lastSysKernel;
                    double dIdle = idle - _sysKPrevIdle;
                    double dKernel = kernel - _sysKPrevKernel;
                    double dUser = user - _sysKPrevUser;
                    double total = dKernel + dUser;
                    _sysKPrevIdle = idle; _sysKPrevKernel = kernel; _sysKPrevUser = user; _sysKPrevStamp = now;
                    _lastSysKernel = total <= 0 ? 0 : Math.Clamp((dKernel - dIdle) / total * 100.0, 0, 100);
                    return _lastSysKernel;
                }
                catch { return -1; }
            }
        }
        private static readonly object _sysKLock = new();

        // ═════════════════════════════════════════════════════════════════════
        //  MEMÓRIA DETALHADA (paridade TMOG: Em uso / Disponível / Comprometida /
        //  Em cache / Pool paginado / Não paginado / Handles / Threads / Processos)
        //  1 chamada K32GetPerformanceInfo (contadores em PÁGINAS) + Environment.SystemPageSize
        //  (sem P/Invoke extra), fallback GlobalMemoryStatusEx.
        // ═════════════════════════════════════════════════════════════════════

        public sealed class MemoryDetails
        {
            public ulong TotalBytes, AvailableBytes, UsedBytes;
            public ulong CommitTotalBytes, CommitLimitBytes, CommitPeakBytes;
            public ulong CachedBytes;
            public ulong PagedPoolBytes, NonPagedPoolBytes;
            public ulong HandleCount, ThreadCount, ProcessCount;
            public double LoadPercent;
            public bool Native; // false = veio do fallback GlobalMemoryStatusEx (sem cache/pools)
        }

        /// <summary>Uso REAL do arquivo de paginação (WMI Win32_PageFileUsage).</summary>
        public sealed class PageFileUsage
        {
            public double CurrentUsageMB;   // quantos MB estão de fato no pagefile
            public double PeakUsageMB;
            public double AllocatedMB;      // tamanho atual do arquivo
            public bool Valid;
            public DateTime StampUtc;
        }

        // A consulta WMI leva dezenas a centenas de ms: o resultado é cacheado e
        // atualizado em BACKGROUND. A UI nunca espera por ela.
        private static PageFileUsage? _pageFileCache;
        private static int _pageFileFetching;
        private const int PageFileCacheSeconds = 10;

        /// <summary>
        /// Último uso conhecido do arquivo de paginação — NUNCA bloqueia. Se o dado estiver
        /// velho, dispara uma consulta em background e devolve o valor anterior (ou null na
        /// primeira chamada). É o número "Swap" que o usuário espera ver, medido de verdade
        /// em vez de estimado por subtração.
        /// </summary>
        public static PageFileUsage? GetPageFileUsageNonBlocking()
        {
            bool fresh = _pageFileCache != null &&
                         (DateTime.UtcNow - _pageFileCache.StampUtc).TotalSeconds < PageFileCacheSeconds;
            if (!fresh && Interlocked.CompareExchange(ref _pageFileFetching, 1, 0) == 0)
            {
                _ = Task.Run(() =>
                {
                    try { QueryPageFileUsage(); }
                    catch { }
                    finally { Interlocked.Exchange(ref _pageFileFetching, 0); }
                });
            }
            return _pageFileCache;
        }

        /// <summary>Consulta síncrona (usada pelo fetch em background e por diagnóstico).</summary>
        public static PageFileUsage QueryPageFileUsage()
        {
            var r = new PageFileUsage();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage");
                foreach (var o in searcher.Get())
                {
                    using var mo = (ManagementObject)o;
                    r.AllocatedMB += ToMB(mo["AllocatedBaseSize"]);
                    r.CurrentUsageMB += ToMB(mo["CurrentUsage"]);
                    r.PeakUsageMB += ToMB(mo["PeakUsage"]);
                    r.Valid = true;
                }
            }
            catch { }
            r.StampUtc = DateTime.UtcNow;
            if (r.Valid) _pageFileCache = r;
            return r;
        }

        private static double ToMB(object? v)
        {
            try { return v == null ? 0 : Convert.ToDouble(v); } catch { return 0; }
        }

        /// <summary>Detalhes de memória do sistema para o cabeçalho/aba Resumo.</summary>
        public static MemoryDetails GetMemoryDetails()
        {
            var d = new MemoryDetails();
            try
            {
                // PageSize vem da PRÓPRIA struct (não de Environment.SystemPageSize):
                // é o valor que o kernel usou para contar as páginas.
                uint pageSize = (uint)Environment.SystemPageSize;
                if (K32GetPerformanceInfo(out var pi, (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>()) && pi.cb > 0)
                {
                    d.Native = true;
                    if (pi.PageSize.ToUInt64() > 0) pageSize = (uint)pi.PageSize.ToUInt64();
                    d.TotalBytes = pi.PhysicalTotal.ToUInt64() * pageSize;
                    d.AvailableBytes = pi.PhysicalAvailable.ToUInt64() * pageSize;
                    d.UsedBytes = pi.PhysicalTotal.ToUInt64() > pi.PhysicalAvailable.ToUInt64()
                        ? (pi.PhysicalTotal.ToUInt64() - pi.PhysicalAvailable.ToUInt64()) * pageSize : 0;
                    d.CommitTotalBytes = pi.CommitTotal.ToUInt64() * pageSize;
                    d.CommitLimitBytes = pi.CommitLimit.ToUInt64() * pageSize;
                    d.CommitPeakBytes = pi.CommitPeak.ToUInt64() * pageSize;
                    d.CachedBytes = pi.SystemCache.ToUInt64() * pageSize;
                    d.PagedPoolBytes = pi.KernelPaged.ToUInt64() * pageSize;
                    d.NonPagedPoolBytes = pi.KernelNonPaged.ToUInt64() * pageSize;
                    d.HandleCount = pi.HandleCount;
                    d.ThreadCount = pi.ThreadCount;
                    d.ProcessCount = pi.ProcessCount;
                    if (d.TotalBytes > 0)
                        d.LoadPercent = (d.TotalBytes - d.AvailableBytes) * 100.0 / d.TotalBytes;
                    return d;
                }
            }
            catch { }

            // Fallback: GlobalMemoryStatusEx (sem cache/pools — marca Native=false)
            try
            {
                var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref ms))
                {
                    d.TotalBytes = ms.ullTotalPhys;
                    d.AvailableBytes = ms.ullAvailPhys;
                    d.UsedBytes = ms.ullTotalPhys > ms.ullAvailPhys ? ms.ullTotalPhys - ms.ullAvailPhys : 0;
                    d.CommitTotalBytes = ms.ullTotalPageFile > ms.ullAvailPageFile ? ms.ullTotalPageFile - ms.ullAvailPageFile : 0;
                    d.CommitLimitBytes = ms.ullTotalPageFile;
                    d.LoadPercent = ms.dwMemoryLoad;
                }
            }
            catch { }
            return d;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  REDE — taxa total (técnica do TMOG: GetIfTable2 no lugar de
        //  PerformanceCounter "Network Interface", cujo nome de instância E de
        //  contador é localizado — pt-BR quebra o caminho em inglês).
        //  MIB_IF_ROW2 traz InOctets/OutOctets acumulados por interface; o delta
        //  entre amostras dá bytes/s de TODAS as interfaces.
        //  O layout da struct é AUTO-VALIDADO em runtime (Mtu/Tipo/link speed +
        //  delta monotônico e <= 5 GB/s): se algo não bater, Valid=false — nunca
        //  mostramos número inventado.
        // ═════════════════════════════════════════════════════════════════════

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetIfTable2(out IntPtr table);

        [DllImport("iphlpapi.dll")]
        private static extern void FreeMibTable(IntPtr memory);

        private const uint IF_TYPE_SOFTWARE_LOOPBACK = 24;
        private const uint IF_TYPE_TUNNEL = 131;
        private const uint IF_OPER_STATUS_UP = 1;

        // ⚠ MIB_IF_ROW2 é lida por OFFSETS BRUTOS, não por marshaling de struct:
        // o CLR alinha os campos de forma diferente da struct C (ByValTStr/ByValArray
        // ganham padding próprio), o que deslocava Mtu/Type e fazia a linha 17 em
        // diante ler lixo. Os offsets abaixo foram VALIDADOS EMPIRICAMENTE com dump
        // hex do GetIfTable2 nesta build (26100), byte a byte:
        //   28   Alias[257]                  514 bytes  → fim 542
        //   542  Description[257]            514 bytes  → fim 1056
        //   1056 PhysicalAddressLength (ULONG, = 6 p/ Ethernet)
        //   1060 PhysicalAddress[32]  ·  1092 PermanentPhysicalAddress[32]
        //   1124 Mtu(=1500)  ·  1128 Type(=6 ETHERNET_CSMACD)  ·  1132 TunnelType
        //   1144 AccessType  ·  1148 DirectionType  ·  1152 flags(1B)+pad
        //   1156 OperStatus(=1 UP)  ·  1160 AdminStatus  ·  1164 MediaConnectState
        //   1168 NetworkGuid(16)  ·  1184 CompartmentId  ·  pad
        //   1192 TransmitLinkSpeed  ·  1200 ReceiveLinkSpeed  ·  1208 InOctets …
        private const int IfRowSize = 1352;          // sizeof(MIB_IF_ROW2) no x64
        private const int IfOffAlias = 28;
        private const int IfOffDescription = 542;
        private const int IfOffMtu = 1124;
        private const int IfOffType = 1128;
        private const int IfOffOperStatus = 1156;
        private const int IfOffLinkSpeed = 1200;     // ReceiveLinkSpeed (bits/s)
        private const int IfOffInOctets = 1208;
        private const int IfOffOutOctets = 1280;     // 9 qwords depois de InOctets

        private static IntPtr RowOff(IntPtr row, int offset) => new IntPtr(row.ToInt64() + offset);

        /// <summary>Lê um WCHAR[] (Alias/Description) por offset bruto, com limites seguros.</summary>
        private static string ReadIfString(IntPtr row, int offset)
        {
            try { return Marshal.PtrToStringUni(RowOff(row, offset), 128)?.TrimEnd('\0') ?? ""; }
            catch { return ""; }
        }

        public sealed class NetRateInfo
        {
            public bool Valid;
            public double InBytesPerSec;
            public double OutBytesPerSec;
            public int Interfaces;
            public int ActiveInterfaces;
            public string PrimaryName = "";
        }

        private static readonly Dictionary<uint, (ulong inOct, ulong outOct, DateTime stamp)> _ifPrev = new();
        private static readonly object _ifLock = new();
        private const double MaxPlausibleBytesPerSec = 5.0 * 1024 * 1024 * 1024;

        /// <summary>Diagnóstico da última leitura de rede (suporte/log).</summary>
        public static string LastNetDiag = "";

        /// <summary>
        /// Taxa de rede total (bytes/s) somando todas as interfaces físicas/VPN ativas.
        /// </summary>
        public static NetRateInfo GetNetworkRates()
        {
            var info = new NetRateInfo();
            lock (_ifLock)
            {
                IntPtr table = IntPtr.Zero;
                try
                {
                    if (GetIfTable2(out table) != 0 || table == IntPtr.Zero) return info;
                    uint count = (uint)Marshal.ReadInt32(table);
                    if (count == 0 || count > 1024) return info;
                    var now = DateTime.UtcNow;
                    double elapsed = 0;
                    double totalIn = 0, totalOut = 0;
                    int total = 0, active = 0;
                    long bestLink = 0;
                    string primary = "";
                    long tableAddr = table.ToInt64();

                    int withMtu = 0;
                    for (uint i = 0; i < count; i++)
                    {
                        // Table[] começa em +8 (MIB_IF_ROW2 alinha em 8 no x64)
                        IntPtr row = new IntPtr(tableAddr + 8 + (long)i * IfRowSize);
                        uint index = (uint)i + 1;
                        int mtu = Marshal.ReadInt32(RowOff(row, IfOffMtu));
                        int type = Marshal.ReadInt32(RowOff(row, IfOffType));
                        uint oper = (uint)Marshal.ReadInt32(RowOff(row, IfOffOperStatus));
                        long linkRx = Marshal.ReadInt64(RowOff(row, IfOffLinkSpeed));

                        // ── Auto-validação do layout (valores implausíveis = struct deslocada):
                        //   Mtu=0 é LEGÍTIMO em adaptador NOT_PRESENT, então só validamos
                        //   quando ele veio preenchido; exigimos ao menos 1 interface com MTU.
                        if (type < 1 || type > 300) { LastNetDiag = $"type={type} idx={i}"; return info; }
                        if (mtu != 0 && (mtu < 500 || mtu > 65535)) { LastNetDiag = $"mtu={mtu} idx={i}"; return info; }
                        if (linkRx != 0 && (linkRx < 1000 || linkRx > 1_000_000_000_000L)) { LastNetDiag = $"link={linkRx} idx={i}"; return info; }
                        if (mtu > 0) withMtu++;

                        total++;
                        if (type == IF_TYPE_SOFTWARE_LOOPBACK) continue;
                        if (oper != IF_OPER_STATUS_UP) continue; // 2=DOWN, 6=NOT_PRESENT, 5=DORMANT...

                        // Camadas de filtro NDIS (WFP/QoS) do MESMO hardware repetem os
                        // contadores do adaptador base — somá-las inflaria a taxa em ~3x.
                        string desc = ReadIfString(row, IfOffDescription);
                        string alias = ReadIfString(row, IfOffAlias);
                        string label = string.IsNullOrWhiteSpace(desc) ? alias : desc;
                        if (label.Contains("QoS", StringComparison.OrdinalIgnoreCase) ||
                            label.Contains("WFP", StringComparison.OrdinalIgnoreCase))
                            continue;

                        active++;
                        if (linkRx >= bestLink)
                        {
                            bestLink = linkRx;
                            primary = label;
                        }

                        ulong inOct = (ulong)Marshal.ReadInt64(RowOff(row, IfOffInOctets));
                        ulong outOct = (ulong)Marshal.ReadInt64(RowOff(row, IfOffOutOctets));
                        if (_ifPrev.TryGetValue(index, out var prev))
                        {
                            elapsed = Math.Max(elapsed, (now - prev.stamp).TotalSeconds);
                            if (inOct >= prev.inOct && outOct >= prev.outOct)
                            {
                                totalIn += inOct - prev.inOct;
                                totalOut += outOct - prev.outOct;
                            }
                        }
                        _ifPrev[index] = (inOct, outOct, now);
                    }

                    info.Interfaces = total;
                    info.ActiveInterfaces = active;
                    info.PrimaryName = primary;
                    LastNetDiag = $"ok ifaces={total}/{active} mtuSeen={withMtu}";
                    if (withMtu == 0) return info; // nenhum MTU válido = layout suspeito
                    if (elapsed < 0.2 || active == 0) return info;

                    info.InBytesPerSec = totalIn / elapsed;
                    info.OutBytesPerSec = totalOut / elapsed;
                    // Sanidade final: taxa absurda = leitura desalinhada
                    if (info.InBytesPerSec < 0 || info.OutBytesPerSec < 0) return info;
                    if (info.InBytesPerSec > MaxPlausibleBytesPerSec || info.OutBytesPerSec > MaxPlausibleBytesPerSec) return info;
                    info.Valid = true;
                    return info;
                }
                catch { return info; }
                finally { if (table != IntPtr.Zero) { try { FreeMibTable(table); } catch { } } }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  DETALHES DO PROCESSO (painel do TMOG: linha de comando, sessão,
        //  arquitetura e prioridade base) — tudo com QUERY_LIMITED_INFORMATION,
        //  ou seja, sem admin e sem abrir handle com direitos completos.
        // ═════════════════════════════════════════════════════════════════════

        public sealed class ProcessDetails
        {
            public string CommandLine = "";
            public uint SessionId;
            public bool SessionKnown;
            public string Architecture = "—";
            public int BasePriority;
            public string PriorityClass = "—";
            public string ParentName = "";

            // ── Contadores lidos DIRETO do processo (TMOG: Commit size / Peak working set
            //    / tempos separados / E/S acumulada). Antes esses campos vinham só do
            //    snapshot da lista — quando o refresh caía no fallback .NET, o painel
            //    ficava cheio de "—". Aqui a leitura é do handle do processo selecionado,
            //    com o MESMO direito mínimo (QUERY_LIMITED_INFORMATION), então nunca falta.
            public bool CountersKnown;
            public ulong CommitBytes;          // PrivateUsage (commit privado)
            public ulong PeakWorkingSetBytes;
            public ulong WorkingSetBytes;
            public long KernelTime100ns;
            public long UserTime100ns;
            public ulong IoReadBytes, IoWriteBytes, IoOtherBytes;
            public ulong IoReadOps, IoWriteOps, IoOtherOps;
            public uint PageFaults;
            public int HandleCount;
            public int ThreadCount;
            public uint GdiObjects, UserObjects;
            /// <summary>False = a leitura FALHOU (0 ambíguo: GetGuiResources devolve 0 tanto para
            /// "sem objetos" quanto para erro — SetLastWin32Error distingue). Evita mostrar
            /// "GDI 0 / USER 0" falso para processos que têm janelas.</summary>
            public bool GuiObjectsKnown;
            public bool ElevatedKnown;
            public bool Elevated;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS_EX
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS_NATIVE
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool K32GetProcessMemoryInfo(IntPtr hProcess, out PROCESS_MEMORY_COUNTERS_EX counters, uint cb);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS_NATIVE counters);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int GetProcessHandleCount(IntPtr hProcess, out uint handleCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags); // 0 = GDI, 1 = USER

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        /// <summary>Threads do processo sem OpenProcess extra: usa o snapshot por PID (cache curto).</summary>
        private static int CountThreadsOf(int pid)
        {
            try
            {
                var list = EnumerateProcesses();
                foreach (var p in list) if (p.Pid == pid) return (int)p.ThreadCount;
            }
            catch { }
            return 0;
        }

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int ProcessCommandLineInformation = 60;

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
            IntPtr processInformation, int processInformationLength, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsWow64Process2(IntPtr hProcess, out ushort pProcessMachine, out ushort pNativeMachine);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetPriorityClass(IntPtr hProcess);

        private const ushort IMAGE_FILE_MACHINE_UNKNOWN = 0x0000;
        private const ushort IMAGE_FILE_MACHINE_I386 = 0x014c;
        private const ushort IMAGE_FILE_MACHINE_ARMNT = 0x01c4;
        private const ushort IMAGE_FILE_MACHINE_AMD64 = 0x8664;
        private const ushort IMAGE_FILE_MACHINE_ARM64 = 0xaa64;

        private static string MachineToArch(ushort machine) => machine switch
        {
            IMAGE_FILE_MACHINE_I386 => "x86",
            IMAGE_FILE_MACHINE_AMD64 => "x64",
            IMAGE_FILE_MACHINE_ARM64 => "ARM64",
            IMAGE_FILE_MACHINE_ARMNT => "ARM32",
            _ => "—",
        };

        /// <summary>
        /// Detalhes extras de UM processo (uso sob demanda — só o processo selecionado,
        /// para não pagar OpenProcess em 400 PIDs a cada tick).
        /// </summary>
        public static ProcessDetails GetProcessDetails(int pid)
        {
            var d = new ProcessDetails();
            if (pid <= 0) return d;
            try { if (ProcessIdToSessionId((uint)pid, out uint sid)) { d.SessionId = sid; d.SessionKnown = true; } } catch { }

            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return d;

                // ── Arquitetura (IsWow64Process2; cai no fallback IsWow64Process em Win < 1709)
                try
                {
                    if (IsWow64Process2(h, out ushort procMachine, out ushort nativeMachine))
                        d.Architecture = MachineToArch(procMachine == IMAGE_FILE_MACHINE_UNKNOWN ? nativeMachine : procMachine);
                    else
                        d.Architecture = IsWow64Process(h, out bool wow64) ? (wow64 ? "x86" : "x64") : "—";
                }
                catch { }

                // ── Prioridade base (GetPriorityClass aceita QUERY_LIMITED em Vista+)
                try
                {
                    uint pc = GetPriorityClass(h);
                    d.PriorityClass = pc switch
                    {
                        0x00000040 => "Ocioso",        // IDLE_PRIORITY_CLASS
                        0x00004000 => "Abaixo do normal", // BELOW_NORMAL
                        0x00000020 => "Normal",        // NORMAL
                        0x00008000 => "Acima do normal", // ABOVE_NORMAL
                        0x00000080 => "Alta",          // HIGH
                        0x00000100 => "Tempo real",    // REALTIME
                        _ => "—",
                    };
                    d.BasePriority = pc switch
                    {
                        0x00000040 => 4, 0x00004000 => 6, 0x00000020 => 8,
                        0x00008000 => 10, 0x00000080 => 13, 0x00000100 => 24, _ => 0,
                    };
                }
                catch { }

                // ── Linha de comando (NtQueryInformationProcess class 60) — padrão
                //    "2 chamadas": a 1ª devolve STATUS_INFO_LENGTH_MISMATCH + tamanho.
                try
                {
                    int st = NtQueryInformationProcess(h, ProcessCommandLineInformation, IntPtr.Zero, 0, out int need);
                    if (need > 0 && need < 64 * 1024)
                    {
                        IntPtr buf = Marshal.AllocHGlobal(need);
                        try
                        {
                            st = NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, need, out int used);
                            if (st >= 0 && used >= 16)
                            {
                                // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer }
                                // Length é USHORT (ler como Int64 mistura MaximumLength — armadilha documentada)
                                ushort len = (ushort)Marshal.ReadInt16(buf, 0);
                                IntPtr strPtr = Marshal.ReadIntPtr(buf, 8);
                                long bufStart = buf.ToInt64(), bufEnd = bufStart + used;
                                // PtrToStringUni com ponteiro inválido é FATAL (CLR 0x80131506)
                                // → validar os limites ANTES de qualquer leitura.
                                if (len > 0 && strPtr.ToInt64() >= bufStart && strPtr.ToInt64() + len <= bufEnd)
                                    d.CommandLine = Marshal.PtrToStringUni(strPtr, len / 2) ?? "";
                            }
                        }
                        finally { Marshal.FreeHGlobal(buf); }
                    }
                }
                catch { }

                // ── Contadores nativos (commit, pico, tempos, E/S, GDI/USER, handles) ──
                try
                {
                    if (K32GetProcessMemoryInfo(h, out var mem, (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>()))
                    {
                        d.CommitBytes = (ulong)mem.PrivateUsage.ToUInt64();
                        d.PeakWorkingSetBytes = (ulong)mem.PeakWorkingSetSize.ToUInt64();
                        d.WorkingSetBytes = (ulong)mem.WorkingSetSize.ToUInt64();
                        d.PageFaults = mem.PageFaultCount;
                        d.CountersKnown = true;
                    }
                }
                catch { }
                try
                {
                    if (GetProcessTimes(h, out _, out _, out long kt, out long ut))
                    {
                        d.KernelTime100ns = kt; d.UserTime100ns = ut;
                    }
                }
                catch { }
                try
                {
                    if (GetProcessIoCounters(h, out var io))
                    {
                        d.IoReadBytes = io.ReadTransferCount; d.IoWriteBytes = io.WriteTransferCount; d.IoOtherBytes = io.OtherTransferCount;
                        d.IoReadOps = io.ReadOperationCount; d.IoWriteOps = io.WriteOperationCount; d.IoOtherOps = io.OtherOperationCount;
                    }
                }
                catch { }
                try { if (GetProcessHandleCount(h, out uint hc) != 0) d.HandleCount = (int)hc; } catch { }
                try
                {
                    // GetGuiResources devolve 0 em FALHA (SetLastError=true liga o motivo).
                    // 0 legítimo = processo sem GUI (serviço) — é diferente de "não consegui ler".
                    uint gdi = GetGuiResources(h, 0);
                    int eGdi = Marshal.GetLastWin32Error();
                    uint usr = GetGuiResources(h, 1);
                    int eUsr = Marshal.GetLastWin32Error();
                    d.GdiObjects = gdi; d.UserObjects = usr;
                    d.GuiObjectsKnown = (gdi != 0 || eGdi == 0) && (usr != 0 || eUsr == 0);
                }
                catch { }
                try
                {
                    if (OpenProcessToken(h, 0x0008 /* TOKEN_QUERY */, out IntPtr tok))
                    {
                        try
                        {
                            uint len = 0;
                            GetTokenInformation(tok, 20 /* TokenElevation */, IntPtr.Zero, 0, out len);
                            if (len >= 4)
                            {
                                IntPtr buf = Marshal.AllocHGlobal(4);
                                try
                                {
                                    if (GetTokenInformation(tok, 20, buf, 4, out _))
                                    {
                                        d.Elevated = Marshal.ReadInt32(buf) != 0;
                                        d.ElevatedKnown = true;
                                    }
                                }
                                finally { Marshal.FreeHGlobal(buf); }
                            }
                        }
                        finally { CloseHandle(tok); }
                    }
                }
                catch { }
                try { d.ThreadCount = CountThreadsOf(pid); } catch { }
            }
            catch { }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
            return d;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
            IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);

        // (reservado para a Fase 3 — CallNtPowerInformation / GetIfTable2 / K32GetPerformanceInfo)
        private const uint _reservedPhase3 = 0;
    }
}
