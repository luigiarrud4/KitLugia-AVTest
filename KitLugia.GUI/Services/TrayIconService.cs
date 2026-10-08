using System;
using System.Drawing;
using System.Diagnostics;
using System.Windows.Forms;
using System.Windows.Threading;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using KitLugia.Core;
using Microsoft.Win32.TaskScheduler;
using System.IO;
using System.Text;
using Application = System.Windows.Application;
using Timer = System.Windows.Threading.DispatcherTimer;

namespace KitLugia.GUI.Services
{
    // Helper: logs first failure per operation, then stays silent until success resumes
    public static class ConditionalLog
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _lastFailed = new();

        public static void Try(string key, System.Action action)
        {
            try { action(); _lastFailed[key] = false; }
            catch (Exception ex) { LogOnce(key, ex); }
        }

        public static void LogOnce(string key, Exception ex)
        {
            if (!_lastFailed.TryGetValue(key, out bool lastFailed) || !lastFailed)
            {
                _lastFailed[key] = true;
                KitLugia.Core.Logger.Log($"⚠️ {key}: {ex.GetType().Name} — {ex.Message}");
            }
        }

        public static void Reset(string key) => _lastFailed.TryRemove(key, out _);
    }

    public static class Win32Api
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        public const uint GW_OWNER = 4;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        public const uint WM_CLOSE = 0x0010;

        // --- Turbo Explorer (F11 re-render trick) ---
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        public const uint WM_KEYDOWN = 0x0100;
        public const uint WM_KEYUP = 0x0101;
        public const int VK_F11 = 0x7A;
        public const string ExplorerWindowClass = "CabinetWClass";

        // --- Invisibilidade temporária de janela (Turbo Explorer sem pular a tela) ---
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        public const long WS_EX_LAYERED = 0x00080000;
        public const uint LWA_ALPHA = 0x00000002;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        // --- Hybrid CPU Detection (P-cores + E-cores) ---
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetLogicalProcessorInformationEx(int RelationshipType, IntPtr Buffer, ref uint ReturnedLength);

        public const int RelationProcessorCore = 0;

        public static bool IsCpuHybrid()
        {
            try
            {
                if (Environment.OSVersion.Version.Build < 22000) return false;
                uint size = 0;
                GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref size);
                if (size == 0) return false;
                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref size))
                        return false;
                    IntPtr ptr = buffer;
                    uint remaining = size;
                    while (remaining > 0)
                    {
                        int relationship = Marshal.ReadInt32(ptr);
                        uint entrySize = (uint)Marshal.ReadInt32(ptr, 4);
                        if (relationship == RelationProcessorCore)
                        {
                            byte efficiencyClass = Marshal.ReadByte(ptr, 9);
                            if (efficiencyClass > 0) return true;
                        }
                        remaining -= entrySize;
                        ptr += (int)entrySize;
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return false;
        }

        // === Extensões Multi-Layer Accelerator (ntdll) ===
        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int NtSetInformationProcess(IntPtr processHandle, int processInformationClass, ref int processInformation, int processInformationLength);

        private const int ProcessIoPriority = 33;
        private const int ProcessPagePriority = 39;

        // ============================================================
        // SONDAGEM DE CAPACIDADE (10/10/2026)
        // ============================================================
        // I/O priority e page priority so existem em ntdll, via
        // NtSetInformationProcess, com numeros de classe FIXOS (33 e 39). Sao APIs nao
        // documentadas: se um build futuro mudar os numeros, ou se o acesso for negado,
        // a chamada falha — e ate aqui a falha era SILENCIOSA (o valor de retorno era
        // ignorado dentro de ConditionalLog.Try). Um motor que se diz fiavel nao pode
        // falhar em silencio.
        //
        // A auditoria ao Process Lasso (docs/GAMEBOOST_PROCESS_LASSO_AUDIT.md) mostra
        // que o Bitsum NAO usa estas duas APIs — so prioridade de CPU e EcoQoS, ambas
        // documentadas. Portanto nao ha referencia para estes numeros: o unico jeito
        // de saber se valem e'experimenta-los. Fazemo-lo UMA vez, com o proprio
        // processo do Kit como cobaia: se pegar, fica marcada como disponivel para
        // sempre; se nao pegar, registamos no log uma vez e deixamos de tentar.
        // Nao muda o comportamento numa maquina normal — so torna a falha visivel.
        private static int _ioPrioProbe = 0;      // 0=desconhecido, 1=ok, 2=indisponivel
        private static int _pagePrioProbe = 0;

        // STATUS_PRIVILEGE_NOT_HELD: a classe existe, mas o token nao tem o
        // privilegio. Medido: com o Kit CORRENDO ELEVADO a classe 33 (I/O)
        // continua a devolver este codigo — ou seja, elevar nao resolve.
        private const int STATUS_PRIVILEGE_NOT_HELD = unchecked((int)0xC0000061);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr h, out uint exitCode);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        // STILL_ACTIVE: o unico valor de codigo de saida que o Windows emite.
        // Um handle de processo morto devolve qualquer outra coisa.
        private const uint STILL_ACTIVE = 259;

        // Sonda a capacidade do SISTEMA, uma unica vez, contra um handle que nos
        // compete de facto: o do proprio processo do Kit. Isto e' deliberado.
        //
        // PORQUEE contra o proprio processo: a versao anterior decidia a partir
        // da primeira chamada a um processo qualquer, e isso era errado por
        // desenho. Mediu-se que a classe 39 (page priority) devolve SUCESSO
        // mesmo com um handle invalido, e que nem GetProcessId nem
        // GetExitCodeProcess aceitam um handle falso (0xFFFFFFFF devolveu
        // "STILL_ACTIVE") — ou seja, nao ha como validar um handle de fora.
        // Como a capacidade e' do sistema, sonda-la no nosso proprio handle
        // elimina essa corrida.
        //
        // CUIDADO com o valor da sonda: a resposta da classe 33 DEPENDE DO VALOR,
        // nao so do processo. Medido, com o Kit CORRENDO ELEVADO:
        //     hint=0 (VeryLow) -> 0x00000000 (aceite)
        //     hint=2 (Normal)  -> 0x00000000 (aceite)
        //     hint=3 (High)    -> 0xC0000061 (RECUSADO pelo Windows)
        // A recusa de hint=3 nao e' falta de privilegio — e' o Windows a
        // impedir que um processo NAO elevado suba a I/O de outro processo. Por
        // isso a sonda tem de usar o valor que o motor realmente quer aplicar;
        // sondar com um valor "gentil" daria um SIM falso.
        private static void SondaCapacidadeNtdll(int infoClass, ref int probe, string nomeLog,
                                              int valorQueOQuero)
        {
            if (probe != 0) return;
            try
            {
                int v = valorQueOQuero;
                int status = NtSetInformationProcess(GetCurrentProcess(), infoClass, ref v, sizeof(int));
                bool ok = status >= 0;         // NTSTATUS: 0 = sucesso
                probe = ok ? 1 : 2;

                if (ok)
                    Logger.Log($"✅ GameBoost: {nomeLog} disponível (classe {infoClass} confirmada).");
                else if (status == STATUS_PRIVILEGE_NOT_HELD)
                    Logger.Log($"⚠️ GameBoost: {nomeLog} recusada pelo Windows (0xC0000061). " +
                               "Medido: subir a I/O de outro processo exige um token com " +
                               "permissão — o nosso não a tem. O motor fica no Normal " +
                               "e segue normal; o resto não é afetado.");
                else
                    Logger.Log($"⚠️ GameBoost: {nomeLog} indisponivel neste sistema " +
                               $"(classe {infoClass} devolveu 0x{status:X8}). " +
                               "Ajuste omitido — o resto do motor continua normal.");
            }
            catch { probe = 2; }
        }

        private static bool TrySetNtdllClass(IntPtr handle, int infoClass, int value,
                                             ref int probe, string nomeLog)
        {
            if (probe == 2) return false;      // ja sabemos que nao funciona
            // A sonda usa o valor REAL que queremos aplicar - e' o que decide.
            SondaCapacidadeNtdll(infoClass, ref probe, nomeLog, value);
            if (probe == 2) return false;      // a sonda acabou de decidir que nao

            try
            {
                int v = value;
                int status = NtSetInformationProcess(handle, infoClass, ref v, sizeof(int));
                return status >= 0;            // NTSTATUS: 0 = sucesso
            }
            catch { return false; }
        }

        public static void SetProcessIoPriority(IntPtr handle, int priorityHint)
        {
            if (_ioPrioProbe == 2) return;
            TrySetNtdllClass(handle, ProcessIoPriority, priorityHint, ref _ioPrioProbe, "I/O priority");
        }

        public static void SetProcessPagePriority(IntPtr handle, int pagePriority)
        {
            if (_pagePrioProbe == 2) return;
            TrySetNtdllClass(handle, ProcessPagePriority, pagePriority, ref _pagePrioProbe, "Page priority");
        }

        // === SetWinEventHook (GameBoost instantâneo) ===
        public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowEnabled(IntPtr hWnd);

        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        public const uint WINEVENT_SKIPOWNTHREAD = 0x0004;

        // === EcoQoS (Windows 11 Power Throttling) ===
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessInformation(IntPtr hProcess, int ProcessInformationClass, IntPtr ProcessInformation, uint ProcessInformationSize);

        public const int ProcessPowerThrottling = 4;
        /// PROCESS_INFORMATION_CLASS: 0 = ProcessMemoryPriority (MEMORY_PRIORITY_INFORMATION).
        public const int ProcessMemoryPriority = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_POWER_THROTTLING_STATE { public uint Version; public uint ControlMask; public uint StateMask; }

        public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

        /// <summary>
        /// Quando LIGADO, o Windows IGNORA os pedidos de timer resolution do processo
        /// (o jogo deixa de "segurar" os timers). ControlMask escolhe o mecanismo e
        /// StateMask declara se ele fica ativo. O kit usa este bit sempre a DESLIGADO
        /// (StateMask = 0) para HONRAR o pedido de timer que o proprio jogo faz — que e
        /// a forma correta de acelerar o timer de um jogo em 2026 (o pedido global
        /// NtSetTimerResolution nao se aplica ao processo do jogo desde o Win10 2004).
        /// </summary>
        public const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x2;

        // === Thread Memory Priority ===
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetThreadInformation(IntPtr hThread, int ThreadInformationClass, IntPtr ThreadInformation, uint ThreadInformationSize);

        public const int ThreadMemoryPriority = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORY_PRIORITY_INFORMATION { public uint MemoryPriority; }

        public const uint MEMORY_PRIORITY_VERY_LOW = 1;
        public const uint MEMORY_PRIORITY_LOW = 2;
        public const uint MEMORY_PRIORITY_MEDIUM = 3;
        public const uint MEMORY_PRIORITY_BELOW_NORMAL = 4;
        public const uint MEMORY_PRIORITY_NORMAL = 5;

        /// <summary>
        /// Define a prioridade de MEMORIA do PROCESSO (1=VERY_LOW .. 5=NORMAL/default).
        ///
        /// BUG CORRIGIDO (05/10/2026): esta chamada usava SetThreadInformation com a
        /// classe 0 — que em THREAD_INFORMATION_CLASS e' ThreadBasicInformation, NAO
        /// memory priority — e ainda passava um HANDLE DE PROCESSO onde a API exige um
        /// HANDLE DE THREAD. Medido no host: falhava com erro 6 (INVALID_HANDLE) e o
        /// valor lido de volta era SEMPRE 5 (NORMAL), ou seja NUNCA fez nada. A API
        /// correta e' SetProcessInformation(hProcess, ProcessMemoryPriority, MPI, 4):
        /// medida 5 -> 1 (VERY_LOW) e depois 1 -> 5 a restaurar.
        /// O nome antigo foi mantido porque todos os chamadores ja passam proc.Handle
        /// (um handle de processo) — que e' exatamente o que a API correta quer.
        /// </summary>
        public static void SetThreadMemoryPriority(IntPtr processHandle, uint priority)
        {
            ConditionalLog.Try("SetThreadMemoryPriority", () =>
            {
                var memPrio = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = priority };
                IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(memPrio));
                try
                {
                    Marshal.StructureToPtr(memPrio, ptr, false);
                    SetProcessInformation(processHandle, ProcessMemoryPriority, ptr, (uint)Marshal.SizeOf(memPrio));
                }
                finally { Marshal.FreeHGlobal(ptr); }
            });
        }

        /// <summary>
        /// EcoQoS + "honrar o timer do jogo" num so passo (um unico SetProcessInformation).
        ///   ecoQoS=true  -> PROCESS_POWER_THROTTLING_EXECUTION_SPEED ligado (poupar energia)
        ///   ecoQoS=false -> desligado (performance). Corrigido em 03/10: EcoQoS e' power
        ///                   saving, usar como boost era o contrario do que se queria.
        /// O bit IGNORE_TIMER_RESOLUTION fica SEMPRE a 0 = o Windows honra o pedido de timer
        /// do processo. Um jogo minimizado/oculto perde esse direito sozinho no Win11; se
        /// ele esta em foreground, o timer dele e' respeitado.
        /// </summary>
        public static void SetPowerThrottling(IntPtr processHandle, bool ecoQoS)
        {
            ConditionalLog.Try("SetPowerThrottling", () =>
            {
                var state = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = 1,
                    ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED
                               | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
                    // 0 no bit de execucao = performance; 0 no de timer = honorar o pedido.
                    StateMask = ecoQoS ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0u
                };
                IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(state));
                try
                {
                    Marshal.StructureToPtr(state, ptr, false);
                    SetProcessInformation(processHandle, ProcessPowerThrottling, ptr, (uint)Marshal.SizeOf(state));
                }
                finally { Marshal.FreeHGlobal(ptr); }
            });
        }

        /// <summary>
        /// Carga de CPU do SISTEMA (0..100) medida por delta entre dois GetSystemTimes.
        /// E' o gate do ProBalance: so vale a pena despriorizar processos de fundo
        /// quando a maquina esta mesmo sob carga (a logica do ProBalance do Process Lasso).
        /// Retorna -1 enquanto nao ha amostra anterior.
        /// </summary>
        [DllImport("kernel32.dll")]
        private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME { public uint Low; public uint High; }

        private static readonly object _sysCpuLock = new();
        private static ulong _prevIdle, _prevKernel, _prevUser;
        private static bool _sysCpuPrimed;

        public static int GetSystemCpuLoad()
        {
            lock (_sysCpuLock)
            {
                if (!GetSystemTimes(out var i, out var k, out var u))
                    return -1;

                ulong idle = ((ulong)i.High << 32) | i.Low;
                ulong kernel = ((ulong)k.High << 32) | k.Low;
                ulong user = ((ulong)u.High << 32) | u.Low;

                if (!_sysCpuPrimed)
                {
                    _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
                    _sysCpuPrimed = true;
                    return -1;
                }

                ulong dIdle = idle - _prevIdle;
                ulong dKernel = kernel - _prevKernel;
                ulong dUser = user - _prevUser;
                _prevIdle = idle; _prevKernel = kernel; _prevUser = user;

                // kernel inclui idle, logo o total "ocupado" e' kernel+user-idle.
                ulong busy = dKernel + dUser;
                if (busy == 0) return 0;
                ulong total = busy + dIdle;
                if (total == 0) return 0;
                return (int)Math.Clamp(busy * 100 / total, 0, 100);
            }
        }

        // === Thread Efficiency Mode (P-Cores Only, Win11 24H2+) ===
        public const int ThreadEfficiencyMode = 5;

        [StructLayout(LayoutKind.Sequential)]
        public struct THREAD_EFFICIENCY_MODE { public byte UseEfficiencyClass; }

        public static void SetThreadEfficiencyMode(IntPtr threadHandle, bool useEfficiencyClass)
        {
            if (Environment.OSVersion.Version.Build < 26100) return;
            ConditionalLog.Try("SetThreadEfficiencyMode", () =>
            {
                var mode = new THREAD_EFFICIENCY_MODE { UseEfficiencyClass = useEfficiencyClass ? (byte)1 : (byte)0 };
                IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(mode));
                try
                {
                    Marshal.StructureToPtr(mode, ptr, false);
                    if (!SetThreadInformation(threadHandle, ThreadEfficiencyMode, ptr, (uint)Marshal.SizeOf(mode)))
                        ReportarClasseRecusada("Thread Efficiency (P-cores)", ThreadEfficiencyMode);
                }
                finally { Marshal.FreeHGlobal(ptr); }
            });
        }

        // === Game Mode (SetProcessGameClassInfo) ===
        public const int ProcessGameClassInfo = 13;

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_GAME_CLASS_INFO { public uint GameMode; public uint GameModeFlags; }

        public static void SetProcessGameClassInfo(IntPtr processHandle, bool enableGameMode)
        {
            ConditionalLog.Try("SetProcessGameClassInfo", () =>
            {
                var info = new PROCESS_GAME_CLASS_INFO { GameMode = enableGameMode ? 1u : 0u, GameModeFlags = 0 };
                IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(info));
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    if (!SetProcessInformation(processHandle, ProcessGameClassInfo, ptr, (uint)Marshal.SizeOf(info)))
                        ReportarClasseRecusada("Game Mode", ProcessGameClassInfo);
                }
                finally { Marshal.FreeHGlobal(ptr); }
            });
        }

        // Classes PRIVADAS do Windows (ntdll / classes nao documentadas) que se
        // provou serem RECUSADAS neste build. Medido com o gbknobs:
        //   classe 13 (Game Mode)   -> err 87 em todos os tamanhos
        //   classe  5 (Thread Efficiency) -> err 87 em todos os tamanhos
        // Nao e' "tamanho errado": falham em TODOS os tamanhos e no proprio
        // processo do Kit, portanto a classe nao esta implementada aqui.
        // O aviso sai UMA vez para nao inundar o log a cada jogo.
        private static readonly HashSet<int> _classesRecusadas = new HashSet<int>();

        private static void ReportarClasseRecusada(string nome, int infoClass)
        {
            if (!_classesRecusadas.Add(infoClass)) return;
            Logger.Log($"[AVISO] GameBoost: {nome} (classe {infoClass}) recusada pelo Windows " +
                       "neste build - medido. Este ajuste nao tem efeito aqui; o resto do " +
                       "motor continua normal.");
        }

        // === Thread/Process helpers ===
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

        public const uint THREAD_SET_INFORMATION = 0x0020;
        public const uint THREAD_QUERY_INFORMATION = 0x0040;

        // === Win32PrioritySeparation Registry ===
        //
        // BUG CORRIGIDO (05/10/2026): o "desligar" gravava 2 (que significa "o sistema da
        // MESMA prioridade a todos") sem nunca ler o valor original. Numa maquina que ja
        // vinha com 0x26 (38, o perfil de servidores) o revert NAO restaurava: piorava.
        // Agora o original e' lido na primeira ativacao e devolvido no desligar.
        private static readonly object _prioSepLock = new();
        private static int? _origWin32PrioritySeparation;

        /// <summary>Le o valor atual (null = chave ausente). Usado para provar a fidelidade do revert.</summary>
        public static int? ReadWin32PrioritySeparation()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\PriorityControl");
                var v = key?.GetValue("Win32PrioritySeparation");
                if (v == null) return null;
                return Convert.ToInt32(v);
            }
            catch { return null; }
        }

        public static void SetWin32PrioritySeparation(bool enableForegroundBoost)
        {
            ConditionalLog.Try("SetWin32PrioritySeparation", () =>
            {
                lock (_prioSepLock)
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\PriorityControl", true);
                    if (key == null) return;

                    if (enableForegroundBoost)
                    {
                        // Guarda o original uma unica vez (idempotente: nao rouba o 38 de si proprio).
                        _origWin32PrioritySeparation ??= ReadWin32PrioritySeparation();
                        key.SetValue("Win32PrioritySeparation", 38, Microsoft.Win32.RegistryValueKind.DWord);
                    }
                    else
                    {
                        if (_origWin32PrioritySeparation is int orig)
                        {
                            key.SetValue("Win32PrioritySeparation", orig, Microsoft.Win32.RegistryValueKind.DWord);
                            _origWin32PrioritySeparation = null;
                        }
                        else
                        {
                            // Nunca ativamos: nao ha nada nosso para desfazer.
                            // (Fallback antigo gravava 2 e estragava maquinas que ja vinham com 38.)
                        }
                    }
                }
            });
        }

        // === Timer Resolution (NtSetTimerResolution) ===
        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int NtSetTimerResolution(int DesiredResolution, bool SetResolution, out int CurrentResolution);

        private static int _originalTimerResolution = 0;
        private static bool _timerResolutionChanged = false;

        public static void BoostTimerResolution()
        {
            if (_timerResolutionChanged) return;
            ConditionalLog.Try("BoostTimerResolution", () =>
            {
                NtSetTimerResolution(0, false, out _originalTimerResolution);
                // 1ms (10000) em vez de 0.5ms (5000) — 0.5ms causa estouro/popping
                // em dispositivos de áudio virtual (Voicemeeter, VB-Cable etc.) porque
                // aumenta latência DPC/ISR e causa underruns no buffer de software.
                // 1ms é seguro para áudio e ainda melhora performance em jogos.
                int desired = 10000;
                int result = NtSetTimerResolution(desired, true, out int current);
                if (result == 0)
                {
                    _timerResolutionChanged = true;
                    Logger.Log($"⏱️ Timer Resolution: {_originalTimerResolution / 10000.0:F2}ms → {current / 10000.0:F2}ms (boosted)");
                }
            });
        }

        public static void RestoreTimerResolution()
        {
            if (!_timerResolutionChanged) return;
            ConditionalLog.Try("RestoreTimerResolution", () =>
            {
                NtSetTimerResolution(_originalTimerResolution, true, out int current);
                _timerResolutionChanged = false;
                Logger.Log($"⏱️ Timer Resolution restaurado: {current / 10000.0:F2}ms");
            });
        }

        // === Privilege Elevation ===
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes, int ImpersonationLevel, int TokenType, out IntPtr phNewToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool SetThreadToken(IntPtr ThreadHandle, IntPtr TokenHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetCurrentThread();

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out long lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        public const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        // ★ CORREÇÃO 28/09: LUID tem alinhamento 4 — com Pack=1 o campo caía no offset 1 e o
        // Windows lia um LUID inválido (AdjustTokenPrivileges = TRUE + erro 1300), então o
        // privilégio nunca era habilitado de fato. Pack = 4 reproduz o layout nativo (16 bytes).
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public long Luid; public uint Attributes; }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential)]
        public class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
        }

        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint TOKEN_DUPLICATE = 0x0002;
        public const uint TOKEN_IMPERSONATE = 0x0004;
        public const uint TOKEN_QUERY = 0x0008;
        public const int SecurityImpersonation = 2;
        public const int TokenImpersonation = 2;

        // === Toolhelp32 Thread Enumeration ===
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Thread32First(IntPtr hSnapshot, ref THREADENTRY32 lpte);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Thread32Next(IntPtr hSnapshot, ref THREADENTRY32 lpte);

        public const uint TH32CS_SNAPTHREAD = 0x00000004;

        [StructLayout(LayoutKind.Sequential)]
        public struct THREADENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ThreadID;
            public uint th32OwnerProcessID;
            public uint tpBasePri;
            public uint tpDeltaPri;
            public uint dwFlags;
        }

        public static List<uint> GetThreadIds(uint targetPid)
        {
            var threadIds = new List<uint>();
            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
            if (snapshot == IntPtr.Zero || snapshot == (IntPtr)(-1)) return threadIds;
            try
            {
                var entry = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
                if (Thread32First(snapshot, ref entry))
                {
                    do { if (entry.th32OwnerProcessID == targetPid) threadIds.Add(entry.th32ThreadID); }
                    while (Thread32Next(snapshot, ref entry));
                }
            }
            catch (Exception ex) { ConditionalLog.LogOnce("GetThreadIds", ex); }
            finally { CloseHandle(snapshot); }
            return threadIds;
        }

        public static void SetThreadEfficiencyForAllThreads(uint pid, bool useEfficiencyClass)
        {
            if (Environment.OSVersion.Version.Build < 26100) return;
            var threadIds = GetThreadIds(pid);
            foreach (uint tid in threadIds)
            {
                IntPtr hThread = IntPtr.Zero;
                try
                {
                    hThread = OpenThread(THREAD_SET_INFORMATION | THREAD_QUERY_INFORMATION, false, tid);
                    if (hThread != IntPtr.Zero) SetThreadEfficiencyMode(hThread, useEfficiencyClass);
                }
                catch (Exception ex) { ConditionalLog.LogOnce("SetThreadEfficiencyForAllThreads", ex); }
                finally { if (hThread != IntPtr.Zero) CloseHandle(hThread); }
            }
        }

        // === Thread analysis for P-Cores Only detection ===
        public const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetThreadIdealProcessorEx(IntPtr hThread, out PROCESSOR_NUMBER lpIdealProcessor);

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESSOR_NUMBER { public ushort Group; public byte Number; public byte Reserved; }

        public static HashSet<int> GetECoreProcessorNumbers()
        {
            var result = new HashSet<int>();
            try
            {
                if (Environment.OSVersion.Version.Build < 22000) return result;
                uint size = 0;
                GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref size);
                if (size == 0) return result;
                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref size)) return result;
                    IntPtr ptr = buffer;
                    uint remaining = size;
                    while (remaining > 0)
                    {
                        int relationship = Marshal.ReadInt32(ptr);
                        uint entrySize = (uint)Marshal.ReadInt32(ptr, 4);
                        if (relationship == RelationProcessorCore)
                        {
                            byte efficiencyClass = Marshal.ReadByte(ptr, 9);
                            if (efficiencyClass > 0)
                            {
                                ulong mask = (ulong)Marshal.ReadIntPtr(ptr + 32);
                                for (int i = 0; i < 64; i++) { if ((mask & (1UL << i)) != 0) result.Add(i); }
                            }
                        }
                        remaining -= entrySize;
                        ptr += (int)entrySize;
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            catch (Exception ex) { ConditionalLog.LogOnce("GetECoreProcessorNumbers", ex); }
            return result;
        }

        public static int GetThreadCountOnECores(uint processId)
        {
            int count = 0;
            try
            {
                var eCores = GetECoreProcessorNumbers();
                if (eCores.Count == 0) return 0;
                IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
                if (snapshot == IntPtr.Zero || snapshot == (IntPtr)(-1)) return 0;
                try
                {
                    var te = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
                    if (!Thread32First(snapshot, ref te)) return 0;
                    do
                    {
                        if (te.th32OwnerProcessID == processId)
                        {
                            IntPtr hThread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, te.th32ThreadID);
                            if (hThread == IntPtr.Zero) continue;
                            try { if (GetThreadIdealProcessorEx(hThread, out var procNum) && eCores.Contains(procNum.Number)) count++; }
                            finally { CloseHandle(hThread); }
                        }
                    }
                    while (Thread32Next(snapshot, ref te));
                }
                finally { CloseHandle(snapshot); }
            }
            catch (Exception ex) { ConditionalLog.LogOnce("GetThreadCountOnECores", ex); }
            return count;
        }

        // === Job Object CPU Rate Control (hard cap per-process) ===
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        public const int JobObjectCpuRateControlInformation = 15;
        public const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1;
        public const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            public uint ControlFlags;
            public uint CpuRate;
        }
    }

    public class TrayIconService : IDisposable
    {
        private NotifyIcon? _trayIcon;
        private DispatcherTimer _monitorTimer;
        private Icon? _currentIcon;
        

        public bool GameBarPresenceWriterDisabled { get; set; } = false;

        // Otimizações da comunidade (Reddit) - aplicadas uma vez no startup conforme preferência
        public bool SmartScreenDisabled { get; set; } = false;
        public bool EdgeUpdateDisabled { get; set; } = false;
        public bool CompatTelRunnerDisabled { get; set; } = false;
        public bool SearchIndexerDisabled { get; set; } = false;
        public bool TextInputHostDisabled { get; set; } = false;

        // Telemetria e relatórios - serviços + tarefas agendadas (aplicadas no startup conforme preferência)
        public bool DiagTrackSvcDisabled { get; set; } = false;
        public bool DmwappushSvcDisabled { get; set; } = false;
        public bool WerSvcDisabled { get; set; } = false;
        public bool PcaSvcDisabled { get; set; } = false;
        public bool TelemetryTasksDisabled { get; set; } = false;

        public bool IsInitialized { get; private set; } = false;

        // === Turbo Explorer automático (watchdog do explorer.exe) ===
        private System.Threading.Timer? _explorerWatchTimer;
        private volatile int _explorerWatchPid;      // PID atual do explorer.exe da nossa sessão
        private volatile bool _explorerTurboArmed;   // aguardando aplicar F11 (pasta do usuário ou própria)

        /// <summary>
        /// Quando ligado, o Kit detecta o processo explorer.exe. Ao (re)iniciar
        /// (login do Windows, crash/restart do shell) ele re-aplica o truque F11 (re-render)
        /// na 1ª janela de pasta que abrir — 1x por processo.
        /// Default TRUE: a aplicação é INVISÍVEL (opacidade 0 durante o toggle, desde 06/09),
        /// então instalação limpa já roda sozinho sem o usuário perceber; pode-se desligar
        /// manualmente em TweaksPage / GameBoostPro.
        /// </summary>
        public bool ExplorerAutoTurbo { get; set; } = true;

        // RAM Limiter - Variáveis e configurações
        private DispatcherTimer? _ramLimiterTimer;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProcessRamLimit> _processRamLimits = new();
        private int _ramLimiterIntervalMs = 1000; // Intervalo em milissegundos
        private readonly Dictionary<string, IntPtr> _cpuJobObjects = new();
        // Amostra anterior de CPU por "nome:pid" para o governador (idle = cpu baixa).
        // Fora da classe serializada em JSON (é estado volátil, não configuração).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long ticks, DateTime when)> _govCpuPrev = new();
        

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProcessInfo> _processCache = new();
        private DispatcherTimer? _advancedMonitorTimer;
        private int _advancedMonitorIntervalMs = 2000; // 2 segundos para monitor avançado
        private long _totalSystemRamMB = 0;
        private long _availableRamMB = 0;
        private double _currentCpuUsage = 0;
        private int _activeProcessCount = 0;
        private DateTime _lastSystemStatsUpdate = DateTime.MinValue;
        // Contador de CPU do sistema REUSADO entre ticks: criar um PerformanceCounter
        // novo a cada tick custa init PDH (registry + instancias) e o primeiro
        // NextValue() de um contador novo sempre retorna 0 (sem baseline).
        private System.Diagnostics.PerformanceCounter? _cpuTotalCounter;
        // Tick do cache de processos: Responding (SendMessageTimeout por processo GUI)
        // e o syscall mais caro da coleta — amostrado a cada 5 ticks (~10s).
        private int _processCacheTick = 0;
        

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProcessAlert> _processAlerts = new();
        private bool _enableSmartAlerts = true;
        private long _highRamThresholdMB = 2048; // 2GB para alerta
        private double _highCpuThresholdPercent = 80.0; // 80% CPU para alerta
        

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProcessBehavior> _processBehaviors = new();
        private bool _enableBehaviorAnalysis = true;
        

        private int _consecutiveErrors = 0;
        private DateTime _lastErrorTime = DateTime.MinValue;
        private readonly TimeSpan _errorCooldown = TimeSpan.FromMinutes(5);
        private readonly int _maxConsecutiveErrors = 3;
        private bool _isInSafeMode = false;
        private readonly object _robustnessLock = new object();
        

        private static readonly string _backupPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KitLugia", "process_ram_limits_backup.json");
        private DateTime _lastBackupTime = DateTime.MinValue;
        private readonly TimeSpan _backupInterval = TimeSpan.FromHours(1);
        public int RamLimiterIntervalMs
        {
            get => _ramLimiterIntervalMs;
            set
            {
                _ramLimiterIntervalMs = Math.Max(500, value); // Mínimo 500ms
                if (_ramLimiterTimer != null)
                {
                    _ramLimiterTimer.Interval = TimeSpan.FromMilliseconds(_ramLimiterIntervalMs);
                }
            }
        }

        // Teto de RAM do proprio Kit (MB): o AggressiveMemoryCleaner limpa quando
        // o GC heap passa disso. Persistido em HKCU TraySettings\KitMemoryLimitMB.
        private long _kitMemoryLimitMB = 200;
        public long KitMemoryLimitMB
        {
            get => _kitMemoryLimitMB;
            set
            {
                _kitMemoryLimitMB = Math.Min(1024, Math.Max(80, value)); // 80..1024 MB
                try { AggressiveMemoryCleaner.SetMemoryLimit(_kitMemoryLimitMB); }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
        }

        public static long GetKitMemoryLimitStatic()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\KitLugia\TraySettings");
                if (key == null) return 200;
                long v = ReadLongSetting(key, "KitMemoryLimitMB", 200);
                return Math.Min(1024, Math.Max(80, v));
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return 200; }
        }
        

        public int AdvancedMonitorIntervalMs
        {
            get => _advancedMonitorIntervalMs;
            set
            {
                _advancedMonitorIntervalMs = Math.Max(1000, value); // Mínimo 1s
                if (_advancedMonitorTimer != null)
                {
                    _advancedMonitorTimer.Interval = TimeSpan.FromMilliseconds(_advancedMonitorIntervalMs);
                }
            }
        }
        
        public bool EnableSmartAlerts
        {
            get => _enableSmartAlerts;
            set => _enableSmartAlerts = value;
        }
        
        public bool EnableBehaviorAnalysis
        {
            get => _enableBehaviorAnalysis;
            set => _enableBehaviorAnalysis = value;
        }
        
        public long HighRamThresholdMB
        {
            get => _highRamThresholdMB;
            set => _highRamThresholdMB = Math.Max(512, value); // Mínimo 512MB
        }
        
        public double HighCpuThresholdPercent
        {
            get => _highCpuThresholdPercent;
            set => _highCpuThresholdPercent = Math.Max(10.0, Math.Min(100.0, value)); // 10-100%
        }
        

        public long TotalSystemRamMB => _totalSystemRamMB;
        public long AvailableRamMB => _availableRamMB;
        public double CurrentCpuUsage => _currentCpuUsage;
        public int ActiveProcessCount => _activeProcessCount;
        public double RamUsagePercent => _totalSystemRamMB > 0 ? ((_totalSystemRamMB - _availableRamMB) * 100.0 / _totalSystemRamMB) : 0;
        private class ProcessProfile
        {
            public string Name { get; set; } = "";
            public int TotalCyclesVisible { get; set; } = 0;
            public int CyclesForeground { get; set; } = 0;
            public bool IsVip { get; set; } = false;
            public DateTime LastTrimTime { get; set; } = DateTime.MinValue;
            public long LastKnownWs { get; set; } = 0;
            public long LastSeenTick { get; set; } = 0;
        }


        // Típico: 20-50 perfis de processos em cache
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProcessProfile> _processProfiles = new(concurrencyLevel: 4, capacity: 50, comparer: StringComparer.OrdinalIgnoreCase);


        private readonly object _processCacheLock = new();


        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (DateTime Timestamp, TimeSpan CpuTime)> _cpuTimeCache = new();

        // Settings
        public bool AutoCleanEnabled { get; set; } = false;
        public int AutoCleanThresholdPercent { get; set; } = 80;
        private int _monitorIntervalSeconds = 60;
        public int MonitorIntervalSeconds
        {
            get => _monitorIntervalSeconds;
            set
            {

                _monitorIntervalSeconds = Math.Max(5, value);
                if (_monitorTimer != null)
                {
                    _monitorTimer.Interval = TimeSpan.FromSeconds(_monitorIntervalSeconds);
                }
            }
        }
        public MemoryOptimizer.CleaningMode SelectedCleaningMode { get; set; } = MemoryOptimizer.CleaningMode.Normal;

        // Background Features
        public bool GamePriorityEnabled { get; set; } = false;

        /// <summary>
        /// ANTI-STUTTER DE ÁUDIO (recuperação automática do motor de áudio).
        ///
        /// O Kit abre uma captura em LOOPBACK do dispositivo de saída padrão e pergunta ao
        /// Windows, pacote por pacote, se ele precisou pular quadros (DATA_DISCONTINUITY —
        /// a API oficial de detecção de glitch). Quando DOIS estalos CONFIRMADOS acontecem
        /// dentro de 90 s, o Kit congela o audiodg por ~300 ms e retoma no MESMO handle —
        /// o mesmo "desligar/ligar" que o usuário já fazia na mão para destravar o som.
        ///
        /// Fica nas configurações do Kit (engrenagem) porque é serviço de segundo plano:
        /// precisa valer ANTES de o problema aparecer, sem abrir o Gerenciador de Tarefas.
        /// Estado real = o do monitor (fonte única), lido/exposto para a UI.
        /// </summary>
        public bool AudioAntiStutterEnabled
        {
            get
            {
                try { return KitLugia.Core.TaskManager.AudioGlitchMonitor.Instance.AutoRecover; }
                catch { return false; }
            }
        }
        public bool ForegroundBoostEnabled { get; set; } = true;
        public bool StandbyCleanEnabled { get; set; } = false;
        public long IslcThresholdMB { get; set; } = SuggestIslcThresholdMB(); // ISLC: auto-calculado baseado na RAM do sistema
        public long IslcLastCleanMB { get; set; } = 0; // Standby List no ultimo clean
        public DateTime IslcLastCleanTime { get; set; } = DateTime.MinValue;
        public int IslcCleanCount { get; set; } = 0; // quantas vezes limpos
        public bool MemoryLeakDetectionEnabled { get; set; } = false;
        public bool DpcMonitorEnabled { get; set; } = false;
        public bool FocusAssistEnabled { get; set; } = false;
        public bool TimerBoost { get; set; } = false;
        public bool NetworkBoost { get; set; } = false;
        public bool DownloadBoostEnabled { get; set; } = false;
        public string DownloadBoostLevel { get; set; } = "Auto";
        public double DownloadBoostThreshold { get; set; } = 5.0;
        public bool ProBalance { get; set; } = false;
        public bool UnparkCpuEnabled { get; set; } = false;
        public bool TurboBootEnabled
        {
            get => SystemTweaks.IsTurboBootEnabled();
            set => SystemTweaks.ToggleTurboBoot(value);
        }
        public bool TurboShutdownEnabled
        {
            get => SystemTweaks.IsFastShutdownEnabled();
            set => SystemTweaks.ToggleFastShutdown();
        }

        // Tray active state
        // Ícone do tray é SEMPRE visível enquanto o Kit roda — a opção de ocultar foi removida.
        public bool IsTrayEnabled { get; set; } = true;
        
        // Close to Tray (minimizar ao invés de fechar)
        public bool CloseToTray { get; set; } = true;

        public static bool IsTrayEnabledStatic()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\KitLugia\TraySettings");
                // Chave INEXISTENTE = primeira execução do Kit (usuário novo). O LoadSettings
                // força IsTrayEnabled = true nesse caso (ícone do tray é SEMPRE visível), mas
                // este método lia a chave ANTES do SaveSettings existir e devolvia false —
                // o MainWindow pulava o SetAutoStart e o Kit NUNCA nascia com o Windows.
                // Agora devolve true, coerente com o LoadSettings.
                if (key == null) return true;
                return (int)(key.GetValue("IsTrayEnabled", 1) ?? 1) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return true; }
        }

        /// <summary>
        /// Verifica se o auto-start está habilitado em QUALQUER um dos 3 métodos:
        /// HKCU Run (universal), pasta Startup (.lnk no AppData) ou Task Scheduler.
        /// Retorna true se qualquer um apontar para o executável atual.
        /// </summary>
        public static bool IsAutoStartEnabled()
        {
            try
            {
                string currentPath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(currentPath)) return false;

                // 1. HKCU Run — método universal, funciona com e sem admin
                if (CheckRegistryAutoStart(currentPath)) return true;

                // 2. Pasta Startup (.lnk no %APPDATA%\...\Startup) — sem depender de serviço
                if (CheckStartupFolderAutoStart(currentPath)) return true;

                // 3. Task Scheduler (best-effort; pode não disparar em conta sem admin)
                try
                {
                    using (var ts = new TaskService())
                    {
                        var task = ts.GetTask("KitLugia");
                        if (task != null && task.Enabled)
                        {
                            foreach (var action in task.Definition.Actions)
                            {
                                if (action is ExecAction execAction)
                                {
                                    string taskPath = execAction.Path;
                                    if (string.Equals(taskPath, currentPath, StringComparison.OrdinalIgnoreCase))
                                        return true;
                                    else
                                        KitLugia.Core.Logger.Log($"⚠️ Auto-Start aponta para versão antiga: {taskPath} != {currentPath}");
                                }
                            }
                        }
                    }
                }
                catch { /* Task Scheduler indisponível — ignorar */ }

                // Preferência persistida: usuário ativou uma vez e as entradas foram
                // apagadas depois (cleaner, uninstaller, reimagem) — a intenção continua viva
                if (GetStartWithWindowsPref()) return true;

                return false;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("IsAutoStartEnabled", $"Erro: {ex.Message}");
                return false;
            }
        }

        private static bool CheckStartupFolderAutoStart(string currentPath)
        {
            try
            {
                string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                string lnk = System.IO.Path.Combine(startup, "KitLugia.lnk");
                if (!File.Exists(lnk)) return false;

                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                dynamic shortcut = shell.CreateShortcut(lnk);
                string target = shortcut.TargetPath?.ToString() ?? "";
                return string.Equals(target, currentPath, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool CheckRegistryAutoStart(string? currentPath = null)
        {
            try
            {
                if (string.IsNullOrEmpty(currentPath))
                {
                    currentPath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    if (string.IsNullOrEmpty(currentPath)) return false;
                }

                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if (key == null) return false;

                var value = key.GetValue("KitLugia") as string;
                if (string.IsNullOrEmpty(value)) return false;

                value = value.Trim();

                // Extrair o caminho entre aspas: "c:\path\exe" --tray
                string registryPath;
                if (value.StartsWith("\"") && value.Length > 1)
                {
                    int closingQuote = value.IndexOf('"', 1);
                    if (closingQuote > 0)
                        registryPath = value.Substring(1, closingQuote - 1);
                    else
                        registryPath = value;
                }
                else
                {
                    registryPath = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
                }

                return string.Equals(registryPath, currentPath, StringComparison.OrdinalIgnoreCase);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        /// <summary>
        /// Remove tarefa antiga se o caminho não corresponder à versão atual
        /// </summary>
        private static void CleanupOldTask()
        {
            try
            {
                string currentPath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(currentPath)) return;

                using (var ts = new TaskService())
                {
                    var task = ts.GetTask("KitLugia");
                    if (task == null) return;

                    // Verificar se o caminho corresponde
                    bool pathMatches = false;
                    foreach (var action in task.Definition.Actions)
                    {
                        if (action is ExecAction execAction)
                        {
                            if (string.Equals(execAction.Path, currentPath, StringComparison.OrdinalIgnoreCase))
                            {
                                pathMatches = true;
                                break;
                            }
                        }
                    }

                    if (!pathMatches)
                    {
                        KitLugia.Core.Logger.Log("🧹 Removendo tarefa antiga com caminho incorreto...");
                        ts.RootFolder.DeleteTask("KitLugia");
                    }
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("CleanupOldTask", $"Erro: {ex.Message}");
            }
        }

        /// <summary>
        /// Ativa/desativa o auto-start (Registry Run + Task Scheduler).
        /// O Gerenciador de Tarefas do Windows lista o Registry Run e a pasta Startup como
        /// DUAS linhas separadas — manter os dois criava a "duplicata" do Kit na aba
        /// Inicializacao (mesmo exe, duas entradas). Por isso o .lnk da Startup agora e
        /// so FALLBACK: se o Registry gravou OK, o .lnk legado e REMOVIDO; so e criado
        /// quando o Registry falha. Restam sempre 2 vias vivas (Registry+Task elevado, ou
        /// .lnk+Task), nunca 3 linhas. O mutex de instancia unica continua evitando
        /// processo duplicado no boot. A preferencia e persistida em
        /// HKCU\Software\KitLugia\TraySettings\StartWithWindows.
        /// </summary>
        public static void SetAutoStart(bool enable)
        {
            try
            {
                string path = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(path)) return;

                if (!enable)
                {
                    // ===== DESLIGAR: remove os 3 métodos — nunca deixa lixo =====
                    SetStartWithWindowsPref(false);
                    try
                    {
                        using var ts = new TaskService();
                        var t = ts.GetTask("KitLugia");
                        if (t != null)
                        {
                            ts.RootFolder.DeleteTask("KitLugia");
                            KitLugia.Core.Logger.Log("✅ Tarefa agendada removida");
                        }
                    }
                    catch { /* Task Scheduler indisponível — registry/lnk abaixo resolvem */ }
                    SetRegistryEntry(false, path);
                    SetStartupShortcut(false, path);
                    KitLugia.Core.Logger.Log("✅ Auto-start desativado (Registry / Startup / Task limpos)");
                    return;
                }

                SetStartWithWindowsPref(true);
                CleanupOldTask();

                // ===== MÉTODO 1 (universal): HKCU Run — funciona com ou sem admin =====
                bool regOk = SetRegistryEntry(true, path);
                KitLugia.Core.Logger.Log(regOk
                    ? $"✅ Auto-start via Registry Run: {path} --tray"
                    : "⚠️ Registry Run falhou");

                // ===== MÉTODO 2: .lnk na pasta Startup — SO como fallback =====
                // Registry OK = remove o .lnk (senao o Gerenciador do Windows mostra o Kit
                // DUAS vezes na aba Inicializacao). Registry falhou = cria o .lnk para o
                // Kit continuar subindo no boot por outra via.
                bool lnkOk;
                if (regOk)
                {
                    lnkOk = SetStartupShortcut(false, path);
                    KitLugia.Core.Logger.Log("ℹ️ Atalho da pasta Startup removido (Registry Run ativo — sem entrada duplicada)");
                }
                else
                {
                    lnkOk = SetStartupShortcut(true, path);
                    KitLugia.Core.Logger.Log(lnkOk
                        ? $"✅ Auto-start via pasta Startup (fallback): \"{path}\" --tray"
                        : "⚠️ Falha ao criar atalho na pasta Startup");
                }

                // ===== MÉTODO 3 (elevado, best-effort): Task Scheduler =====
                // Só quando o app roda como admin: registro com RunLevel.Highest sem
                // privilégios "registra" a tarefa mas ela NUNCA dispara (falha silenciosa).
                if (IsRunningElevated())
                {
                    try
                    {
                        using (var ts = new TaskService())
                        {
                            var existingTask = ts.GetTask("KitLugia");

                            // Tarefa existente com caminho correto → apenas habilitar e usar ela
                            if (existingTask != null)
                            {
                                bool pathMatches = false;
                                foreach (var action in existingTask.Definition.Actions)
                                {
                                    if (action is ExecAction execAction &&
                                        string.Equals(execAction.Path, path, StringComparison.OrdinalIgnoreCase))
                                    {
                                        pathMatches = true;
                                        break;
                                    }
                                }

                                // Só reutiliza se a task antiga já tiver os 2 triggers (Boot + Logon).
                                // Task criada ANTES da sessao 08/08 (so LogonTrigger) nao tem o
                                // "inicia junto do boot" — nesse caso cai fora e e recriada abaixo.
                                if (pathMatches && TaskHasBootTrigger(existingTask))
                                {
                                    existingTask.Enabled = true;
                                    existingTask.RegisterChanges();
                                    KitLugia.Core.Logger.Log("✅ Tarefa agendada existente habilitada (Boot+Logon)");
                                    return;
                                }
                                KitLugia.Core.Logger.Log((pathMatches
                                    ? "🔄 Tarefa existe sem BootTrigger, recriando com boot+logon..."
                                    : "🔄 Tarefa existe com caminho incorreto, recriando..."));
                                ts.RootFolder.DeleteTask("KitLugia");
                            }

                            // Criar nova tarefa com privilégios de logon + prioridade de boot
                            var td = ts.NewTask();
                            td.RegistrationInfo.Description = "KitLugia Auto-Startup (Admin Mode)";
                            td.Principal.RunLevel = TaskRunLevel.Highest;
                            td.Settings.DisallowStartIfOnBatteries = false;
                            td.Settings.StopIfGoingOnBatteries = false;
                            td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
                            td.Settings.StartWhenAvailable = true;
                            td.Settings.AllowHardTerminate = false;

                            // ★ OTIMIZAÇÃO TIPO WALLPAPER ENGINE: Priority High (1) = HIGH_PRIORITY_CLASS
                            // Sempre High — requisito do usuário: kit inicia "junto com o sistema" sem fome de CPU
                            // (padrão do Task Scheduler é 7 = BELOW_NORMAL, que atrasa o boot do app)
                            td.Settings.Priority = ProcessPriorityClass.High;

                            td.Settings.RestartCount = 2;
                            td.Settings.RestartInterval = TimeSpan.FromMinutes(1);

                            var trigger = new LogonTrigger { Delay = TimeSpan.Zero, Enabled = true };
                            td.Triggers.Add(trigger);

                            // BootTrigger: inicia junto do boot do Windows (nao so no logon).
                            // Com InteractiveToken o agendador dispara a task o mais cedo
                            // possivel (com Fast Startup / auto-login, antes da sessao pronta).
                            // MultipleInstances = IgnoreNew evita processo duplo caso Logon
                            // e Boot disparem na mea sequencia.
                            td.Triggers.Add(new BootTrigger { Delay = TimeSpan.Zero, Enabled = true });
                            td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;

                            td.Actions.Add(new ExecAction(path, "--tray", Path.GetDirectoryName(path)));

                            ts.RootFolder.RegisterTaskDefinition("KitLugia", td);
                            KitLugia.Core.Logger.Log($"✅ Tarefa agendada admin criada: \"{path}\" --tray (Boot+Logon, Priority: High)");
                        }
                    }
                    catch (Exception taskEx)
                    {
                        // Registry + .lnk já garantiram o auto-start — apenas registra
                        KitLugia.Core.Logger.Log($"⚠️ Task Scheduler indisponível ({taskEx.Message}) — usando Registry/Startup já criado");
                    }
                }

                // Diagnóstico: imprime o comando REAL de cada uma das 3 vias. Sem isso o
                // log só mostrava o caminho do .lnk/sem --tray e parecia que as vias divergiam.
                LogAutoStartDiagnostics();
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"SetAutoStart ERROR: {ex.Message}");
            }
        }

        /// <summary>Persiste a preferência de auto-start (sobrevive a limpezas que apaguem as entradas).</summary>
        private static void SetStartWithWindowsPref(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\KitLugia\TraySettings");
                key?.SetValue("StartWithWindows", enabled ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { /* não crítico */ }
        }

        /// <summary>
        /// O valor "StartWithWindows" JÁ foi gravado alguma vez? Distingue "primeira execução"
        /// (usuário nunca escolheu) de "usuário desativou de propósito" (valor 0) — sem isso,
        /// um desativado voltaria a ter auto-start no próximo start.
        /// </summary>
        public static bool HasStartWithWindowsPref()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\KitLugia\TraySettings");
                return key?.GetValue("StartWithWindows") != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// Serializa as escritas de auto-start (registro + .lnk + Task Scheduler).
        /// App.OnStartup e MainWindow disparam EnsureAutoStartMethods em paralelo —
        /// sem esta trava as duas rotinas brigavam pelo mesmo destino.
        /// </summary>
        private static readonly object _autoStartLock = new();

        /// <summary>Lê a preferência persistida de auto-start.</summary>
        public static bool GetStartWithWindowsPref()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\KitLugia\TraySettings");
                var raw = key?.GetValue("StartWithWindows", 0);
                return raw is int i ? i == 1 : Convert.ToInt32(raw ?? 0) == 1;
            }
            catch { return false; }
        }

        /// <summary>
        /// Garante o auto-start apontando para o executavel ATUAL. Roda em todo start do kit:
        /// se o usuario ja ativou uma vez (preferencia persistida ou qualquer entrada viva),
        /// reconfigura (Registry Run + Task; o .lnk da Startup e removido quando o Registry
        /// esta ativo para nao duplicar a entrada no Gerenciador do Windows) — mesmo se
        /// AppData/settings ainda nao existirem ou se alguma limpeza apagou as entradas.
        /// </summary>
        public static void EnsureAutoStartMethods()
        {
            // CORRIDA (04/10/2026): App.OnStartup e MainWindow chamam este método em
            // paralelo no mesmo start. As duas rotinas escrevem no MESMO registro
            // (HKCU\...\Run), no MESMO .lnk e na MESMA Task Scheduler — o
            // RegisterTaskDefinition de uma podia colidir com o DeleteTask da outra e
            // deixava a entrada faltando (o app não subia no boot seguinte).
            // O lock serializa as duas e o segundo call só reconfirma o estado.
            lock (_autoStartLock)
            {
                try
                {
                    bool desired = IsAutoStartEnabled(); // método vivo OU preferência persistida

                // PRIMEIRA EXECUÇÃO (usuário novo): a chave de preferências ainda NÃO tem o
                    // valor StartWithWindows (o SaveSettings do tray não grava esse valor) e não
                    // existe nenhuma entrada de auto-start. Registrar o auto-start é o
                    // comportamento esperado do produto (o app é um monitor de tray/otimização
                    // que só faz sentido rodando em background). Antes disso o Ensure retornava
                    // aqui e o primeiro start do Kit ficava SEM auto-start para sempre.
                    // Usuário que DESLIGOU de propósito tem o valor gravado (0) -> respeitado.
                    bool firstRun = !desired && !HasStartWithWindowsPref() && !HasAnyAutoStartEntry();
                    if (firstRun)
                    {
                        KitLugia.Core.Logger.Log("🆕 Primeira execução detectada — registrando auto-start inicial (Registry Run + Task).");
                        SetAutoStart(true);
                        return;
                    }

                    // Auto-start explicitamente desativado pelo usuário -> não ressuscita.
                    if (!desired)
                    {
                        if (HasStartWithWindowsPref() && !GetStartWithWindowsPref())
                            KitLugia.Core.Logger.Log("ℹ️ Auto-start desativado pelo usuário — respeitando a preferência.");
                        return;
                    }
                    KitLugia.Core.Logger.Log("🔁 Reconfigurando auto-start em todos os métodos com o executável atual...");
                    SetAutoStart(true);
                }
                catch (Exception ex)
                {
                    KitLugia.Core.Logger.LogError("EnsureAutoStartMethods", $"Erro: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Existe ALGUMA entrada de auto-start viva (sem comparar o caminho)? Usado para
        /// distinguir "primeira execução" de "usuário desativou de propósito" — só a
        /// ausência TOTAL de entradas + ausência de preferência indica primeira execução.
        /// </summary>
        private static bool HasAnyAutoStartEntry()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (key?.GetValue("KitLugia") is string v && !string.IsNullOrWhiteSpace(v))
                        return true;
                }

                string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                if (!string.IsNullOrEmpty(startup) &&
                    File.Exists(Path.Combine(startup, "KitLugia.lnk")))
                    return true;

                try
                {
                    using var ts = new TaskService();
                    // Só conta como entrada VIVA se estiver HABILITADA — uma task
                    // desabilitada (cleaner, "Desativar" do Agendador, política) não inicia
                    // o Kit, então não pode impedir a remontagem do auto-start.
                    var t = ts.GetTask("KitLugia");
                    if (t != null && t.Enabled) return true;
                }
                catch { /* Task Scheduler indisponível — as outras 2 vias bastam */ }

                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Diagnóstico do auto-start: devolve uma linha por via (Registry Run / Startup .lnk /
        /// Task) com o comando REAL gravado. O log antigo da pasta Startup omitia o "--tray",
        /// o que fazia parecer (no log) que as vias divergiam — na verdade só o log era
        /// incompleto. Este helper grava a linha completa e verificável.
        /// </summary>
        public static void LogAutoStartDiagnostics()
        {
            try
            {
                string currentPath = Process.GetCurrentProcess().MainModule?.FileName ?? "";

                string regValue = "";
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                    regValue = key?.GetValue("KitLugia") as string ?? "";

                string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                string lnkPath = string.IsNullOrEmpty(startup) ? "" : Path.Combine(startup, "KitLugia.lnk");

                KitLugia.Core.Logger.Log($"🔎 [AUTOSTART] Registry Run  : {(string.IsNullOrEmpty(regValue) ? "(ausente)" : regValue)}");
                KitLugia.Core.Logger.Log($"🔎 [AUTOSTART] Pasta Startup: {(string.IsNullOrEmpty(lnkPath) ? "(sem pasta)" : File.Exists(lnkPath) ? lnkPath : $"(ausente) {lnkPath}")}");

                try
                {
                    using var ts = new TaskService();
                    var task = ts.GetTask("KitLugia");
                    if (task == null)
                    {
                        KitLugia.Core.Logger.Log("🔎 [AUTOSTART] Task Scheduler: (ausente)");
                    }
                    else
                    {
                        var exec = task.Definition.Actions.OfType<ExecAction>().FirstOrDefault();
                        KitLugia.Core.Logger.Log(
                            $"🔎 [AUTOSTART] Task Scheduler: {(task.Enabled ? "habilitada" : "DESABILITADA")} — " +
                            $"{(exec == null ? "(sem ação)" : $"\"{exec.Path}\" {exec.Arguments}")} " +
                            $"| Triggers: {string.Join("+", task.Definition.Triggers.Select(t => t switch
                            {
                                BootTrigger => "Boot",
                                LogonTrigger => "Logon",
                                _ => t.GetType().Name
                            }))}");
                    }
                }
                catch (Exception taskEx)
                {
                    KitLugia.Core.Logger.Log($"🔎 [AUTOSTART] Task Scheduler: indisponível ({taskEx.Message})");
                }

                KitLugia.Core.Logger.Log($"🔎 [AUTOSTART] Preferência  : {(GetStartWithWindowsPref() ? "ativada" : "desativada/ainda não definida")}");
                KitLugia.Core.Logger.Log($"🔎 [AUTOSTART] Executável   : {currentPath}");
                KitLugia.Core.Logger.Log($"🔎 [AUTOSTART] Situação     : {(IsAutoStartEnabled() ? "ATIVO ✅" : "INATIVO ❌")}");
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("LogAutoStartDiagnostics", $"Erro: {ex.Message}");
            }
        }

        /// <summary>Grava/remove a entrada HKCU Run (método universal, sem admin).</summary>
        private static bool SetRegistryEntry(bool enable, string path)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)
                                ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if (key == null) return false;
                if (enable)
                    key.SetValue("KitLugia", $"\"{path}\" --tray");
                else
                    key.DeleteValue("KitLugia", false);
                return true;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogWarning("SetRegistryEntry", ex.Message);
                return false;
            }
        }

        /// <summary>Cria/remove o atalho na pasta Startup (fica no AppData, não depende de serviço).</summary>
        private static bool SetStartupShortcut(bool enable, string path)
        {
            try
            {
                string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                string lnk = Path.Combine(startup, "KitLugia.lnk");
                if (enable)
                {
                    if (!Directory.Exists(startup)) Directory.CreateDirectory(startup);
                    return KitLugia.Core.StartupManager.CreateShortcut(lnk, path, "--tray", "KitLugia Auto-Startup", Path.GetDirectoryName(path) ?? "");
                }
                if (File.Exists(lnk)) File.Delete(lnk);
                return true;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogWarning("SetStartupShortcut", ex.Message);
                return false;
            }
        }

        private static bool IsRunningElevated()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static bool TaskHasBootTrigger(Microsoft.Win32.TaskScheduler.Task task)
        {
            try
            {
                foreach (var trigger in task.Definition.Triggers)
                {
                    if (trigger is Microsoft.Win32.TaskScheduler.BootTrigger)
                        return true;
                }
            }
            catch { }
            return false;
        }

        // Adaptive Data
        private readonly string[] _vipProcesses = { "opera", "discord", "taskmgr", "devenv", "kitlugia", "steam", "riotclient" };
        private long _stutterBackoffCycles = 0;
        private long _lastCleanDurationMs = 0;

        // LocalApplicationData não depende de Roaming e é mais seguro
        private string _logPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KitLugia", "ram_stats.csv");

        private bool _seDebugEnabled;

        private void EnsureSeDebugPrivilege()
        {
            if (_seDebugEnabled) return;
            _seDebugEnabled = true;
            EnableSeDebugPrivilege();
        }

        public event System.Action? OnOpenMainWindow;
        public event System.Action? OnOpenSettings;

        public TrayIconService()
        {

            _instance = this;

            _monitorTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(MonitorIntervalSeconds)
            };
            _monitorTimer.Tick += MonitorTick;
        }


        private void EnableSeDebugPrivilege()
        {
            try
            {
                IntPtr hToken;
                if (!Win32Api.OpenProcessToken(Process.GetCurrentProcess().Handle, Win32Api.TOKEN_ADJUST_PRIVILEGES | Win32Api.TOKEN_QUERY, out hToken))
                {
                    KitLugia.Core.Logger.Log("⚠️ Falha ao abrir token do processo");
                    return;
                }

                try
                {
                    long luid;
                    if (!Win32Api.LookupPrivilegeValue(string.Empty, "SeDebugPrivilege", out luid))
                    {
                        KitLugia.Core.Logger.Log("⚠️ Falha ao obter LUID do SeDebugPrivilege");
                        return;
                    }

                    var tp = new Win32Api.TOKEN_PRIVILEGES
                    {
                        PrivilegeCount = 1,
                        Luid = luid,
                        Attributes = Win32Api.SE_PRIVILEGE_ENABLED
                    };

                    if (!Win32Api.AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                    {
                        KitLugia.Core.Logger.Log("⚠️ Falha ao ajustar privilégio SeDebugPrivilege");
                    }
                    else
                    {
                        KitLugia.Core.Logger.Log("✅ SeDebugPrivilege habilitado com sucesso");
                    }
                }
                finally
                {
                    Win32Api.CloseHandle(hToken);
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ Erro ao habilitar SeDebugPrivilege: {ex.Message}");
            }
        }


        private Process[] GetCachedProcesses()
        {
            return Process.GetProcesses();
        }


        private void ClearProcessCache()
        {
        }

        public void Initialize()
        {
            // GUARDA DE IDEMPOTÊNCIA: Initialize() só pode produzir UM NotifyIcon, UM
            // watchdog e UM _monitorTimer. Sem esta trava, um segundo start da página
            // (ou a recuperação de health check) criava um ícone fantasma na bandeja
            // e DOIS timers de monitor disputando o mesmo ciclo de CPU.
            if (IsInitialized)
            {
                KitLugia.Core.Logger.Log("🔁 TrayIconService já inicializado — ignorando segunda chamada.");
                return;
            }

            // Resgate de tweak global órfão (Kit morto com o boost de rede aplicado).
            System.Threading.Tasks.Task.Run(() => RescueNetworkBoostOnStartup());

            // Resgate de processos que ficaram com prioridade alterada por um crash
            // do GameBoost (High sem original, ou BelowNormal do ProBalance).
            System.Threading.Tasks.Task.Run(() => RescueOrphanedBoostState());

            // LoadSettings já APLICA o anti-stutter de áudio salvo (o setter liga/desliga
            // o monitor e a recuperação automática) — nada a fazer depois daqui.
            LoadSettings();

            System.Threading.Tasks.Task.Run(() => AutoFixGameBarPresenceWriter());
            System.Threading.Tasks.Task.Run(() => AutoFixCommunityProcesses());
            System.Threading.Tasks.Task.Run(() => AutoFixForceStopContextMenu());

            // Watchdog do Turbo Explorer: quando o explorer.exe (re)aparece (login, crash,
            // restart do shell), re-aplica o F11 re-render na 1ª janela de pasta que abrir.
            StartExplorerWatchdog();

            try
            {
                _trayIcon = new NotifyIcon
                {
                    Text = "KitLugia RAM Monitor",
                    Visible = false
                };
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"ERRO ao criar NotifyIcon: {ex.Message}");
                return;
            }

            // ★ Show tray icon IMMEDIATELY before building menus — ícone é SEMPRE visível
            if (_trayIcon != null)
            {
                try
                {
                    UpdateTrayIcon(GetMemoryUsagePercent());
                    _trayIcon.Visible = true;
                    _monitorTimer.Start();

                    // ★ LANÇAR OS APPS DO TURBO BOOT EM BACKGROUND
                    // Depois do tray visível — não bloqueia WPF. Conc Bulk: cada app dispara em thread própria.
                    // Isto garante que o KitLugia apareça PRIMEIRO no tray, e depois orquestre o lançamento.
                    // (Antes isto rodava sincrono no Program.cs e travava o startup do próprio KitLugia.)
                    if (Environment.CommandLine.Contains("--tray") || Environment.CommandLine.Contains("--minimized"))
                    {
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                System.Threading.Thread.Sleep(50); // Dá 50ms pro tray renderizar
                                StartupManager.LaunchTurboApps();
                            }
                            catch (Exception ex) { KitLugia.Core.Logger.LogError("LaunchTurboApps bg", ex.Message); }
                        });
                    }
                }
                catch (Exception ex)
                {
                    KitLugia.Core.Logger.Log($"❌ ERRO ao ativar Tray Icon: {ex.Message}");
                }
            }

            // Context Menu — build after icon is visible
            var menu = new ContextMenuStrip();

            var itemClean = new ToolStripMenuItem("🧹 Limpar RAM Agora");
            itemClean.Click += (s, e) => CleanRamNow();
            menu.Items.Add(itemClean);

            menu.Items.Add(new ToolStripSeparator());

            var itemAutoClean = new ToolStripMenuItem($"⚡ Auto-Limpeza ({AutoCleanThresholdPercent}%)");
            itemAutoClean.Checked = AutoCleanEnabled;
            itemAutoClean.Click += (s, e) =>
            {
                AutoCleanEnabled = !AutoCleanEnabled;
                itemAutoClean.Checked = AutoCleanEnabled;
                SaveSettings();
            };
            menu.Items.Add(itemAutoClean);

            menu.Items.Add(new ToolStripSeparator());


            var itemGameBoost = new ToolStripMenuItem("🚀 GameBoost Pro");
            itemGameBoost.Checked = GamePriorityEnabled;
            itemGameBoost.Click += (s, e) =>
            {
                GamePriorityEnabled = !GamePriorityEnabled;
                itemGameBoost.Checked = GamePriorityEnabled;
                if (GamePriorityEnabled) EnsureSeDebugPrivilege();
                SaveSettings();
                KitLugia.Core.Logger.Log($"🚀 GameBoost {(GamePriorityEnabled ? "ativado" : "desativado")} via Tray Icon");
            };
            menu.Items.Add(itemGameBoost);

            menu.Items.Add(new ToolStripSeparator());

            var itemTurboExplorer = new ToolStripMenuItem("⚡ Turbo Explorer (F11)");
            itemTurboExplorer.ToolTipText = "Envia F11 2x para a janela do Explorer mais recente — força re-render e acelera o 'ir direto ao arquivo'.";
            itemTurboExplorer.Click += (s, e) => System.Threading.Tasks.Task.Run(() => TurboExplorerF11());
            menu.Items.Add(itemTurboExplorer);

            // Boot Tray
            int bootAppCount = 0;
            try
            {
                using var bootKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\KitLugia\StartupApps");
                if (bootKey != null) bootAppCount = bootKey.ValueCount;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            string bootCountStr = bootAppCount > 0 ? $"{bootAppCount} apps" : "vazio";
            var itemBootTrayAdmin = new ToolStripMenuItem($"🛡️ Boot Tray: Iniciar (Admin)");
            itemBootTrayAdmin.ToolTipText = $"Inicia os apps do Boot Tray com privilégios de Administrador ({bootCountStr})";
            itemBootTrayAdmin.Click += (s, e) =>
            {
                try { StartupManager.LaunchTurboApps(); }
                catch (Exception ex) { KitLugia.Core.Logger.LogError("BootTrayAdmin", ex.Message); }
            };
            menu.Items.Add(itemBootTrayAdmin);

            var itemBootTrayNormal = new ToolStripMenuItem($"👤 Boot Tray: Iniciar (Normal - Sem Admin)");
            itemBootTrayNormal.ToolTipText = $"Inicia os apps do Boot Tray como usuário normal via tarefa agendada ({bootCountStr})";
            itemBootTrayNormal.Click += (s, e) =>
            {
                try { StartupManager.LaunchTurboAppsNonAdmin(); }
                catch (Exception ex) { KitLugia.Core.Logger.LogError("BootTrayNormal", ex.Message); }
            };
            menu.Items.Add(itemBootTrayNormal);

            var itemBootTrayManager = new ToolStripMenuItem($"📋 Gerenciar Boot Tray ({bootCountStr})");
            itemBootTrayManager.Click += (s, e) => OnOpenSettings?.Invoke();
            menu.Items.Add(itemBootTrayManager);

            menu.Items.Add(new ToolStripSeparator());

            var itemSettings = new ToolStripMenuItem("⚙ Configurações");
            itemSettings.Click += (s, e) => OnOpenSettings?.Invoke();
            menu.Items.Add(itemSettings);

            var itemOpen = new ToolStripMenuItem("🚀 Abrir KitLugia");
            itemOpen.Font = new Font(itemOpen.Font, FontStyle.Bold);
            itemOpen.Click += (s, e) => OnOpenMainWindow?.Invoke();
            menu.Items.Add(itemOpen);

            menu.Items.Add(new ToolStripSeparator());

            var itemRestartAdmin = new ToolStripMenuItem("🛡️ Iniciar como Admin");
            itemRestartAdmin.Click += (s, e) =>
            {
                try
                {
                    string exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                    Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true, Verb = "runas" });
                    Dispose();
                    Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            };
            menu.Items.Add(itemRestartAdmin);

            var itemRestartNormal = new ToolStripMenuItem("👤 Iniciar como Usuário Normal");
            itemRestartNormal.Click += async (s, e) =>
            {
                try
                {
                    string exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                    KitLugia.Core.StartupManager.RegisterNonAdminTask("__KitLugiaRestart", exe, null);
                    KitLugia.Core.StartupManager.RunNonAdminTask("__KitLugiaRestart");
                    await System.Threading.Tasks.Task.Delay(1500);
                    Dispose();
                    if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.HasShutdownFinished)
                        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            };
            menu.Items.Add(itemRestartNormal);

            menu.Items.Add(new ToolStripSeparator());

            var itemExit = new ToolStripMenuItem("❌ Sair Completamente");
            itemExit.Click += (s, e) =>
            {
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ForceShutdown();
                else
                    Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
            };
            menu.Items.Add(itemExit);

            _trayIcon.ContextMenuStrip = menu;

            // Double-click to open main window
            _trayIcon.DoubleClick += (s, e) => OnOpenMainWindow?.Invoke();if (_trayIcon != null)
                {
                    if (_trayIcon.Visible)
                    {
                        // Defer heavy init tasks to threadpool so UI stays responsive
                        _ = System.Threading.Tasks.Task.Run(() =>
                        {
                            try { RunSafetyProfiler(); }
                            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                        });

                        // LoadProcessLimits() faz I/O de DISCO (carrega o arquivo de
                        // limites, backup e recovery). Rodava na thread de UI dentro do
                        // BeginInvoke abaixo — com o disco ocupado (boot, antivírus,
                        // OneDrive) isso travava a janela nos primeiros segundos.
                        // ShowTrayStatusReport() chama IsSystemHealthy() (só leitura de
                        // campos) e MonitorTick já despacha para o thread pool.
                        _ = System.Threading.Tasks.Task.Run(() =>
                        {
                            try { LoadProcessLimits(); }
                            catch (Exception ex) { KitLugia.Core.Logger.LogWarning("LoadProcessLimits(bg)", ex.Message); }
                        });

                        if (Application.Current?.Dispatcher == null || Application.Current.Dispatcher.HasShutdownFinished) return;
                        Application.Current.Dispatcher.BeginInvoke(new System.Action(() =>
                        {
                            ShowTrayStatusReport();
                            MonitorTick(null, EventArgs.Empty);
                        }), DispatcherPriority.Background);
                    }
                    else
                    {
                        KitLugia.Core.Logger.Log("❌ ERRO: Tray Icon não ficou visível após tentativa");
                    }
                }
            else
            {
                KitLugia.Core.Logger.Log($"Tray Icon desativado ou nulo. Enabled: {IsTrayEnabled}, Icon: {_trayIcon != null}");
            }

            // Register for Shutdown events
            Microsoft.Win32.SystemEvents.SessionEnding += (s, e) => ShutdownTurboCharge();


            if (GamePriorityEnabled)
            {
                InitializeGameBoost();
            }

            IsInitialized = true;
        }

        private void StartExplorerWatchdog()
        {
            try
            {
                _explorerWatchTimer?.Dispose();
                _explorerWatchTimer = new System.Threading.Timer(_ =>
                {
                    try { ExplorerTurboWatchdogTick(); }
                    catch (Exception ex) { KitLugia.Core.Logger.LogError("ExplorerWatchdog", ex.Message); }
                }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            }
            catch { /* watchdog é opcional — nunca derruba o kit */ }
        }

        /// <summary>
        /// Detecta o explorer.exe da nossa sessão. Quando o processo (re)aparece — login do
        /// Windows, crash ou restart do shell — arma o turbo. Na 1ª janela de pasta
        /// (CabinetWClass) que abrir depois disso, aplica F11 2x (re-render). Depois desarma:
        /// 1x por processo, sem piscar janela a janela.
        /// </summary>
        private void ExplorerTurboWatchdogTick()
        {
            if (!ExplorerAutoTurbo) { _explorerTurboArmed = false; return; }

            // JÁ APLICADO para o explorer atual: não há mais nada para fazer neste ciclo.
            // Antes, mesmo desarmado, o watchdog continuava chamando GetProcessesByName("explorer")
            // a cada 2s PARA SEMPRE (o timer nunca parava) — enumeração de processos +
            // ProcessId/SessionId sem necessidade, o dia inteiro, só para re-descobrir
            // um explorer.exe que já está lá.
            if (!_explorerTurboArmed && _explorerWatchPid != 0) return;

            int pid = GetSessionExplorerPid();
            if (pid == 0)
            {
                // Explorer ainda não subiu (kit pode iniciar antes do shell no login)
                _explorerWatchPid = 0;
                _explorerTurboArmed = false;
                return;
            }

            if (pid != _explorerWatchPid)
            {
                _explorerWatchPid = pid;
                _explorerTurboArmed = true;
                KitLugia.Core.Logger.Log($"⚡ Turbo Explorer: explorer.exe ativo (PID {pid}) — auto-turbo armado p/ a 1ª janela de pasta.");
                return;
            }

            if (!_explorerTurboArmed) return;

            IntPtr hwnd = FindTopExplorerWindow();
            if (hwnd == IntPtr.Zero) return;   // ainda não abriu nenhuma pasta

            _explorerTurboArmed = false;       // 1x por processo
            var h = hwnd;
            System.Threading.Tasks.Task.Run(() => ApplyF11Render(h, 400, "auto"));
        }

        private static int GetSessionExplorerPid()
        {
            int session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("explorer"))
                {
                    try
                    {
                        // Lê SessionId ANTES de Dispose (a property lança em processo já morto).
                        if (p.SessionId == session) return p.Id;
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
            return 0;
        }

        /// <summary>Janela de pasta (CabinetWClass) mais recente em ordem de Z — não precisa de foco.</summary>
        private static IntPtr FindTopExplorerWindow()
        {
            IntPtr found = IntPtr.Zero;
            Win32Api.EnumWindows((hWnd, lParam) =>
            {
                if (!Win32Api.IsWindowVisible(hWnd)) return true;
                var cls = new System.Text.StringBuilder(256);
                Win32Api.GetClassName(hWnd, cls, cls.Capacity);
                if (cls.ToString() == Win32Api.ExplorerWindowClass)
                {
                    found = hWnd;   // EnumWindows lista em ordem de Z — primeira = mais recente
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// Truque F11 do Explorer: entrar e sair de tela cheia força o Explorer a re-renderizar
        /// a pasta, pulando a lógica bugada de rolagem/posicionamento — o "ir direto ao arquivo"
        /// (ex.: navegador → Mostrar na pasta) passa a acertar na hora.
        ///
        /// Para NÃO interromper o usuário, a janela é tornado INVISÍVEL (opacidade 0 via
        /// layered window) durante o toggle — o re-render acontece, mas o pulo de tela cheia
        /// nunca é exibido. Se a técnica de opacidade falhar, cai no comportamento antigo
        /// (toggle visível) para nunca perder a função.
        /// </summary>
        private static void ApplyF11Render(IntPtr explorerWindow, int dwellMs, string source)
        {
            bool hidingApplied = false;
            bool wasLayered = false;
            long originalExStyle = 0;

            try
            {
                if (explorerWindow == IntPtr.Zero || !Win32Api.IsWindow(explorerWindow)) return;

                // ── 1. Tenta esconder a janela (sem mover / sem roubar foco) ──
                try
                {
                    originalExStyle = Win32Api.GetWindowLongPtr(explorerWindow, Win32Api.GWL_EXSTYLE).ToInt64();
                    wasLayered = (originalExStyle & Win32Api.WS_EX_LAYERED) != 0;
                    if (!wasLayered)
                    {
                        Win32Api.SetWindowLongPtr(explorerWindow, Win32Api.GWL_EXSTYLE,
                            new IntPtr(originalExStyle | Win32Api.WS_EX_LAYERED));
                    }
                    hidingApplied = Win32Api.SetLayeredWindowAttributes(explorerWindow, 0, 0, Win32Api.LWA_ALPHA);
                    if (hidingApplied) System.Threading.Thread.Sleep(60); // deixa o DWM aplicar
                }
                catch { hidingApplied = false; } // sem opacidade → segue visível (fallback)

                // ── 2. Toggle F11 2x (entra e sai de tela cheia) ──
                Win32Api.PostMessage(explorerWindow, Win32Api.WM_KEYDOWN, (IntPtr)Win32Api.VK_F11, IntPtr.Zero);
                Win32Api.PostMessage(explorerWindow, Win32Api.WM_KEYUP, (IntPtr)Win32Api.VK_F11, IntPtr.Zero);
                System.Threading.Thread.Sleep(Math.Max(150, dwellMs));
                Win32Api.PostMessage(explorerWindow, Win32Api.WM_KEYDOWN, (IntPtr)Win32Api.VK_F11, IntPtr.Zero);
                Win32Api.PostMessage(explorerWindow, Win32Api.WM_KEYUP, (IntPtr)Win32Api.VK_F11, IntPtr.Zero);

                KitLugia.Core.Logger.Log($"⚡ Turbo Explorer: F11 2x aplicado (re-render) — origem: {source}{(hidingApplied ? " (invisível)" : "")}.");
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("ApplyF11Render", ex.Message);
            }
            finally
            {
                // ── 3. Restaura a visibilidade SEMPRE ──
                try
                {
                    if (hidingApplied && Win32Api.IsWindow(explorerWindow))
                    {
                        Win32Api.SetLayeredWindowAttributes(explorerWindow, 0, 255, Win32Api.LWA_ALPHA);
                        if (!wasLayered)
                        {
                            Win32Api.SetWindowLongPtr(explorerWindow, Win32Api.GWL_EXSTYLE,
                                new IntPtr(originalExStyle));
                        }
                    }
                }
                catch { /* restauração é best-effort */ }
            }
        }

        /// <summary>Aplicação manual — item do tray "⚡ Turbo Explorer (F11)".</summary>
        private void TurboExplorerF11()
        {
            try
            {
                IntPtr explorer = FindTopExplorerWindow();
                if (explorer == IntPtr.Zero)
                {
                    KitLugia.Core.Logger.Log("⚡ Turbo Explorer: nenhuma janela do Explorer aberta.");
                    return;
                }
                ApplyF11Render(explorer, 500, "manual (tray)");
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("TurboExplorerF11", ex.Message);
            }
        }

        public void ShutdownTurboCharge()
        {
            try
            {
                // Active Shutdown Charge (WM_CLOSE broadcast)
                foreach (var proc in GetCachedProcesses())
                {
                    try
                    {
                        if (proc.MainWindowHandle != IntPtr.Zero)
                        {
                            Win32Api.SendMessage(proc.MainWindowHandle, Win32Api.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        }
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    finally { proc.Dispose(); }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public void SaveSettings()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\KitLugia\TraySettings");
                key.SetValue("IsTrayEnabled", IsTrayEnabled ? 1 : 0);
                key.SetValue("CloseToTray", CloseToTray ? 1 : 0);
                key.SetValue("AutoCleanEnabled", AutoCleanEnabled ? 1 : 0);
                key.SetValue("Threshold", AutoCleanThresholdPercent);
                key.SetValue("Interval", MonitorIntervalSeconds);
                key.SetValue("CleaningMode", (int)SelectedCleaningMode);
                key.SetValue("GamePriority", GamePriorityEnabled ? 1 : 0);
                key.SetValue("ForegroundBoost", ForegroundBoostEnabled ? 1 : 0);
                key.SetValue("BoostSustained", BoostSustainedEnabled ? 1 : 0);
                key.SetValue("RamPressure", RamPressurePercent);
                key.SetValue("StandbyClean", StandbyCleanEnabled ? 1 : 0);
                key.SetValue("IslcThresholdMB", IslcThresholdMB);
                key.SetValue("AntiLeak", MemoryLeakDetectionEnabled ? 1 : 0);
                key.SetValue("FocusAssist", FocusAssistEnabled ? 1 : 0);
                key.SetValue("TimerBoost", TimerBoost ? 1 : 0);
                key.SetValue("NetworkBoost", NetworkBoost ? 1 : 0);
                key.SetValue("DownloadBoostEnabled", DownloadBoostEnabled ? 1 : 0);
                key.SetValue("DownloadBoostLevel", DownloadBoostLevel);
                key.SetValue("DownloadBoostThreshold", DownloadBoostThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture));
                key.SetValue("ProBalance", ProBalance ? 1 : 0);
                key.SetValue("UnparkCpu", UnparkCpuEnabled ? 1 : 0);
                key.SetValue("TurboBoot", TurboBootEnabled ? 1 : 0);
                key.SetValue("TurboShutdown", TurboShutdownEnabled ? 1 : 0);
                

                key.SetValue("EnableSmartAlerts", EnableSmartAlerts ? 1 : 0);
                key.SetValue("EnableBehaviorAnalysis", EnableBehaviorAnalysis ? 1 : 0);
                key.SetValue("HighRamThresholdMB", HighRamThresholdMB);
                key.SetValue("HighCpuThresholdPercent", HighCpuThresholdPercent);
                key.SetValue("AdvancedMonitorIntervalMs", AdvancedMonitorIntervalMs);
                key.SetValue("RamLimiterIntervalMs", RamLimiterIntervalMs);
                key.SetValue("KitMemoryLimitMB", KitMemoryLimitMB);
                key.SetValue("AudioAntiStutter", AudioAntiStutterEnabled ? 1 : 0);
                key.SetValue("GameBarPresenceWriterDisabled", GameBarPresenceWriterDisabled ? 1 : 0);
                key.SetValue("SmartScreenDisabled", SmartScreenDisabled ? 1 : 0);
                key.SetValue("EdgeUpdateDisabled", EdgeUpdateDisabled ? 1 : 0);
                key.SetValue("CompatTelRunnerDisabled", CompatTelRunnerDisabled ? 1 : 0);
                key.SetValue("SearchIndexerDisabled", SearchIndexerDisabled ? 1 : 0);
                key.SetValue("TextInputHostDisabled", TextInputHostDisabled ? 1 : 0);
                key.SetValue("DiagTrackSvcDisabled", DiagTrackSvcDisabled ? 1 : 0);
                key.SetValue("DmwappushSvcDisabled", DmwappushSvcDisabled ? 1 : 0);
                key.SetValue("WerSvcDisabled", WerSvcDisabled ? 1 : 0);
                key.SetValue("PcaSvcDisabled", PcaSvcDisabled ? 1 : 0);
                key.SetValue("TelemetryTasksDisabled", TelemetryTasksDisabled ? 1 : 0);
                key.SetValue("ExplorerAutoTurbo", ExplorerAutoTurbo ? 1 : 0);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        /// <summary>
        /// Auto-fix: se o usuário desativou GameBarPresenceWriter antes e o Windows recriou o .exe, re-desativa.
        /// Roda na inicialização do kit e pode ser chamado pela UI (público).
        /// Não depende SÓ da flag salva: a existência do .bak já é prova de que o usuário desativou.
        /// </summary>
        public void AutoFixGameBarPresenceWriter()
        {
            try
            {
                string system32 = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32");
                string gameBarPath = Path.Combine(system32, "GameBarPresenceWriter.exe");
                string backupPath = Path.Combine(system32, "GameBarPresenceWriter.exe.bak");

                bool exeExists = File.Exists(gameBarPath);
                bool bakExists = File.Exists(backupPath);

                // Nada a fazer: preferência não é desativar E nunca houve .bak
                if (!GameBarPresenceWriterDisabled && !bakExists) return;

                // Já desativado (só .bak) — nada a fazer
                if (bakExists && !exeExists) return;

                // Chegou aqui: .exe existe e o usuário quer desativado (flag salva OU .bak antigo)
                if (!exeExists) return;

                KitLugia.Core.Logger.Log("⚠️ GameBarPresenceWriter: Windows recriou o .exe - re-desativando...");

                // Matar processo antes de mexer no arquivo
                SystemUtils.RunExternalProcess("taskkill", "/F /IM GameBarPresenceWriter.exe", true);
                System.Threading.Thread.Sleep(500);

                if (bakExists)
                {
                    try
                    {
                        SystemUtils.RunExternalProcess("takeown", $"/f \"{backupPath}\"", true);
                        SystemUtils.RunExternalProcess("icacls", $"\"{backupPath}\" /grant *S-1-3-4:F /t /c /l", true);
                        File.Delete(backupPath);
                    }
                    catch (Exception ex) { KitLugia.Core.Logger.Log($"⚠️ GameBarPresenceWriter: erro ao limpar .bak antigo: {ex.Message}"); }
                }

                // Take ownership do .exe e renomeia para .bak
                SystemUtils.RunExternalProcess("takeown", $"/f \"{gameBarPath}\"", true);
                SystemUtils.RunExternalProcess("icacls", $"\"{gameBarPath}\" /grant *S-1-3-4:F /t /c /l", true);

                File.Move(gameBarPath, backupPath);
                KitLugia.Core.Logger.Log("✅ GameBarPresenceWriter re-desativado (renomeado para .bak).");
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ GameBarPresenceWriter auto-fix: {ex.Message}");
            }
        }

        /// <summary>
        /// Aplica/desfaz uma otimização da comunidade (Reddit) pelo método correto do processo:
        /// - SmartScreen: política EnableSmartScreen + Explorer SmartScreenEnabled (registro)
        /// - MicrosoftEdgeUpdate: serviços edgeupdate/edgeupdatem + tarefas agendadas MachineCore/UA
        /// - CompatTelRunner: serviço DiagTrack + tarefas Compatibility Appraiser/ProgramDataUpdater
        /// - SearchIndexer: serviço WSearch
        /// - TextInputHost: IFEO (Image File Execution Options) apontando para systray (bloqueio sem renomear)
        /// Nenhum arquivo é renomeado (TrustedInstaller reverte rename em updates; registro/serviços persistem).
        /// </summary>
        public void ApplyCommunityProcessToggle(string name, bool disable)
        {
            try
            {
                switch (name)
                {
                    case "SmartScreen":
                        ApplySmartScreen(disable);
                        break;
                    case "EdgeUpdate":
                        ApplyEdgeUpdate(disable);
                        break;
                    case "CompatTelRunner":
                        ApplyCompatTelRunner(disable);
                        break;
                    case "SearchIndexer":
                        ApplySearchIndexer(disable);
                        break;
                    case "TextInputHost":
                        ApplyTextInputHost(disable);
                        break;
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ Community toggle {name}: {ex.Message}");
            }
        }

        private void ApplySmartScreen(bool disable)
        {
            if (disable)
            {
                using var polKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\System");
                polKey?.SetValue("EnableSmartScreen", 0, RegistryValueKind.DWord);
                using var expKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer");
                expKey?.SetValue("SmartScreenEnabled", "Off", RegistryValueKind.String);
                KitLugia.Core.Logger.Log("🛡️ SmartScreen desativado (política EnableSmartScreen=0 + Explorer=Off)");
            }
            else
            {
                using var polKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\System", true);
                polKey?.DeleteValue("EnableSmartScreen", false);
                using var expKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer", true);
                expKey?.DeleteValue("SmartScreenEnabled", false);
                KitLugia.Core.Logger.Log("🔄 SmartScreen restaurado");
            }
        }

        private void ApplyEdgeUpdate(bool disable)
        {
            string[] services = { "edgeupdate", "edgeupdatem" };
            string[] tasks = { "MicrosoftEdgeUpdateTaskMachineCore", "MicrosoftEdgeUpdateTaskMachineUA" };

            if (disable)
            {
                foreach (var svc in services)
                    SystemUtils.RunExternalProcess("sc", $"config {svc} start= disabled", true, true, true);
                foreach (var task in tasks)
                    SystemUtils.RunExternalProcess("schtasks", $"/Change /TN \"{task}\" /Disable", true, true, true);
                SystemUtils.RunExternalProcess("taskkill", "/F /IM MicrosoftEdgeUpdate.exe", true, true, true);
                KitLugia.Core.Logger.Log("🛡️ MicrosoftEdgeUpdate desativado (serviços + tarefas agendadas)");
            }
            else
            {
                foreach (var svc in services)
                    SystemUtils.RunExternalProcess("sc", $"config {svc} start= auto", true, true, true);
                foreach (var task in tasks)
                    SystemUtils.RunExternalProcess("schtasks", $"/Change /TN \"{task}\" /Enable", true, true, true);
                KitLugia.Core.Logger.Log("🔄 MicrosoftEdgeUpdate restaurado");
            }
        }

        private void ApplyCompatTelRunner(bool disable)
        {
            string[] tasks = { @"Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
                               @"Microsoft\Windows\Application Experience\ProgramDataUpdater",
                               @"Microsoft\Windows\Application Experience\StartupAppTask" };

            if (disable)
            {
                SystemUtils.RunExternalProcess("sc", "config DiagTrack start= disabled", true, true, true);
                using var polKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection");
                polKey?.SetValue("AllowTelemetry", 0, RegistryValueKind.DWord);
                foreach (var task in tasks)
                    SystemUtils.RunExternalProcess("schtasks", $"/Change /TN \"{task}\" /Disable", true, true, true);
                SystemUtils.RunExternalProcess("taskkill", "/F /IM CompatTelRunner.exe", true, true, true);
                KitLugia.Core.Logger.Log("🛡️ CompatTelRunner desativado (DiagTrack + AllowTelemetry=0 + tarefas)");
            }
            else
            {
                SystemUtils.RunExternalProcess("sc", "config DiagTrack start= auto", true, true, true);
                using var polKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", true);
                polKey?.DeleteValue("AllowTelemetry", false);
                foreach (var task in tasks)
                    SystemUtils.RunExternalProcess("schtasks", $"/Change /TN \"{task}\" /Enable", true, true, true);
                KitLugia.Core.Logger.Log("🔄 CompatTelRunner restaurado");
            }
        }

        private void ApplySearchIndexer(bool disable)
        {
            if (disable)
            {
                SystemUtils.RunExternalProcess("sc", "config WSearch start= disabled", true, true, true);
                SystemUtils.RunExternalProcess("sc", "stop WSearch", true, true, true);
                KitLugia.Core.Logger.Log("🛡️ SearchIndexer desativado (serviço WSearch)");
            }
            else
            {
                SystemUtils.RunExternalProcess("sc", "config WSearch start= delayed-auto", true, true, true);
                KitLugia.Core.Logger.Log("🔄 SearchIndexer restaurado (delayed-auto)");
            }
        }

        private void ApplyTextInputHost(bool disable)
        {
            const string ifeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\TextInputHost.exe";
            if (disable)
            {
                using var key = Registry.LocalMachine.CreateSubKey(ifeoPath);
                key?.SetValue("Debugger", "%SystemRoot%\\system32\\systray.exe", RegistryValueKind.String);
                SystemUtils.RunExternalProcess("taskkill", "/F /IM TextInputHost.exe", true, true, true);
                KitLugia.Core.Logger.Log("🛡️ TextInputHost bloqueado via IFEO (emoji Win+. e teclado virtual desativados)");
            }
            else
            {
                Registry.LocalMachine.DeleteSubKeyTree(ifeoPath, false);
                KitLugia.Core.Logger.Log("🔄 TextInputHost restaurado (IFEO removido)");
            }
        }

        /// <summary>
        /// Auto-fix: aplica uma única vez no startup as otimizações da comunidade salvas.
        /// Idempotente — se já desativado, não faz nada de novo.
        /// </summary>
        private void AutoFixCommunityProcesses()
        {
            if (SmartScreenDisabled) ApplyCommunityProcessToggle("SmartScreen", true);
            if (EdgeUpdateDisabled) ApplyCommunityProcessToggle("EdgeUpdate", true);
            if (CompatTelRunnerDisabled) ApplyCommunityProcessToggle("CompatTelRunner", true);
            if (SearchIndexerDisabled) ApplyCommunityProcessToggle("SearchIndexer", true);
            if (TextInputHostDisabled) ApplyCommunityProcessToggle("TextInputHost", true);

            if (DiagTrackSvcDisabled) SystemTweaks.SetServiceStartup("DiagTrack", true);
            if (DmwappushSvcDisabled) SystemTweaks.SetServiceStartup("dmwappushservice", true);
            if (WerSvcDisabled) SystemTweaks.SetServiceStartup("WerSvc", true);
            if (PcaSvcDisabled) SystemTweaks.SetServiceStartup("PcaSvc", true);
            if (TelemetryTasksDisabled) SystemTweaks.ApplyTelemetryScheduledTasks(true);
        }

        /// <summary>
        /// Liga/desliga o anti-stutter de áudio e (por padrão) persiste a escolha.
        /// Ligar também garante a ESCUTA ligada: sem medição não existe gatilho — a
        /// recuperação só dispara com estalos CONFIRMADOS pelo próprio Windows.
        /// </summary>
        public void SetAudioAntiStutter(bool enabled, bool persist = true)
        {
            try
            {
                var mon = KitLugia.Core.TaskManager.AudioGlitchMonitor.Instance;
                mon.AutoRecover = enabled;

                if (enabled)
                {
                    if (!mon.IsRunning) mon.Start();
                    KitLugia.Core.Logger.Log("🩹 Anti-stutter de áudio LIGADO: o Kit escuta os estalos e ressincroniza o motor de áudio sozinho se eles se repetirem (2 confirmados em 90 s).");
                }
                else
                {
                    // Sem escuta não há gatilho: desligar de verdade libera a thread de
                    // loopback (nada de medir um problema que não vai mais ser tratado).
                    try { if (mon.IsRunning) mon.Stop(); } catch { }
                    KitLugia.Core.Logger.Log("🩹 Anti-stutter de áudio DESLIGADO — o Kit não toca mais no motor de áudio.");
                }

                if (persist) SaveSettings();
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"🩹 Falha ao aplicar o anti-stutter de áudio: {ex.Message}");
            }
        }

        /// <summary>
        /// Grava SÓ a preferência (sem mexer no monitor). Usado pelo checkbox do
        /// Gerenciador de Tarefas, que tem regras próprias de escuta (aba Latência).
        /// </summary>
        public void PersistAudioAntiStutter(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\KitLugia\TraySettings");
                key.SetValue("AudioAntiStutter", enabled ? 1 : 0);
            }
            catch { }
        }

        private void AutoFixForceStopContextMenu()
        {
            try
            {
                if (!SystemTweaks.IsForceStopUnlockAdded()) return;
                string currentExe = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/'), "KitLugia.GUI.exe");
                if (!System.IO.File.Exists(currentExe))
                    currentExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(currentExe)) return;
                var existing = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\*\shell\forcestopunlock\command", "", null) as string;
                if (existing != null && !existing.Contains(currentExe, StringComparison.OrdinalIgnoreCase))
                {
                    SystemTweaks.AddForceStopUnlock();
                    KitLugia.Core.Logger.Log($"[FORCE STOP] Menu de contexto reconfigurado para versão atual: {currentExe}");
                }
            }
            catch (Exception ex) { KitLugia.Core.Logger.Log($"[FORCE STOP] AutoFix menu: {ex.Message}"); }
        }

        public void LoadSettings()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\KitLugia\TraySettings");
                if (key == null)
                {
                    IsTrayEnabled = true; // Primeira execução (Kit "limpo") — ícone SEMPRE visível
                    return;
                }

                // Ícone do tray agora é SEMPRE visível enquanto o Kit roda (opção de ocultar removida).
                // Antes, um Kit limpo iniciava com o ícone DESLIGADO e "Close to Tray" o escondia no
                // background — processo fantasma sem como reabrir (só via Gerenciador de Tarefas).
                if (!ReadBoolSetting(key, "IsTrayEnabled", true))
                    Logger.Log("🔔 Ícone do tray forçado a SEMPRE visível (preferência antiga 'ocultar' migrada para ON).");
                IsTrayEnabled = true;

                // X da janela SEMPRE minimiza para o tray (fechar de verdade = menu do tray >
                // "Sair Completamente"). Opção de "rodar em segundo plano" removida das telas.
                if (!ReadBoolSetting(key, "CloseToTray", true))
                    Logger.Log("🔔 'Rodar em segundo plano' forçado a SEMPRE ATIVO (X minimiza para o tray — preferência antiga migrada).");
                CloseToTray = true;
                AutoCleanEnabled = ReadBoolSetting(key, "AutoCleanEnabled", false);
                AutoCleanThresholdPercent = ReadIntSetting(key, "Threshold", 80);
                MonitorIntervalSeconds = ReadIntSetting(key, "Interval", 30);
                SelectedCleaningMode = (MemoryOptimizer.CleaningMode)ReadIntSetting(key, "CleaningMode", (int)MemoryOptimizer.CleaningMode.Normal);
                GamePriorityEnabled = ReadBoolSetting(key, "GamePriority", false);
                ForegroundBoostEnabled = ReadBoolSetting(key, "ForegroundBoost", true);
                BoostSustainedEnabled = ReadBoolSetting(key, "BoostSustained", true);
                RamPressurePercent = ReadIntSetting(key, "RamPressure", 88);
                StandbyCleanEnabled = ReadBoolSetting(key, "StandbyClean", false);
                IslcThresholdMB = ReadLongSetting(key, "IslcThresholdMB", SuggestIslcThresholdMB());
                MemoryLeakDetectionEnabled = ReadBoolSetting(key, "AntiLeak", false);
                FocusAssistEnabled = ReadBoolSetting(key, "FocusAssist", false);
                TimerBoost = ReadBoolSetting(key, "TimerBoost", false);
                NetworkBoost = ReadBoolSetting(key, "NetworkBoost", false);
                DownloadBoostEnabled = ReadBoolSetting(key, "DownloadBoostEnabled", false);
                DownloadBoostLevel = (string)key.GetValue("DownloadBoostLevel", "Auto");
                double.TryParse((string)key.GetValue("DownloadBoostThreshold", "5.0"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dbt);
                DownloadBoostThreshold = dbt > 0 ? dbt : 5.0;
                ProBalance = ReadBoolSetting(key, "ProBalance", false);
                UnparkCpuEnabled = ReadBoolSetting(key, "UnparkCpu", false);

                EnableSmartAlerts = ReadBoolSetting(key, "EnableSmartAlerts", true);
                EnableBehaviorAnalysis = ReadBoolSetting(key, "EnableBehaviorAnalysis", true);
                HighRamThresholdMB = ReadLongSetting(key, "HighRamThresholdMB", 2048);
                HighCpuThresholdPercent = ReadDoubleSetting(key, "HighCpuThresholdPercent", 80.0);
                AdvancedMonitorIntervalMs = ReadIntSetting(key, "AdvancedMonitorIntervalMs", 2000);
                RamLimiterIntervalMs = ReadIntSetting(key, "RamLimiterIntervalMs", 1000);
                KitMemoryLimitMB = ReadLongSetting(key, "KitMemoryLimitMB", 200);
                // Anti-stutter de áudio: APLICA o valor salvo (sem regravar — acabou de ser
                // lido do registro). Ligado = escuta em loopback + recuperação automática
                // valendo desde a abertura do Kit, sem precisar abrir o Gerenciador de Tarefas.
                SetAudioAntiStutter(ReadBoolSetting(key, "AudioAntiStutter", false), persist: false);
                GameBarPresenceWriterDisabled = ReadBoolSetting(key, "GameBarPresenceWriterDisabled", false);
                SmartScreenDisabled = ReadBoolSetting(key, "SmartScreenDisabled", false);
                EdgeUpdateDisabled = ReadBoolSetting(key, "EdgeUpdateDisabled", false);
                CompatTelRunnerDisabled = ReadBoolSetting(key, "CompatTelRunnerDisabled", false);
                SearchIndexerDisabled = ReadBoolSetting(key, "SearchIndexerDisabled", false);
                TextInputHostDisabled = ReadBoolSetting(key, "TextInputHostDisabled", false);
                DiagTrackSvcDisabled = ReadBoolSetting(key, "DiagTrackSvcDisabled", false);
                DmwappushSvcDisabled = ReadBoolSetting(key, "DmwappushSvcDisabled", false);
                WerSvcDisabled = ReadBoolSetting(key, "WerSvcDisabled", false);
                PcaSvcDisabled = ReadBoolSetting(key, "PcaSvcDisabled", false);
                TelemetryTasksDisabled = ReadBoolSetting(key, "TelemetryTasksDisabled", false);
                ExplorerAutoTurbo = ReadBoolSetting(key, "ExplorerAutoTurbo", true);

                _monitorTimer.Interval = TimeSpan.FromSeconds(MonitorIntervalSeconds);
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ LoadSettings: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Leitura defensiva de settings: o registro pode guardar int/long/double/string
        // de formas diferentes (REG_DWORD, REG_QWORD, REG_BINARY, REG_SZ) — nunca
        // castar direto, senão UM valor com tipo errado aborta o LoadSettings inteiro
        // e o resto das preferências cai para default silenciosamente.
        private static long ReadLongSetting(Microsoft.Win32.RegistryKey key, string name, long def)
        {
            try
            {
                var v = key.GetValue(name);
                if (v == null) return def;
                return v switch
                {
                    long l => l,
                    int i => i,
                    double d => (long)d,
                    string s when long.TryParse(s, out var l2) => l2,
                    string s when double.TryParse(s, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var d2) => (long)d2,
                    byte[] b when b.Length == 8 => BitConverter.ToInt64(b, 0),
                    byte[] b when b.Length == 4 => BitConverter.ToInt32(b, 0),
                    _ => def
                };
            }
            catch { return def; }
        }

        private static double ReadDoubleSetting(Microsoft.Win32.RegistryKey key, string name, double def)
        {
            try
            {
                var v = key.GetValue(name);
                if (v == null) return def;
                return v switch
                {
                    double d => d,
                    int i => i,
                    long l => l,
                    string s when double.TryParse(s, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var d2) => d2,
                    byte[] b when b.Length == 8 => BitConverter.ToDouble(b, 0),
                    byte[] b when b.Length == 4 => BitConverter.ToInt32(b, 0),
                    _ => def
                };
            }
            catch { return def; }
        }

        private static int ReadIntSetting(Microsoft.Win32.RegistryKey key, string name, int def)
        {
            try
            {
                var v = key.GetValue(name);
                if (v == null) return def;
                return v switch
                {
                    int i => i,
                    long l => (int)l,
                    double d => (int)d,
                    string s when int.TryParse(s, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var i2) => i2,
                    byte[] b when b.Length == 4 => BitConverter.ToInt32(b, 0),
                    byte[] b when b.Length == 8 => (int)BitConverter.ToInt64(b, 0),
                    _ => def
                };
            }
            catch { return def; }
        }

        private static bool ReadBoolSetting(Microsoft.Win32.RegistryKey key, string name, bool def)
            => ReadIntSetting(key, name, def ? 1 : 0) == 1;

        private void RunSafetyProfiler()
        {
            // Era uma limpeza de memória REAL (EmptyWorkingSets em TODOS os processos)
            // executada em CADA boot, mesmo com a limpeza automática DESLIGADA e sem o
            // usuário nunca ter pedido. Pior que isso: rodava no start, quando hundreds
            // de páginas estavam sendo faultadas de volta — causeira direta do
            // "lentão/travada nos primeiros segundos" relatado.
            // Agora o profiler só MEDE o custo de uma limpeza leve quando o usuário
            // realmente pediu limpeza automática. Sem ela, não há nada a calibrar.
            if (!AutoCleanEnabled) return;

            try
            {
                // Baseline: How long does a 'Leve' clean take on this system?
                Stopwatch sw = Stopwatch.StartNew();
                MemoryOptimizer.Optimize(MemoryOptimizer.CleaningMode.Leve);
                sw.Stop();

                _lastCleanDurationMs = sw.ElapsedMilliseconds;
                // If it takes > 300ms just for a Leve clean, this system is slow/busy
                if (_lastCleanDurationMs > 300)
                {
                    _stutterBackoffCycles = 1; // Start with caution
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        private void ApplyTrayEnabledState(bool enabled)
        {
            if (_trayIcon != null)
                _trayIcon.Visible = enabled;

            if (enabled)
            {
                if (!_monitorTimer.IsEnabled) _monitorTimer.Start();
                UpdateTrayIcon(GetMemoryUsagePercent());
            }
            else
            {
                _monitorTimer.Stop();
            }

            SaveSettings();
        }

        public void SetTrayEnabled(bool enabled)
        {
            // Ícone do tray é SEMPRE visível enquanto o Kit roda — pedidos para ocultar são ignorados
            // (mantido por compatibilidade com telas antigas que ainda chamam este método).
            if (!enabled)
                Logger.Log("🔔 Ícone do tray é sempre visível — solicitação de ocultar ignorada.");
            IsTrayEnabled = true;

            if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
            {
                d.BeginInvoke(new System.Action(() => ApplyTrayEnabledState(true)));
                return;
            }

            ApplyTrayEnabledState(true);
        }


        public void PauseMonitoring()
        {
            try
            {
                _monitorTimer?.Stop();
                StopAdvancedMonitor();
                KitLugia.Core.Logger.Log("⏸️ TrayIcon: Monitoramento pausado (janela oculta)");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }


        public void ResumeMonitoring()
        {
            try
            {
                if (IsTrayEnabled && !_monitorTimer.IsEnabled)
                {
                    _monitorTimer?.Start();
                    StartAdvancedMonitor();
                    KitLugia.Core.Logger.Log("▶️ TrayIcon: Monitoramento retomado (janela visível)");
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // Guarda de overlap: sob carga um ciclo pode durar mais que o intervalo (0 = livre, 1 = em andamento).
        private int _monitorTickBusy;

        // Ultimo percentual de RAM pintado no icone — evita recriar bitmap/GDI e notificar o shell sem mudanca.
        private int _lastTrayPercent = -1;

        /// <summary>Trabalho de UI do tray, sem bloquear o chamador (usado pelos ciclos em background).</summary>
        private static void PostTrayUi(System.Action action)
        {
            var app = Application.Current;
            if (app?.Dispatcher == null || app.Dispatcher.HasShutdownFinished) return;
            try { app.Dispatcher.BeginInvoke(DispatcherPriority.Background, action); } catch { }
        }

        /// <summary>
        /// Dispara o ciclo de monitoramento FORA da thread de UI.
        ///
        /// POR QUE: sob pressao (RAM cheia / CPU saturada) o tick sincrono rodava na thread
        /// de UI: Process.GetProcesses() em 350+ processos, PerformanceCounter de IO por
        /// processo (perflib do "Process"), trims de working set e append em CSV. Em maquina
        /// ociosa isso custa ~200 ms; com o sistema carregado infla para SEGUNDOS e o Windows
        /// mostra "(Nao Respondendo)" — exatamente quando o usuario clica no kit.
        /// Agora o calculo roda no thread pool e a thread de UI so pinta o icone.
        /// </summary>
        private void MonitorTick(object? sender, EventArgs e)
        {
            // Ciclo anterior ainda rodando: pula (nao empilha trabalho sob carga)
            if (System.Threading.Interlocked.CompareExchange(ref _monitorTickBusy, 1, 0) != 0) return;

            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try { RunMonitorCycle(); }
                catch { /* monitoramento nunca derruba o kit */ }
                finally { System.Threading.Interlocked.Exchange(ref _monitorTickBusy, 0); }
            });
        }

        /// <summary>Corpo do ciclo do monitor — RODA NO THREAD POOL (nao tocar em UI daqui).</summary>
        private void RunMonitorCycle()
        {
            try
            {
                _monitorTickCounter = unchecked(_monitorTickCounter + 1);

                // 0. Download Boost Auto-Detection
                if (DownloadBoostEnabled)
                {
                    uint fgPid = GamePriorityEnabled ? _currentBoostedPid : 0;
                    var trafficSnapshot = NetworkTrafficMonitor.SampleTraffic(fgPid > 0 ? fgPid : null);
                    var level = DownloadBoostLevel switch
                    {
                        "Download" => DownloadBoostEngine.BoostLevel.Download,
                        "Latency" => DownloadBoostEngine.BoostLevel.Latency,
                        "Balanced" => DownloadBoostEngine.BoostLevel.Balanced,
                        _ => DownloadBoostEngine.BoostLevel.Auto
                    };

                    bool systemWide = level switch
                    {
                        DownloadBoostEngine.BoostLevel.Download => true,
                        _ => false
                    };

                    var config = new DownloadBoostEngine.DownloadBoostConfig
                    {
                        Enabled = true,
                        Level = level,
                        AutoThresholdMBps = DownloadBoostThreshold,
                        ForegroundPid = fgPid,
                        SystemWideTuning = systemWide
                    };

                    var targets = level == DownloadBoostEngine.BoostLevel.Auto
                        ? DownloadBoostEngine.AutoDecide(trafficSnapshot, config)
                        : NetworkTrafficMonitor.GetActiveDownloaders(trafficSnapshot, DownloadBoostThreshold)
                            .Select(p => p.Pid).ToList();

                    foreach (var pid in targets)
                        DownloadBoostEngine.Apply(config, pid);

                    foreach (var boostedPid in DownloadBoostEngine.BoostedPids.ToList())
                    {
                        if (!targets.Contains(boostedPid))
                            DownloadBoostEngine.Revert(boostedPid);
                    }

                    DownloadBoostEngine.CleanupStaleBackups();
                }

                // 1. Refresh System Stats
                //    (unico trecho que pertence a thread de UI: o NotifyIcon)
                var stats = MemoryOptimizer.GetMemoryStats();
                int usedPercent = stats.Percent;
                PostTrayUi(() =>
                {
                    UpdateTrayIcon(usedPercent);
                    if (_trayIcon != null)
                        _trayIcon.Text = $"KitLugia - RAM: {usedPercent}% em uso";
                });

                // 2. Auto-clean logic (Manual/Threshold)
                if (AutoCleanEnabled && usedPercent >= AutoCleanThresholdPercent)
                {
                    CleanRamNow();
                }

                // 3. Game Priority Boost
                // NOTA: OptimizeForegroundProcess so roda quando NAO ha hook ativo
                // (hook/timer 250ms ja cuida do boost via CheckForegroundWindow)
                if (GamePriorityEnabled && !_useWinEventHook && _foregroundCheckTimer == null)
                {
                    OptimizeForegroundProcess();
                }

                // 3b. REAVALIACAO DO MOTOR AUTOMATICO (03/10/2026)
                // O motor Auto decide por cena (loading x jogo leve), mas ApplyBoostModern so roda
                // na transicao para foreground. Sem reavaliar aqui, a primeira avaliacao ficaria
                // presa para o resto da sessao — e, como a medicao de CPU e por DELTA entre duas
                // amostras, a primeira chamada nem tem amostra anterior e usaria o valor neutro.
                // A cada 5 ticks (~15 s com tick de 3 s) reavalia: barato e responsivo o bastante.
                if (TrayIconService.CurrentEngine == GameBoostEngine.Auto && !IsCustomEngineActive)
                {
                    uint boostedPid = _currentBoostedPid;
                    // So reavalia quem esta DE FATO em boost: _currentBoostedPid tambem aponta
                    // para janelas que nao entram no boost (browsers, excecoes do usuario) e
                    // boostar essas seria o contrario do que a tela promete.
                    if (boostedPid != 0 && _monitorTickCounter % 5 == 0 && _boostTargets.ContainsKey(boostedPid))
                    {
                        try { ApplyBoostModern(boostedPid, BoostLevel.Foco); }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }
                }

                // 3c. FAIXA DE PRIORIDADE: mantem em AboveNormal o app que saiu do foco mas
                // continua trabalhando e devolve a prioridade original de quem ficou ocioso.
                // O "reverso automatico" mora aqui — nao depende de hook nem de evento de janela.
                TickBoostTiers();

                // 4. Standby List Cleaning (be gentle)
                if (StandbyCleanEnabled)
                {
                    CheckAndCleanStandby(usedPercent);
                }

                // 5. Memory Leak Mitigation (Anti-Leak) - Targeted and Smart
                if (MemoryLeakDetectionEnabled)
                {
                    DetectAndTrimLeaks(usedPercent, stats);
                }

                // 6. Focus Assist (Quiet Hours)
                if (FocusAssistEnabled)
                {
                    ManageFocusAssist();
                }

                // 7. Dynamic Intelligence (V2) - Tracker & Firemin-Optimized Trim
                UpdateProcessProfiles(stats);
                ApplyFireminOptimizations();

                // 8. Auto-Log Stats
                LogStats(stats);
            }
            catch
            {
                // Silently ignore monitoring errors
            }
        }

        private void UpdateProcessProfiles(MemoryOptimizer.MemoryInfo stats)
        {
            try
            {
                IntPtr foregroundHwnd = Win32Api.GetForegroundWindow();
                uint foregroundPid = 0;
                if (foregroundHwnd != IntPtr.Zero) Win32Api.GetWindowThreadProcessId(foregroundHwnd, out foregroundPid);

                foreach (var proc in GetCachedProcesses())
                {
                    try
                    {
                        string name = proc.ProcessName.ToLower();
                        if (name == "explorer" || name == "dwm" || name == "lsass" || name == "csrss") continue;

                        // Only track user-facing apps for VIP promotion
                        if (proc.MainWindowHandle == IntPtr.Zero || !IsTaskbarWindow(proc.MainWindowHandle)) continue;

                        var profile = _processProfiles.GetOrAdd(name, _ => new ProcessProfile { Name = name });

                        profile.TotalCyclesVisible++;
                        if (proc.Id == foregroundPid) profile.CyclesForeground++;
                        profile.LastKnownWs = proc.WorkingSet64;
                        profile.LastSeenTick = _monitorTickCounter;

                        // Promotion logic:
                        // 1. Known browsers/apps
                        if (!profile.IsVip)
                        {
                            bool isKnownVip = _vipProcesses.Any(v => name.Contains(v)) || name.Contains("chrome") || name.Contains("msedge") || name.Contains("brave") || name.Contains("vivaldi");
                            // 2. Used heavily (long cycles visible)
                            bool isHeavyUse = profile.TotalCyclesVisible > 5;

                            if (isKnownVip || isHeavyUse)
                            {
                                profile.IsVip = true;
                                // Log promotion indirectly via CSV (later or debug)
                            }
                        }
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    finally { proc.Dispose(); }
                }

                PruneDeadProcessProfiles();
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        private long _monitorTickCounter = 0;

        private void PruneDeadProcessProfiles()
        {
            try
            {
                if (_processProfiles.Count <= 60) return;

                // Remove perfis que nao foram vistos nos ultimos 60 ticks (30min a 30s/tick)
                var cutoff = _monitorTickCounter - 60;
                foreach (var key in _processProfiles.Keys.ToList())
                {
                    if (_processProfiles.TryGetValue(key, out var p) && p.LastSeenTick < cutoff)
                        _processProfiles.TryRemove(key, out _);
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        private void ApplyFireminOptimizations()
        {
            try
            {
                // Firemin: trim suave para processos VIP usando o modelo combinado
                // (mesmo modelo que o RAM Limiter - SEM EmptyWorkingSet bruto)
                foreach (var profile in _processProfiles.Values)
                {
                    if (!profile.IsVip) continue;

                    // Rate Limit: 10 segundos entre trims
                    if ((DateTime.Now - profile.LastTrimTime).TotalSeconds < 10) continue;

                    // Threshold: so trim se excede 300MB
                    if (profile.LastKnownWs < 300L * 1024 * 1024) continue;

                    try
                    {
                        foreach (var proc in Process.GetProcessesByName(profile.Name))
                        {
                            try
                            {
                                IntPtr handle = OpenProcess(
                                    PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION | 0x0200,
                                    false, proc.Id);
                                if (handle != IntPtr.Zero)
                                {
                                    try
                                    {
                                        long wsMB = profile.LastKnownWs / (1024 * 1024);
                                        long targetMB = Math.Max(150, wsMB * 70 / 100); // trim 30% suave
                                        long floorMB = Math.Max(60, wsMB * 30 / 100); // floor 30% do WS atual

                                        // 1. Memory priority = VERY_LOW (OS trimma este processo primeiro)
                                        SetProcessMemoryPriority(handle, Win32Api.MEMORY_PRIORITY_VERY_LOW);

                                        // 2. Hard ceiling: min=floor, max=target
                                        SetProcessWorkingSetSizeEx(handle,
                                            (IntPtr)(floorMB * 1024 * 1024),
                                            (IntPtr)(targetMB * 1024 * 1024),
                                            0); // HARDWS_MAX_DISABLE

                                        // 3. EmptyWorkingSet SO quando WS > 150% do target (kickstart)
                                        if (wsMB > targetMB * 150 / 100)
                                            EmptyWorkingSet(handle);

                                        // 4. Restaura NORMAL quando dentro do target
                                        if (wsMB <= targetMB)
                                            SetProcessMemoryPriority(handle, Win32Api.MEMORY_PRIORITY_NORMAL);
                                    }
                                    finally { CloseHandle(handle); }
                                }
                            }
                            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                            finally { proc.Dispose(); }
                        }
                        profile.LastTrimTime = DateTime.Now;
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        private void LogStats(MemoryOptimizer.MemoryInfo stats)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(_logPath)!;
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);

                // Rotação: 1 linha por ciclo de monitor (~30s) = ~1.05M linhas/ano
                // (~35 MB) num arquivo que ninguém nunca abre. Passando de 2 MB,
                // vira para .1 (mantendo o anterior) e recomeça.
                try
                {
                    var fi = new System.IO.FileInfo(_logPath);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                    {
                        string old = System.IO.Path.ChangeExtension(_logPath, "1.csv");
                        try { if (System.IO.File.Exists(old)) System.IO.File.Delete(old); } catch { }
                        System.IO.File.Move(_logPath, old);
                    }
                }
                catch { /* rotação é best-effort — nunca pode derrubar o log */ }

                bool exists = System.IO.File.Exists(_logPath);
                using var sw = new System.IO.StreamWriter(_logPath, true);
                if (!exists) sw.WriteLine("Timestamp,UsedPercent,UsedGB,FreeGB,LastDurationMs,StutterCycles");

                sw.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{stats.Percent},{stats.UsedGB:F2},{stats.FreeGB:F2},{_lastCleanDurationMs},{_stutterBackoffCycles}");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        private bool _lastFocusState = false;
        private void ManageFocusAssist()
        {
            try
            {
                // Check if a game/foreground app is likely active
                IntPtr hwnd = Win32Api.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;
                Win32Api.GetWindowThreadProcessId(hwnd, out uint pid);


                if (pid == 0) return;

                using var proc = Process.GetProcessById((int)pid);
                string name = proc.ProcessName.ToLower();

                // If it's not a system/shell process, assume we want focus
                bool shouldFocus = (name != "explorer" && name != "dwm" && name != "shellexperiencehost" && name != "searchhost");

                if (shouldFocus != _lastFocusState)
                {
                    SetWindowsFocusAssist(shouldFocus);
                    _lastFocusState = shouldFocus;
                }
            }
            catch (System.ComponentModel.Win32Exception) { /* Processo encerrou - ignorar */ }
            catch (ArgumentException) { /* PID inválido - ignorar */ }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        private void SetWindowsFocusAssist(bool enable)
        {
            try
            {
                // Registry key for Focus Assist (Quiet Hours) - simplified approach
                // 0 = Off, 1 = Priority Only, 2 = Alarms Only
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings", true);
                if (key != null)
                {
                    key.SetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED", enable ? 0 : 1, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        private void DetectAndTrimLeaks(int systemUsagePercent, MemoryOptimizer.MemoryInfo stats)
        {
            try
            {
                // Only act if system RAM usage is starting to get high
                if (systemUsagePercent < 65) return;

                // Threshold: 15% of total RAM or at least 2GB
                ulong standardThreshold = (ulong)(stats.TotalBytes * 0.15);
                if (standardThreshold < 2000UL * 1024 * 1024) standardThreshold = 2000UL * 1024 * 1024;

                // VIP Threshold: 25% of total RAM (much more tolerant)
                ulong vipThreshold = (ulong)(stats.TotalBytes * 0.25);

                IntPtr foregroundHwnd = Win32Api.GetForegroundWindow();
                uint foregroundPid = 0;
                if (foregroundHwnd != IntPtr.Zero) Win32Api.GetWindowThreadProcessId(foregroundHwnd, out foregroundPid);

                foreach (var proc in GetCachedProcesses())
                {
                    try
                    {
                        if (proc.Id == foregroundPid) continue;

                        string name = proc.ProcessName.ToLower();
                        bool isVip = _vipProcesses.Any(v => name.Contains(v));

                        if (name == "explorer" || name == "dwm" || name == "lsass" || name == "csrss" || name == "searchindexer") continue;

                        ulong currentWs = (ulong)proc.WorkingSet64;
                        ulong activeThreshold = isVip ? vipThreshold : standardThreshold;

                        if (currentWs > activeThreshold)
                        {
                            // MODELO COMBINADO em vez de EmptyProcessWorkingSet bruto
                            // (previne page fault storm em navegadores/Electron)
                            try
                            {
                                IntPtr handle = OpenProcess(
                                    PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION | 0x0200,
                                    false, proc.Id);
                                if (handle != IntPtr.Zero)
                                {
                                    try
                                    {
                                        long wsMB = (long)(currentWs / (1024 * 1024));
                                        long targetMB = Math.Max(100, wsMB * 60 / 100); // trim 40% suave
                                        long floorMB = Math.Max(50, wsMB * 25 / 100); // floor 25% do WS

                                        SetProcessMemoryPriority(handle, Win32Api.MEMORY_PRIORITY_VERY_LOW);
                                        SetProcessWorkingSetSizeEx(handle,
                                            (IntPtr)(floorMB * 1024 * 1024),
                                            (IntPtr)(targetMB * 1024 * 1024),
                                            0); // HARDWS_MAX_DISABLE

                                        if (wsMB > targetMB * 150 / 100)
                                            EmptyWorkingSet(handle);

                                        if (wsMB <= targetMB)
                                            SetProcessMemoryPriority(handle, Win32Api.MEMORY_PRIORITY_NORMAL);
                                    }
                                    finally { CloseHandle(handle); }
                                }
                            }
                            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                        }
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    finally { proc.Dispose(); }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        private readonly object _boostLock = new(); // Serializa boost/revert do hook


        public enum GameBoostEngine
        {
            V1_Balanced = 1,      // Equilibrado - não trava, velocidade consistente (PADRÃO)
            V2_StableFPS = 2,     // FPS estável - pode travar um pouco
            V3_Extreme = 3,       // Extremo - rede estável/mais rápida, pode travar mais
            V4_ExtremePro = 4,    // Máximo desempenho — agora SEM RealTime e SEM I/O Crítico
            Auto = 5              // Motor único automático (PADRÃO desde 03/10/2026)
        }

        private static GameBoostEngine _currentEngine = GameBoostEngine.Auto;

        public static GameBoostEngine CurrentEngine
        {
            get => _currentEngine;
            set
            {
                _currentEngine = value;
            }
        }

        /// <summary>
        /// Traduz o nivel de I/O configurado para um valor SEGURO (3/10/2026).
        ///
        /// O codigo antigo fazia `cfg.IoPriorityLevel == 1 ? 4 : ...`, e 4 = IoPriorityCritical.
        /// I/O Crítico coloca o processo à FRENTE de TODOS na fila do disco — inclusive do
        /// pagefile, do log do Windows e do antivirus. O pagefile starved é page fault, e page
        /// fault em rajada com disco saturado é exatamente a travada/tela preta que o usuario
        ///-DFICIENTE/reportava. O nivel 1 da config agora significa "High" (3), nunca "Critical".
        ///
        /// Mapa: 0 -> 2 (Normal) | 1 -> 3 (High, historico "critical") | 2..4 -> 3 (High, teto)
        /// </summary>
        private static int SafeIoPriority(int configured)
        {
            if (configured <= 0) return 2;   // Normal
            return 3;                        // High — teto seguro. Nunca 4 (Critical).
        }

        public static string GetEngineDescription(GameBoostEngine engine) => engine switch
        {
            GameBoostEngine.Auto => "Automático (recomendado)",
            GameBoostEngine.V1_Balanced => "V1 - Equilibrado",
            GameBoostEngine.V2_StableFPS => "V2 - FPS Estável",
            GameBoostEngine.V3_Extreme => "V3 - Extremo (Rede+)",
            GameBoostEngine.V4_ExtremePro => "V4 - Performance (High + I/O Alta)",
            _ => "Desconhecido"
        };

        public static void SetEngine(GameBoostEngine engine) => CurrentEngine = engine;

        // Guard: um numero fora do enum (ex.: 0 gravado como "sem motor") deixava
        // CurrentEngine num valor INVALIDO, o switch de ApplyBoostModern caia no
        // default e o motor efetivo deixava de ser o que a UI mostrava.
        public static void SetEngine(int engineNumber) =>
            CurrentEngine = Enum.IsDefined(typeof(GameBoostEngine), engineNumber)
                ? (GameBoostEngine)engineNumber
                : GameBoostEngine.Auto;


        public static CustomEngineConfig? _customEngineConfig = null;
        public static bool IsCustomEngineActive => _customEngineConfig != null;

        // Padrao = Auto: sair de um motor personalizado tem de devolver o RECOMENDADO,
        // nao um legado que o utilizador pode nunca ter escolhido.
        private static GameBoostEngine _previousEngine = GameBoostEngine.Auto;

        public static void SetCustomEngine(CustomEngineConfig config)
        {
            _previousEngine = _currentEngine;
            _customEngineConfig = config;
            _currentEngine = GameBoostEngine.V1_Balanced; // Reset para não conflitar
            KitLugia.Core.Logger.Log($"🎮 GameBoost: Motor personalizado ativado - {config.CpuPriority} | ProBalance: {(config.ProBalance ? "ON" : "OFF")}");
        }

        public static void ClearCustomEngine()
        {
            _customEngineConfig = null;
            _currentEngine = _previousEngine;
            KitLugia.Core.Logger.Log($"🎮 GameBoost: Motor personalizado desativado - Restaurado para {GetEngineDescription(_previousEngine)}");
        }


        public static void ForceReapplyBoost(uint pid)
        {
            // NUNCA fazer boost em si mesmo
            if (pid == 0 || pid == Environment.ProcessId) return;

            try
            {
                var service = _instance;
                if (service == null) return;

                // Reverte o boost anterior para garantir estado limpo
                // (o processo volta a prioridade original e depois recebe a nova faixa)
                service.RevertBoost(pid);

                // Registra na faixa: sem isso, o processo ficaria boosted sem estar na faixa e
                // perderia o "reverso automatico" na proxima troca de janela.
                string procName;
                try { using var p = Process.GetProcessById((int)pid); procName = p.ProcessName; }
                catch { procName = $"PID {pid}"; }
                service.PromoteBoost(pid, procName);

                // Aplica o boost com o novo motor
                service.ApplyBoostModern(pid, BoostLevel.Foco);

                // Se for V2, V3 ou motor personalizado com ProBalance, aplica o ProBalance também
                if (_currentEngine != GameBoostEngine.V1_Balanced || 
                    (_customEngineConfig != null && _customEngineConfig.ProBalance))
                {
                    service.ApplyProBalance(pid);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"⚠️ Erro em ForceReapplyBoost: {ex.Message}");
            }
        }

        // Referência estática para acesso ao método privado
        private static TrayIconService? _instance;


        public uint CurrentForegroundPid => _currentBoostedPid;


        public IntPtr CurrentForegroundHwnd => _lastForegroundHwnd;


        private uint _currentBoostedPid = 0;
        private IntPtr _lastForegroundHwnd = IntPtr.Zero;
        private DateTime _lastBoostTime = DateTime.MinValue;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, ProcessPriorityClass> _originalPriorities = new();
        private readonly TimeSpan _boostCooldown = TimeSpan.FromMilliseconds(50);

        // ───────────────────────── FAIXA DE PRIORIDADE (03/10/2026) ─────────────────────────
        // PROBLEMA QUE ISSO RESOLVE: o modelo antigo tinha UMA vaga so. Ao trocar de janela,
        // o processo anterior era REVERTIDO na hora (volta a Normal) mesmo quando era o jogo
        // que continuava carregando em segundo plano — ou seja, o boost era jogado fora
        // exatamente no cenario (loading com Alt+Tab) em que o usuario mais sente falta dele.
        //
        // MODELO NOVO: prioridade em FAIXA, com histerese.
        //   Nenhum      = restaurado a prioridade original (comportamento padrao do Windows)
        //   Sustentado  = app que saiu do foco mas ainda esta TRABALHANDO (loading, streaming,
        //                compilacao): fica em AboveNormal + I/O Normal + page Normal.
        //                Leve, some em ~1 tique de ociosidade, e nunca rouba RAM dos outros.
        //   Foco        = janela em primeiro plano: o motor escolhe os parametros (HIGH, no maximo).
        // Nunca REALTIME em nenhum nivel: RealTime impede o scheduler de rodar thread de
        // kernel/DPC e foi a causa das travadas/tela preta/BSOD.
        public enum BoostLevel
        {
            Nenhum = 0,
            Sustentado = 1,
            Foco = 2
        }

        private sealed class BoostTarget
        {
            public BoostLevel Level = BoostLevel.Foco;
            public string Name = "";
            /// Ultima vez em que o processo consumiu CPU de verdade (renova enquanto trabalha).
            public DateTime LastWorking;
            /// Comeco do ocioso atual; DateTime.MinValue = ainda trabalhando.
            public DateTime IdleSince = DateTime.MinValue;
            /// A faixa Sustentado ja foi aplicada (evita reaplicar a cada tique).
            public bool SustainedApplied;
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, BoostTarget> _boostTargets = new();

        /// Intervalo de ociosidade antes de devolver o processo para a prioridade original.
        /// A faixa efetiva acompanha o tick do monitor: sem isso, com o monitor em 60 s, um
        /// processo sustentado podia ficar acima da normal por quase 100 s (a contagem so
        /// acontece no tique). Acima => no maximo ~1,75 tique.
        private static readonly TimeSpan BoostIdleGraceMax = TimeSpan.FromSeconds(45);
        private TimeSpan BoostIdleGrace => TimeSpan.FromSeconds(
            Math.Clamp(MonitorIntervalSeconds * 0.75, 15, BoostIdleGraceMax.TotalSeconds));

        /// Fracao de CPU abaixo da qual o processo e considerado ocioso.
        private const float BoostIdleCpuShare = 0.02f;

        /// Teto de processos na faixa Sustentado (evita "boostar" o sistema inteiro).
        private const int MaxSustainedTargets = 3;

        /// Habilita a faixa Sustentado (processo que saiu do foco ainda ativo).
        public bool BoostSustainedEnabled { get; set; } = true;

        /// <summary>
        /// Percentual de RAM em uso que autoriza o RAM Limiter a aplicar o TETO DURO (corte seco).
        /// Abaixo disso o limite fica SOFADO (o Windows reduz sozinho, sem rajada de disco).
        /// </summary>
        public int RamPressurePercent { get; set; } = 88;
        private Win32Api.WinEventDelegate? _winEventDelegate;
        private IntPtr _winEventHook = IntPtr.Zero;
        private bool _useWinEventHook = false;


        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        /// <summary>
        /// Obtém o título da janela com precisão usando UIAutomation para identificar abas específicas
        /// </summary>
        private string GetWindowTitle(IntPtr hwnd)
        {
            try
            {
                int length = GetWindowTextLength(hwnd);
                if (length > 0)
                {
                    var sb = new StringBuilder(length + 1);
                    GetWindowText(hwnd, sb, sb.Capacity);
                    return sb.ToString();
                }
            }
            catch (Exception ex) { ConditionalLog.LogOnce("GetWindowTitle", ex); }
            return "";
        }

        // REMOVIDO (05/10/2026): _heavyAppIndicators — a heuristica "unreal/unity/game/lobby/
        // steam" estava DECLARADA e NUNCA usada. Nao e' a falta dela que faz falta: o
        // desenho do motor NAO e' detetar jogo, e' reagir a quem esta em foreground
        // (que pode ser o jogo, um browser com jogo web, ou o Mapa de Caracteres).
        // O "terceiro balde" do motor nao e' "isto e' jogo", e' "ja em boost e perdeu o
        // foco" -> faixa Sustentado. Detectores de jogo aqui dariam falso negativo no
        // jogo independente/Emulated e falso positivo em qualquer janela com "game" no nome.

        private static readonly HashSet<string> _protectedProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            // Windows Core
            "explorer", "dwm", "shellexperiencehost", "searchindexer", "taskmgr",
            "csrss", "lsass", "svchost", "services", "winlogon", "smss", "crss",
            "wininit", "memory compression", "registry", "system",
            // Áudio (crítico para não travar som)
            "audiodg", "audioendpointbuilder", "audiosrv", "audioengine",
            // GPU/Drivers (crítico para display)
            "nvcontainer", "nvservices", "nvdisplay.container", "amdremont",
            "amdrsserv", "intelgraphics", "igfxem", "igfxhk", "igfxtray",
            // Rede (crítico para conectividade)
            "wpnService", "wpnUserService",
            // Input (crítico para mouse/teclado)
            "ctfmon", "tabtip", "textinputhost",
            // Xbox/Gaming (crítico - gameplatformservices.dll_unloaded crash)
            "xboxgamebarwidgets", "xboxpcappft", "gamebar", "gamingservices",
            "xboxapp", "xboxidp", "gamelauncher"
        };


        private void OptimizeForegroundProcess()
        {
            // O motor de foreground tem UM caminho so (a faixa de prioridade).
            // Antes este metodo mantinha um segundo estado paralelo (_lastBoostedPid /
            // _lastOriginalPriority) que brigava com o do hook: uma restaurava a prioridade
            // do processo enquanto a outra ainda achava que ele era dela. Delegando, os dois
            // caminhos veem o mesmo estado e o conflito deixa de existir.
            CheckForegroundWindow();
        }

        // Timer para verificação rápida do foreground (alternativa estável ao hook)
        private DispatcherTimer? _foregroundCheckTimer;


        // Tenta registrar SetWinEventHook — retorna true se bem-sucedido
        private bool TryRegisterWinEventHook()
        {
            try
            {
                _winEventDelegate = OnForegroundChanged;
                _winEventHook = Win32Api.SetWinEventHook(
                    Win32Api.EVENT_SYSTEM_FOREGROUND, Win32Api.EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero, _winEventDelegate, 0, 0,
                    // SKIPOWNPROCESS: o proprio Kit nao gera evento de foreground para si
                    // (ele ja nao faz boost em si mesmo), entao filtrar e' uma correcao
                    // direta — menos chamadas ao CheckForegroundWindow, sem efeito colateral.
                    Win32Api.WINEVENT_OUTOFCONTEXT | Win32Api.WINEVENT_SKIPOWNPROCESS);
                if (_winEventHook != IntPtr.Zero)
                {
                    _useWinEventHook = true;
                    KitLugia.Core.Logger.Log("🎮 GameBoost: SetWinEventHook registrado com sucesso");
                    return true;
                }
                KitLugia.Core.Logger.Log("⚠️ GameBoost: SetWinEventHook retornou handle nulo, usando polling");
            }
            catch (Exception ex)
            {
                ConditionalLog.LogOnce("WinEventHookRegister", ex);
                KitLugia.Core.Logger.Log($"⚠️ GameBoost: Falha no SetWinEventHook ({ex.GetType().Name}), usando polling fallback");
            }
            return false;
        }

        public void InitializeGameBoost()
        {
            if (!GamePriorityEnabled) return;

            EnsureSeDebugPrivilege();

            try
            {
                _instance = this;

                // Tenta hook primeiro; se falhar, fallback para polling
                if (!TryRegisterWinEventHook())
                {
                    _foregroundCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    _foregroundCheckTimer.Tick += (s, e) => CheckForegroundWindow();
                    _foregroundCheckTimer.Start();
                    KitLugia.Core.Logger.Log("🎮 GameBoost ativado (Polling 250ms)");
                }

                // Timer dedicado do ProBalance (sempre polling, independente)
                _proBalanceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _proBalanceTimer.Tick += ProBalanceTimerTick;
                _proBalanceTimer.Start();
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ Falha no GameBoost: {ex.Message}");
            }
        }

        // Callback do WinEventHook — RODA EM THREAD DO WINDOWS (qualquer exceção não tratada = crash)
        private void OnForegroundChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            try
            {
                if (hwnd == IntPtr.Zero || hwnd == _lastForegroundHwnd) return;
                _lastForegroundHwnd = hwnd;
                if (Application.Current?.Dispatcher == null || Application.Current.Dispatcher.HasShutdownFinished) return;
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    try { CheckForegroundWindow(); }
                    catch (Exception ex) { ConditionalLog.LogOnce("WinEventHookDispatch", ex); }
                });
            }
            catch { /* engole qualquer erro na thread do hook para não crashar */ }
        }private void CheckForegroundWindow()
            {
            try
            {
                IntPtr currentHwnd = Win32Api.GetForegroundWindow();
                if (currentHwnd == IntPtr.Zero) return;

                // Debounce - verifica ANTES de processar
                if ((DateTime.Now - _lastBoostTime) < _boostCooldown) return;
                _lastBoostTime = DateTime.Now;

                // Obtém PID do foreground
                Win32Api.GetWindowThreadProcessId(currentHwnd, out uint pid);
                if (pid == 0) return;
                if (pid == _currentBoostedPid) return; // Mesmo processo


                string windowTitle = GetWindowTitle(currentHwnd);

                // Verifica se deve aplicar boost
                bool shouldBoost = ShouldBoostProcess(pid, currentHwnd);

                // O processo anterior NAO e revertido na hora: ele desce para a faixa
                // Sustentado (reverso automatico, histerese) e so volta a prioridade
                // original quando ficar ocioso. Reverter na hora era o que jogava fora o
                // boost do jogo no exato cenario de loading com Alt+Tab.
                if (_currentBoostedPid != 0 && _currentBoostedPid != pid)
                {
                    try { DemoteBoost(_currentBoostedPid); }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }

                // _currentBoostedPid e o PID da janela em foco (usado por Download Boost e
                // ProBalance), mesmo quando ela nao entra no boost (browsers, excecoes).
                _currentBoostedPid = pid;

                if (shouldBoost)
                {
                    // Aplica boost ao novo processo
                    try
                    {
                        // Salva prioridade ORIGINAL antes de boostar — SO NA PRIMEIRA VEZ.
                        // Sem o TryAdd, voltar para uma janela que ficou na faixa sustentada
                        // sobrescreveria a original pela prioridade de boost (High/AboveNormal)
                        // e o processo nunca mais voltaria ao Normal.
                        using var newProc = Process.GetProcessById((int)pid);
                        _originalPriorities.TryAdd(pid, newProc.PriorityClass);

                        PromoteBoost(pid, newProc.ProcessName);
                        ApplyBoostModern(pid, BoostLevel.Foco);
                        SaveCrashRescue();   // fica registado para o caso de crash


                        string logTitle = string.IsNullOrEmpty(windowTitle) ? $"Process {pid}" : windowTitle;
                        KitLugia.Core.Logger.Log($"🎮 GameBoost (Timer): Boost aplicado ao processo PID: {pid} - {logTitle}");
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
            }
            catch { /* Ignora erros silenciosamente */ }
        }


        // ───────────── FAIXA DE PRIORIDADE: promover / rebaixar / decair ─────────────

        /// <summary>
        /// Coloca o processo na faixa "Foco": o motor escolhe os parametros (HIGH, no maximo).
        /// Guarda a prioridade original uma unica vez, para o RevertBoost ter o que restaurar.
        /// </summary>
        private void PromoteBoost(uint pid, string processName)
        {
            if (pid == 0) return;

            var target = _boostTargets.GetOrAdd(pid, _ => new BoostTarget());
            target.Level = BoostLevel.Foco;
            target.Name = processName;
            target.LastWorking = DateTime.UtcNow;
            target.IdleSince = DateTime.MinValue;
            target.SustainedApplied = false;

            EnforceSustainedCap(pid);
        }

        /// <summary>
        /// Rebaixa o processo que saiu do foco.
        ///   - Se a faixa Sustentado esta ligada e o processo existe: fica em AboveNormal com
        ///     I/O e page em Normal, e passa a ser reavaliado a cada tique do monitor.
        ///     O REVERSO e automatico (TickBoostTiers) — nao depende de hook nem de evento.
        ///   - Caso contrario: revertido imediatamente, como antes.
        /// </summary>
        private void DemoteBoost(uint pid)
        {
            if (pid == 0 || pid == Environment.ProcessId) return;

            if (!BoostSustainedEnabled || !_boostTargets.ContainsKey(pid))
            {
                _boostTargets.TryRemove(pid, out _);
                RevertBoost(pid);
                return;
            }

            try
            {
                var target = _boostTargets[pid];
                using var proc = Process.GetProcessById((int)pid);

                target.Level = BoostLevel.Sustentado;
                target.IdleSince = DateTime.MinValue;   // ainda conta como ativo
                target.LastWorking = DateTime.UtcNow;

                if (!target.SustainedApplied)
                {
                    target.SustainedApplied = true;
                    ApplyBoostModern(pid, BoostLevel.Sustentado);
                    KitLugia.Core.Logger.Log(
                        $"🎮 GameBoost: {proc.ProcessName} saiu do foco mas continua ativo -> faixa SUSTENTADA (AboveNormal). " +
                        "Volta a prioridade original sozinho quando ficar ocioso.");
                }

                EnforceSustainedCap(pid);
            }
            catch
            {
                // Processo morreu no meio da troca de janela: nao ha o que rebaixar.
                _boostTargets.TryRemove(pid, out _);
            }
        }

        /// <summary>
        /// Aplica a faixa SUSTENTADA: o processo SAI da prioridade de foco mas mantem uma
        /// prioridade leve. Ela e sempre uma REDUCAO (nunca sobe), e zera o que o motor de
        /// foco tinha apertado (I/O, page priority e GameClassInfo voltam ao padrao), para
        /// que o app em segundo plano nao roube I/O nem memoria de quem esta em foco.
        /// </summary>
        private void ApplySustainedBand(uint pid)
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);

                if (ForegroundBoostEnabled &&
                    proc.PriorityClass == ProcessPriorityClass.High)
                {
                    proc.PriorityClass = ProcessPriorityClass.AboveNormal;
                }

                try { Win32Api.SetProcessIoPriority(proc.Handle, 2); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }  // Normal
                try { Win32Api.SetProcessPagePriority(proc.Handle, 5); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); } // Normal: nao rouba RAM
                try { Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_NORMAL); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { Win32Api.SetProcessGameClassInfo(proc.Handle, false); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { SetEcoQoS(proc.Handle, false); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        /// <summary>
        /// Tique da faixa: mede quem esta trabalhando e devolve a prioridade original do
        /// processo que ficou ocioso. Roda no tick do monitor (thread pool), sem UI.
        /// </summary>
        private void TickBoostTiers()
        {
            if (_boostTargets.IsEmpty) return;

            var now = DateTime.UtcNow;
            foreach (var kv in _boostTargets.ToArray())
            {
                uint pid = kv.Key;
                var target = kv.Value;

                try
                {
                    // Quem esta em foco nao decai (a reavaliacao do motorAuto no passo 3b cuida dele).
                    if (pid == _currentBoostedPid)
                    {
                        target.Level = BoostLevel.Foco;
                        target.IdleSince = DateTime.MinValue;
                        continue;
                    }

                    float cpu = SampleProcessCpuShare(pid);
                    bool trabalhando = cpu >= 0f && cpu > BoostIdleCpuShare;

                    if (trabalhando)
                    {
                        target.LastWorking = now;
                        target.IdleSince = DateTime.MinValue;
                        continue;
                    }

                    if (target.IdleSince == DateTime.MinValue) target.IdleSince = now;

                    if (now - target.IdleSince < BoostIdleGrace) continue;

                    // Ocioso demais: volta a prioridade original e sai da faixa.
                    RevertBoost(pid);
                    _boostTargets.TryRemove(pid, out _);
                    _autoBand.TryRemove(pid, out _);
                    _threadEfficiencyOff.TryRemove(pid, out _);
                    KitLugia.Core.Logger.Log(
                        $"🎮 GameBoost: {(target.Name.Length > 0 ? target.Name : "PID " + pid)} ficou ocioso — prioridade original restaurada.");
                }
                catch
                {
                    // Processo encerrado: limpa o registro sem tocar em nada.
                    _boostTargets.TryRemove(pid, out _);
                    _autoBand.TryRemove(pid, out _);
                    _threadEfficiencyOff.TryRemove(pid, out _);
                }
            }
        }

        /// <summary>
        /// Limita quantos processos ficam na faixa Sustentado: sem teto, alt-tab entre varias
        /// janelas acabaria com "meio sistema" com prioridade acima da normal. Expulsa o mais
        /// antigo primeiro (revertendo de verdade).
        /// </summary>
        private void EnforceSustainedCap(uint keepPid)
        {
            var sustentados = _boostTargets
                .Where(kv => kv.Key != keepPid && kv.Value.Level == BoostLevel.Sustentado)
                .OrderBy(kv => kv.Value.LastWorking)
                .ToList();

            int excedente = sustentados.Count - (MaxSustainedTargets - 1);
            if (excedente <= 0) return;

            foreach (var kv in sustentados.Take(excedente))
            {
                RevertBoost(kv.Key);
                _boostTargets.TryRemove(kv.Key, out _);
                KitLugia.Core.Logger.Log(
                    $"🎮 GameBoost: {kv.Value.Name} saiu da faixa sustentada (limite de {MaxSustainedTargets} processos).");
            }
        }

        /// <summary>
        /// Reverte TODOS os processos em boost (usado ao desligar o toggle, no shutdown do
        /// GameBoost e no botao "restaurar"). Antes so voltava o PID em foco, e os demais
        /// ficavam com prioridade alterada para sempre.
        /// </summary>
        public void RevertAllBoostTargets()
        {
            foreach (var pid in _boostTargets.Keys.ToList())
            {
                try { RevertBoost(pid); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                _boostTargets.TryRemove(pid, out _);
            }
            _originalPriorities.Clear();
            _currentBoostedPid = 0;

            // Estado derivado do motor Automatico: sem isto os dicionarios viviam para
            // sempre com os pids que o dia todo tiveram boost (o _autoCpuSamples ja tinha
            // um limite de 64, estes nao tinham nenhum).
            _autoBand.Clear();
            _threadEfficiencyOff.Clear();
            // Reescreve o ficheiro de resgate com o estado agora (vazio) → apaga-o.
            SaveCrashRescue();

            // Nenhum processo mais em boost -> o tweak GLOBAL de rede tem que voltar ao
            // valor original do Windows. Sem isto o SystemResponsiveness=10 sobrevivia ao
            // boost e derrubava a perceived performance da máquina inteira.
            RevertNetworkBoost();
        }


        private bool ShouldBoostProcess(uint pid, IntPtr hwnd)
        {
            if (pid == 0) return false;

            // NUNCA fazer boost em si mesmo
            if (pid == Environment.ProcessId) return false;

            try
            {
                using var proc = Process.GetProcessById((int)pid);
                string procName = proc.ProcessName.ToLower();

                if (_protectedProcesses.Contains(procName))
                    return false;

                return true;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }


        // REMOVIDO (05/10/2026): IsFullScreen — zero chamadas (verificado por grep). O motor
        // nao deve tratar "janela grande" como "jogo": um video do YouTube em tela cheia
        // tem o mesmo formato e receberia prioridade diferente de um video pequeno.


        private void ApplyBoostModern(uint pid, BoostLevel level = BoostLevel.Foco)
        {
            // NUNCA fazer boost em si mesmo
            if (pid == 0 || pid == Environment.ProcessId) return;

            try
            {
                // Faixa Sustentado: nao roda o motor (nada de timer/rede/throttling). E so uma
                // prioridade leve e a volta dos parametros de I/O/pagina ao padrao.
                if (level == BoostLevel.Sustentado)
                {
                    ApplySustainedBand(pid);
                    return;
                }

                if (_customEngineConfig != null)
                {
                    ApplyBoostCustom(pid, _customEngineConfig);
                    return;
                }

                // Chama o motor selecionado pelo usuário
                switch (_currentEngine)
                {
                    case GameBoostEngine.Auto:
                        ApplyBoostAuto(pid);
                        break;
                    case GameBoostEngine.V1_Balanced:
                        ApplyBoostV1(pid);
                        break;
                    case GameBoostEngine.V2_StableFPS:
                        ApplyBoostV2(pid);
                        break;
                    case GameBoostEngine.V3_Extreme:
                        ApplyBoostV3(pid);
                        break;
                    case GameBoostEngine.V4_ExtremePro:
                        ApplyBoostV4(pid);
                        break;
                    default:
                        ApplyBoostAuto(pid);
                        break;
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }


        private bool ElevateToSystem()
        {
            try
            {
                // Obtém token do processo System (PID 4 - kernel/ntoskrnl)
                IntPtr systemProcess = Win32Api.OpenProcess(Win32Api.PROCESS_QUERY_INFORMATION, false, 4);
                if (systemProcess == IntPtr.Zero)
                {
                    // Fallback: tenta lsass.exe (Local Security Authority)
                    using (var lsass = Process.GetProcessesByName("lsass").FirstOrDefault())
                    {
                        if (lsass != null)
                            systemProcess = Win32Api.OpenProcess(Win32Api.PROCESS_QUERY_INFORMATION, false, lsass.Id);
                    }
                }

                if (systemProcess == IntPtr.Zero) return false;

                try
                {
                    // Abre token do processo System
                    if (!Win32Api.OpenProcessToken(systemProcess, 
                        Win32Api.TOKEN_DUPLICATE | Win32Api.TOKEN_IMPERSONATE | Win32Api.TOKEN_QUERY, 
                        out IntPtr systemToken))
                        return false;

                    try
                    {
                        // Duplica token para impersonação
                        if (!Win32Api.DuplicateTokenEx(systemToken, 
                            0x1F0FFF, // MAXIMUM_ALLOWED
                            IntPtr.Zero, 
                            Win32Api.SecurityImpersonation, 
                            Win32Api.TokenImpersonation, 
                            out IntPtr impersonationToken))
                            return false;

                        try
                        {
                            // Aplica token à thread atual
                            if (Win32Api.SetThreadToken(Win32Api.GetCurrentThread(), impersonationToken))
                            {
                                KitLugia.Core.Logger.Log("🔐 Privilégios elevados para System - Acesso a processos protegidos habilitado");
                                return true;
                            }
                        }
                        finally
                        {
                            Win32Api.CloseHandle(impersonationToken);
                        }
                    }
                    finally
                    {
                        Win32Api.CloseHandle(systemToken);
                    }
                }
                finally
                {
                    Win32Api.CloseHandle(systemProcess);
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ Falha ao elevar privilégios: {ex.Message}");
            }
            return false;
        }

        private void ApplyBoostCustom(uint pid, CustomEngineConfig config)
        {
            if (pid == 0) return;

            // GLOBAL TIMER BOOST (não precisa de handle)
            if (config.TimerBoost)
                Win32Api.BoostTimerResolution();

            // GLOBAL NETWORK BOOST (não precisa de handle)
            if (config.NetworkBoost)
                ApplyNetworkBoostV3();

            // DOWNLOAD BOOST — amostra tráfego e aplica aos downloaders reais
            if (config.DownloadBoostEnabled)
            {
                try
                {
                    var trafficSnapshot = NetworkTrafficMonitor.SampleTraffic(pid);
                    var level = config.DownloadBoostLevel switch
                    {
                        "Download" => DownloadBoostEngine.BoostLevel.Download,
                        "Latency" => DownloadBoostEngine.BoostLevel.Latency,
                        "Balanced" => DownloadBoostEngine.BoostLevel.Balanced,
                        _ => DownloadBoostEngine.BoostLevel.Auto
                    };

                    var dlConfig = new DownloadBoostEngine.DownloadBoostConfig
                    {
                        Enabled = true,
                        Level = level,
                        AutoThresholdMBps = 5.0,
                        ForegroundPid = pid
                    };

                    var targets = level == DownloadBoostEngine.BoostLevel.Auto
                        ? DownloadBoostEngine.AutoDecide(trafficSnapshot, dlConfig)
                        : NetworkTrafficMonitor.GetActiveDownloaders(trafficSnapshot, 5.0)
                            .Select(p => p.Pid).ToList();

                    foreach (var t in targets)
                        DownloadBoostEngine.Apply(dlConfig, t);
                }
                catch (Exception ex)
                {
                    KitLugia.Core.Logger.Log($"⚠️ Download Boost (ApplyBoostCustom): {ex.Message}");
                }
            }

            // GLOBAL Win32PrioritySeparation (não precisa de handle)
            if (config.Win32PrioritySeparation)
                Win32Api.SetWin32PrioritySeparation(true);

            // GLOBAL ThreadEfficiencyMode via pid (não precisa de handle do processo)
            //
            // CUSTO (05/10/2026): esta chamada ABRE e FECHA um handle por thread do
            // processo. Com o motor Automatico, que reaplica a cada troca de janela e a
            // cada ~15 s, isso significava enumerar + abrir TODAS as threads do jogo
            // varias vezes por minuto, para um ajuste que e' estatico enquanto o
            // processo viver. Agora corre UMA VEZ por pid; o RevertBoost (uma vez por
            // processo, na saida) e' que volta ao valor anterior.
            if (config.ThreadEfficiencyMode == false && !_threadEfficiencyOff.ContainsKey(pid))
            {
                CheckPcoreBenefit(pid);
                Win32Api.SetThreadEfficiencyForAllThreads(pid, false);
                _threadEfficiencyOff.TryAdd(pid, true);
            }

            // PROCESS-LEVEL: Prioridade, I/O, Page, GameClassInfo, EcoQoS
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                string name = proc.ProcessName;

                KitLugia.Core.Logger.Log($"⚡ GameBoost [PERSONALIZADO]: {name} (PID: {pid}) aplicando configurações...");

                // SEGURANÇA (03/10/2026): REALTIME foi PROIBIDO globalmente.
                // REALTIME_PRIORITY_CLASS em processo de usuario pode impedir o scheduler de rodar
                // threads do kernel/DPC → congelamento, tela preta e watchdog/BSOD. O teto e HIGH.
                // O log de aviso existe porque alguma config salva do usuario ainda pede "realtime".
                bool pediuRealtime = config.CpuPriority.Equals("realtime", StringComparison.OrdinalIgnoreCase);
                if (pediuRealtime)
                    KitLugia.Core.Logger.Log(
                        $"⚠️ Boost: '{name}' pediu REALTIME — bloqueado por segurança, usando HIGH. " +
                        "(RealTime em processo de usuario pode congelar o sistema.)");

                var targetPriority = config.CpuPriority.ToLower() switch
                {
                    "normal" => ProcessPriorityClass.Normal,
                    "high" => ProcessPriorityClass.High,
                    "realtime" => ProcessPriorityClass.High,   // NUNCA RealTime (travas/BSOD)
                    _ => ProcessPriorityClass.High
                };

                bool elevated = false;
                try
                {
                    if (ForegroundBoostEnabled)
                    {
                        // Sem o ramo de REALTIME: targetPriority nunca e RealTime.
                        if (proc.PriorityClass != targetPriority)
                            proc.PriorityClass = targetPriority;
                    }
                }
                catch (System.ComponentModel.Win32Exception) when (!elevated)
                {
                    KitLugia.Core.Logger.Log($"🔒 Acesso negado ao processo {name} - tentando elevar privilégios...");
                    if (ElevateToSystem())
                    {
                        elevated = true;
                        try
                        {
                            if (ForegroundBoostEnabled)
                            {
                                proc.PriorityClass = targetPriority;
                                KitLugia.Core.Logger.Log($"✅ Prioridade aplicada com privilégios elevados: {name}");
                            }
                        }
                        catch { KitLugia.Core.Logger.Log($"⚠️ Mesmo elevado, falha ao alterar {name} (PPL bloqueia)"); }
                    }
                }

                try { Win32Api.SetProcessIoPriority(proc.Handle, SafeIoPriority(config.IoPriorityLevel)); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { Win32Api.SetProcessPagePriority(proc.Handle, config.PagePriorityLevel == 0 ? 5 : config.PagePriorityLevel); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { Win32Api.SetThreadMemoryPriority(proc.Handle, (uint)(config.ThreadMemoryPriority == 0 ? 5 : config.ThreadMemoryPriority)); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { SetEcoQoS(proc.Handle, config.EcoQoSEnabled); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                if (config.GameClassInfo)
                {
                    try { Win32Api.SetProcessGameClassInfo(proc.Handle, true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }

                KitLugia.Core.Logger.Log($"✅ GameBoost [PERSONALIZADO]: {name} otimizado com sucesso!");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private static void CheckPcoreBenefit(uint pid)
        {
            int onECores = Win32Api.GetThreadCountOnECores(pid);
            System.Diagnostics.Debug.WriteLine($"[GameBoost] P-Cores Only PID {pid}: {onECores} thread(s) em E-cores - {(onECores > 0 ? "ben\u00E9fico" : "n\u00E3o ben\u00E9fico")}");
        }


        private void ApplyBoostV1(uint pid)
        {
            if (pid == 0) return;

            // GLOBAL: Win32PrioritySeparation (não precisa de handle do processo)
            Win32Api.SetWin32PrioritySeparation(true);

            CheckPcoreBenefit(pid);
            Win32Api.SetThreadEfficiencyForAllThreads(pid, false);

            // PROCESS-LEVEL: Prioridade, I/O, Page, Memory
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                string name = proc.ProcessName;

                try
                {
                    if (ForegroundBoostEnabled)
                    {
                        if (proc.PriorityClass != ProcessPriorityClass.High &&
                            proc.PriorityClass != ProcessPriorityClass.RealTime)
                        {
                            proc.PriorityClass = ProcessPriorityClass.High;
                        }
                    }
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
                {
                    KitLugia.Core.Logger.Log($"⚠️ V1: Acesso negado à prioridade do processo {name} (PID: {pid}) - processo protegido");
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                try { Win32Api.SetProcessIoPriority(proc.Handle, 3); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { Win32Api.SetProcessPagePriority(proc.Handle, 5); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_NORMAL); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }


        private void ApplyBoostV2(uint pid)
        {
            if (pid == 0) return;

            // GLOBAL: Scheduler + P-Cores (não precisa de handle do processo)
            Win32Api.SetWin32PrioritySeparation(true);
            CheckPcoreBenefit(pid);
            Win32Api.SetThreadEfficiencyForAllThreads(pid, false);

            // PROCESS-LEVEL: GameClassInfo, Prioridade, I/O, Page, EcoQoS
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                string name = proc.ProcessName;

                Win32Api.SetProcessGameClassInfo(proc.Handle, true);

                try
                {
                    if (ForegroundBoostEnabled)
                    {
                        if (proc.PriorityClass != ProcessPriorityClass.High &&
                            proc.PriorityClass != ProcessPriorityClass.RealTime)
                        {
                            proc.PriorityClass = ProcessPriorityClass.High;
                        }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                try { Win32Api.SetProcessIoPriority(proc.Handle, 3); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { Win32Api.SetProcessPagePriority(proc.Handle, 5); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_NORMAL); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { SetEcoQoS(proc.Handle, false); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                ApplyProBalanceV2(pid);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }


        private void ApplyBoostV3(uint pid)
        {
            if (pid == 0) return;

            // GLOBAL: Scheduler + P-Cores + Timer + Network (não precisa de handle)
            Win32Api.SetWin32PrioritySeparation(true);
            CheckPcoreBenefit(pid);
            Win32Api.SetThreadEfficiencyForAllThreads(pid, false);
            Win32Api.BoostTimerResolution();
            ApplyNetworkBoostV3();

            KitLugia.Core.Logger.Log($"⚡ GameBoost V3 [Extremo]: PID {pid} com boosts globais ativos");

            // PROCESS-LEVEL: GameClassInfo, Prioridade, I/O, Page, EcoQoS
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                string name = proc.ProcessName;

                Win32Api.SetProcessGameClassInfo(proc.Handle, true);

                try
                {
                    if (ForegroundBoostEnabled)
                    {
                        if (proc.PriorityClass != ProcessPriorityClass.High &&
                            proc.PriorityClass != ProcessPriorityClass.RealTime)
                        {
                            proc.PriorityClass = ProcessPriorityClass.High;
                        }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                try { Win32Api.SetProcessIoPriority(proc.Handle, 3); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { Win32Api.SetProcessPagePriority(proc.Handle, 5); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_NORMAL); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { SetEcoQoS(proc.Handle, false); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                ApplyProBalanceV3(pid);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }


        private void ApplyBoostV4(uint pid)
        {
            // SEGURANÇA (03/10/2026): o V4 usava CpuPriority="realtime" e IoPriorityLevel=1,
            // que viravam REALTIME_PRIORITY_CLASS e IoPriorityCritical. Isso NAO é ganho de FPS —
            // é starvation: um thread REALTIME pode impedir o scheduler de rodar threads do
            // kernel/DPC, o I/O Crítico faz o pagefile e o log do Windows esperarem na fila, e o
            // resultado observado pelo usuario foi travada, tela preta e BSOD.
            //
            // O ganho de loading (Poppy Playtime Ch.4) vinha de GameClassInfo + page priority
            // moderada + timer boost + network boost — não da REALTIME.
            //
            // MEDIDO (harness gbknobs): a classe 13 (Game Mode) é RECUSADA pelo
            // Windows neste build (err 87 em todos os tamanhos), e a classe 5
            // (Thread Efficiency) também. E o timer: o Windows não desce abaixo
            // de 0,50 ms, e o motor pede 1 ms. Portanto, neste build, o que
            // sobra de tudo isto é a page priority — que foi provada a
            // aplicar (leitura de volta). Manter os ajustes; o aviso de classe
            // recusada sai uma vez e o resto do motor segue normal.
            var v4Config = new CustomEngineConfig
            {
                CpuPriority = "high",        // ERA "realtime" -> causava starvation do scheduler
                IoPriorityLevel = 3,          // ERA 1 (= I/O Crítico na fila do disco)
                PagePriorityLevel = 4,        // ERA 2 (very high: roubava RAM dos outros processos)
                TimerBoost = true,
                EcoQoSEnabled = false,      // DISABLED: EcoQoS = power saving, NAO performance!
                ProBalance = false,
                NetworkBoost = true,
                ThreadMemoryPriority = 5,     // NORMAL explicito (antes "0" virava 5 no Custom)
                ThreadEfficiencyMode = false,
                GameClassInfo = true,
                Win32PrioritySeparation = true
            };
            ApplyBoostCustom(pid, v4Config);
        }

        /// <summary>
        /// MOTOR ÚNICO AUTOMÁTICO (03/10/2026) — padrão do kit.
        ///
        /// POR QUE: o usuario usava V4 quase sempre "porque no loading ficava mais rápido", mas
        /// o V4 era o que trazia travada/tela preta/BSOD. A solução não é escolher entre os
        /// motores: é um motor que ESCOLHE os parametros certos conforme a cena, e que nunca
        /// entra em território que derruba o sistema.
        ///
        /// REGRAS DE OURO (todas deliberadamente conservadoras):
        ///   - NUNCA REALTIME. teto é HIGH.
        ///   - NUNCA I/O Crítico (4). teto é HIGH (3).
        ///   - NUNCA page priority abaixo do NORMAL (5). 4 = BELOW_NORMAL: rouba RAM do
        ///     resto do sistema. A escala oficial MEMORY_PRIORITY_INFORMATION e' 1=VERY_LOW
        ///     .. 5=NORMAL e 5 e' o DEFAULT — a v1 usava 4 no foco, ou seja, abaixo do padrao.
        ///   - Thread memory priority sempre NORMAL (VERY_LOW + working set = page fault storm).
        ///
        /// AUTOMATICO v2 (05/10/2026) — o que a pesquisa (docs/GAMEBOOST_ENGINE_RESEARCH.md)
        /// mudou em relacao a v1. O V1/V2/V3/V4 NAO foram tocados.
        ///   (a) PAGE PRIORITY sempre 5. Na v1 o foco usava 4 (= BELOW_NORMAL), que na escala
        ///       real e' ABAIXO do default — o motor Automatico estava a REBAIXAR a memoria do
        ///       jogo exatamente quando ele devia ser o mais protegido. Os rotulos da UI
        ///       diziam "Maximum (5)" com o valor 4 ligado: inverted.
        ///   (b) TIMER GLOBAL fora do caminho Automatico. BoostTimerResolution() roda no
        ///       PROCESSO DO KIT; desde o Win10 2004 o pedido de timer e' por processo, logo
        ///       nao acelera o timer do jogo. O certo e' limpar
        ///       PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION no jogo (honrar o pedido
        ///       que o JOGO faz) — feito em SetPowerThrottling.
        ///   (c) Win32PrioritySeparation fora do caminho Automatico. Alem de ser um global
        ///       em HKLM, o revert antigo gravava 2 sem ler o original. Agora o original e'
        ///       guardado e devolvido, e o Automatico simplesmente nao mexe nele.
        ///   (d) ProBalance COM GATE DE CARGA — a metade que faltava (ver ApplyAutoProBalance).
        /// </summary>
        private void ApplyBoostAuto(uint pid)
        {
            if (pid == 0) return;

            // Carga do jogo: define o quanto vale apertar.
            // CPU do PROCESSO medida por DELTA entre dois ticks (fracao de 0..1 do total).
            // BUG CORRIGIDO (03/10/2026): a versao anterior dividia TotalProcessorTime pelo
            // tempo de vida do processo — ou seja, a MEDIA HISTORICA. Numa sessao longa com
            // jogo leve no meio, essa media nunca chega a 0.75 e o "modo loading" nunca
            // acionava: o motor Automatico nao fazia o que dizia fazer.
            float cpuPct = SampleProcessCpuShare(pid);
            bool temAmostra = cpuPct >= 0f;
            if (!temAmostra) cpuPct = 0.35f; // sem amostra ainda: valor NEUTRO, sem aperto

            // Jogo em foreground? (o que decide se o resto do sistema pode ceder)
            bool foreground = false;
            try
            {
                IntPtr fg = Win32Api.GetForegroundWindow();
                uint fgPid = 0;
                if (fg != IntPtr.Zero) { Win32Api.GetWindowThreadProcessId(fg, out fgPid); foreground = fgPid == pid; }
            }
            catch { }

            bool carregando = cpuPct > 0.75f;          // fase de loading: CPU alta e sustentada

            // HISTERESE: a cena decide A FAIXA, nao cada parametro. Trocar de faixa a cada
            // tick fazia o motor reescrever I/O/pagina sem parar (e cada reescrita custa uma
            // serie de chamadas ao kernel). A faixa so muda quando o CPU cruza um limiar.
            var faixa = carregando ? AutoBand.Loading : AutoBand.Idle;
            bool mudouDeFaixa = !_autoBand.TryGetValue(pid, out var faixaAtual) || faixaAtual != faixa;
            _autoBand[pid] = faixa;

            // Perfil escolhido pela cena — sempre dentro dos limites seguros acima.
            var cfg = new CustomEngineConfig
            {
                CpuPriority = "high",                                   // HIGH e' o teto
                IoPriorityLevel = carregando ? 3 : 3,                   // High: o jogo em foco pede I/O
                PagePriorityLevel = 5,                                  // NORMAL = default (item a)
                TimerBoost = false,                                     // nada global (item b)
                NetworkBoost = false,                                   // global e revertido no RevertBoost
                EcoQoSEnabled = false,                                  // EcoQoS e' power saving
                ProBalance = true,                                      // COM GATE DE CARGA (item d)
                ThreadMemoryPriority = 5,                               // SEMPRE NORMAL
                ThreadEfficiencyMode = false,
                GameClassInfo = true,                                   // diz ao Windows que e' jogo
                Win32PrioritySeparation = false,                        // nada global (item c)
                DownloadBoostEnabled = false
            };

            KitLugia.Core.Logger.Log(
                $"🤖 GameBoost AUTO: fg={foreground} cpu={cpuPct:P0} amostra={temAmostra} faixa={faixa}" +
                $"{(mudouDeFaixa ? " (mudou)" : "")} -> IO={cfg.IoPriorityLevel} Page={cfg.PagePriorityLevel}");

            ApplyBoostCustom(pid, cfg);

            // ProBalance com gate de carga do sistema: roda no MESMO caminho do boost, para
            // nao depender do ProBalanceTimerTick (que so dispara com o toggle ProBalance).
            if (ProBalance) ApplyAutoProBalance(pid);
        }

        private enum AutoBand { Idle = 0, Loading = 1 }
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, AutoBand> _autoBand = new();

        /// <summary>
        /// Pids que ja tiveram ThreadEfficiencyMode desligado (so para garantir P-cores).
        /// Evita re-enumerar todas as threads do processo em cada reaplicacao.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, bool> _threadEfficiencyOff = new();

        /// <summary>
        /// ProBalance do motor Automatico (v2): DEMOVER O FUNDO, mas SO SOB CARGA REAL.
        ///
        /// A logica do Process Lasso (Bitsum) e' esta: em vez de subir o jogo para
        /// High/RealTime, ele BAIXA os processos de fundo e age apenas quando a maquina
        /// esta carregada. Tres condicoes, todas necessarias:
        ///   1. o processo nao e o foreground nem esta protegido;
        ///   2. a CARGA TOTAL do sistema > AutoProBalanceLoadGate (70%);
        ///   3. o processo passou do limiar de CPU em amostras consecutivas + cooldown.
        ///
        /// Sem a condicao 2 o motor rebaixava ate um render legitimo numa maquina ociosa
        /// (o V2/V3 decisiono so pelo CPU do processo, sem olhar a maquina).
        /// </summary>
        private void ApplyAutoProBalance(uint foregroundPid)
        {
            if (!ForegroundBoostEnabled) return;

            int carga = Win32Api.GetSystemCpuLoad();
            ConditionalLog.Try("ApplyAutoProBalance", () =>
            {
                lock (_throttleLock)
                {
                    var now = DateTime.UtcNow;

                    // Restaura quem saiu do limiar / do cooldown / virou protegido.
                    var toRestore = _throttledProcesses.Where(p => p != foregroundPid).ToList();
                    foreach (var pid in toRestore)
                        RestoreThrottled(pid, "Auto");

                    if (carga < AutoProBalanceLoadGate)
                    {
                        // Maquina folgada: NAO se mexe em ninguem. E' a diferenca para V2/V3.
                        ConditionalLog.Try("AutoProBalanceLoad",
                            () => KitLugia.Core.Logger.Log(
                                $"⚖️ GameBoost AUTO ProBalance: carga {carga}% < {AutoProBalanceLoadGate}% — maquina folgada, nao rebaixa ninguem"));
                        return;
                    }

                    foreach (var proc in GetCachedProcesses())
                    {
                        try
                        {
                            uint pid = (uint)proc.Id;
                            if (pid == foregroundPid || pid == (uint)Environment.ProcessId) continue;
                            if (_throttledProcesses.Contains(pid)) continue;

                            string name = proc.ProcessName.ToLower();
                            if (_protectedProcesses.Contains(name))
                            {
                                _proBalanceConsecutive.Remove(pid);
                                _proBalanceCooldowns.Remove(pid);
                                continue;
                            }

                            if (_proBalanceCooldowns.TryGetValue(pid, out var cooldownEnd) && now < cooldownEnd)
                                continue;

                            // Nao briga com o Firemin (mesmo teste do ProBalance existente).
                            if (_processProfiles.TryGetValue(name, out var prof) &&
                                (DateTime.Now - prof.LastTrimTime).TotalSeconds < 15)
                                continue;

                            double cpuUsage = GetProcessCpuUsage(proc);
                            if (cpuUsage > AutoProBalanceCpuThreshold)
                            {
                                _proBalanceConsecutive.TryGetValue(pid, out int count);
                                count++;
                                _proBalanceConsecutive[pid] = count;

                                if (count >= ProBalanceSamplesRequired && proc.PriorityClass >= ProcessPriorityClass.Normal)
                                {
                                    proc.PriorityClass = ProcessPriorityClass.BelowNormal;
                                    _throttledProcesses.Add(pid);
                                    _proBalanceConsecutive.Remove(pid);
                                    _proBalanceCooldowns[pid] = now.AddSeconds(ProBalanceCooldownSec);

                                    // So a memory priority E' retirada de proposito: e' o que
                                    // faz o working set do processo de fundo ser o primeiro a
                                    // ser trimado quando falta RAM.
                                    ConditionalLog.Try("AutoProBalanceMem",
                                        () => Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_VERY_LOW));

                                    KitLugia.Core.Logger.Log(
                                        $"🔻 ProBalance Auto: {name} (PID: {pid}) rebaixado para BelowNormal " +
                                        $"(carga {carga}%, CPU {cpuUsage:F1}% > {AutoProBalanceCpuThreshold}% apos {count} amostras)");
                                    SaveCrashRescue();   // crash agora = este processo ficaria BelowNormal
                                }
                            }
                            else
                            {
                                _proBalanceConsecutive.Remove(pid);
                                _proBalanceCooldowns.Remove(pid);
                            }
                        }
                        catch (Exception ex) { ConditionalLog.LogOnce("AutoProBalanceThrottle", ex); }
                        finally { proc.Dispose(); }
                    }
                }
            });
        }

        /// <summary>
        /// Restaura um processo rebaixado pelo ProBalance (Normal + memory priority NORMAL).
        /// Extraido para o Auto e o V2/V3 partilharem exatamente a mesma rotina de saida.
        /// </summary>
        private void RestoreThrottled(uint pid, string version)
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                string name = proc.ProcessName.ToLower();

                if (_protectedProcesses.Contains(name))
                {
                    _throttledProcesses.Remove(pid);
                    _proBalanceConsecutive.Remove(pid);
                    _proBalanceCooldowns.Remove(pid);
                    return;
                }

                if (proc.PriorityClass == ProcessPriorityClass.BelowNormal)
                {
                    proc.PriorityClass = ProcessPriorityClass.Normal;
                    ConditionalLog.Try("ProBalanceRestore",
                        () => Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_NORMAL));
                    KitLugia.Core.Logger.Log($"🔼 ProBalance {version}: {name} (PID: {pid}) restaurado para Normal");
                }
                _throttledProcesses.Remove(pid);
                _proBalanceConsecutive.Remove(pid);
                _proBalanceCooldowns.Remove(pid);
            }
            catch (Exception ex)
            {
                _throttledProcesses.Remove(pid);
                _proBalanceConsecutive.Remove(pid);
                _proBalanceCooldowns.Remove(pid);
                ConditionalLog.LogOnce("ProBalanceRestoreFail", ex);
            }
        }

        /// <summary>Carga total do sistema (%) acima da qual o ProBalance do Auto age. Bitsum usa ~alta carga.</summary>
        private const int AutoProBalanceLoadGate = 70;
        /// <summary>CPU do processo (%) a partir da qual conta como candidato.</summary>
        private const double AutoProBalanceCpuThreshold = 8.0;

        // =========================================================
        // Traducao dos indices do ComboBox para a escala real
        // =========================================================
        //
        // BUG CORRIGIDO (05/10/2026): a UI guardava/aplicava o INDICE do ComboBox como se
        // fosse o valor da escala. O item "Maximum (minimo swap, maxima performance)" estava
        // no indice 1, e 1 na escala oficial MEMORY_PRIORITY_INFORMATION e' VERY_LOW — ou
        // seja, o perfil "Maximum" aplicava a PIOR prioridade de memoria possivel a um app
        // escolhido justamente para nao fazer swap.
        //
        // A escala e' 1=VERY_LOW .. 5=NORMAL(default). NAO ha "maximum": o 5 e' o topo E
        // o padrao do Windows, por isso a rotula antiga prometia algo que a API nao tem.
        // Os perfis ja gravados em disco mantem o indice (0 ou 1) — a traducao e' feita
        // sempre na fronteira, entao um perfil antigo continua a ler como "Normal".
        public static int PagePriorityFromIndex(int index) => index >= 1 ? 4 : 5;   // 0=Normal, 1=Below Normal
        public static int ThreadMemoryPriorityFromIndex(int index) => index >= 1 ? 1 : 5; // 0=Normal, 1=Very Low

        /// <summary>
        /// Fracao de CPU do PROCESSO (0..1) medida por DELTA entre duas amostras consecutivas.
        /// Retorna -1 quando ainda nao ha amostra anterior para comparar.
        ///
        /// Por que delta e nao media: a media desde o inicio do processo nao serve para detectar
        /// "loading" — uma sessao de 3 h de jogo leve dilui qualquer pico. O delta entre dois
        /// ticks do monitor (~1 s) mostra o que o processo esta fazendo AGORA, que e a unica
        /// coisa que o motor Automatico precisa decidir.
        /// </summary>
        private static readonly Dictionary<uint, (DateTime At, TimeSpan Cpu)> _autoCpuSamples = new();
        private static readonly object _autoCpuLock = new();

        private static float SampleProcessCpuShare(uint pid)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                DateTime now = DateTime.UtcNow;
                TimeSpan cpu = p.TotalProcessorTime;

                lock (_autoCpuLock)
                {
                    // Sem amostra anterior: guarda esta e nao inventa numero.
                    if (!_autoCpuSamples.TryGetValue(pid, out var prev))
                    {
                        _autoCpuSamples[pid] = (now, cpu);
                        if (_autoCpuSamples.Count > 64) _autoCpuSamples.Clear(); // nao cresce sem limite
                        return -1f;
                    }

                    _autoCpuSamples[pid] = (now, cpu);
                    double dtMs = (now - prev.At).TotalMilliseconds;
                    double dCpuMs = cpu.TotalMilliseconds - prev.Cpu.TotalMilliseconds;
                    if (dtMs < 200) return -1f;   // janela curta demais para ser confiavel

                    double share = dCpuMs / dtMs / Math.Max(1, Environment.ProcessorCount);
                    return (float)Math.Clamp(share, 0.0, 1.0);
                }
            }
            catch { return -1f; }   // processo morreu / sem permissao
        }


        // Estado do tweak GLOBAL de rede. Estes valores vivem em HKLM e afetam TODOS os
        // threads do sistema (SystemProfile) — se ficarem gravados depois que o GameBoost
        // acaba, o sintoma é o PC "travando/tela preta" mesmo com o Kit fechado.
        // Guardamos o valor ORIGINAL (antes do primeiro boost) para devolver exatamente o
        // que o Windows tinha, e um flag para saber se somos nós que precisamos restaurar.
        private static readonly object _networkBoostLock = new();
        private static bool _networkBoostApplied;
        private static object? _origNetworkThrottlingIndex;   // null = valor não existia
        private static object? _origSystemResponsiveness;

        /// <summary>
        /// Aplica o tweak global de rede e guarda o valor ORIGINAL para poder reverter.
        ///
        /// BUG CORRIGIDO (04/10/2026): a versão anterior gravava NetworkThrottlingIndex=10 e
        /// SystemResponsiveness=10 no HKLM e NUNCA restaurava. Como o SystemProfile é o
        /// perfil que o Windows usa para threads de sistema, manter SystemResponsiveness=10
        /// (padrão 20) depois do boost CAUSA a perceived "travada/tela preta" que o usuário
        /// reclamou — e sobrevivia ao desligamento do GameBoost e ao reboot do app.
        ///
        /// Também virou idempotente: antes, TODO ApplyBoostV3/V4/custom reescrevia as duas
        /// chaves no registro a cada troca de janela (dezenas de escritas por minuto).
        /// </summary>
        private void ApplyNetworkBoostV3()
        {
            lock (_networkBoostLock)
            {
                // Já aplicado por nós: nada a fazer (e não suja o log a cada boost).
                if (_networkBoostApplied) return;

                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile");
                    if (key == null) return;

                    // Guarda o que o Windows tinha ANTES do nosso tweak (1a vez só).
                    _origNetworkThrottlingIndex = key.GetValue("NetworkThrottlingIndex");
                    _origSystemResponsiveness = key.GetValue("SystemResponsiveness");

                    // NetworkThrottlingIndex: padrão do Windows = 10 (0xFFFFFFFF desabilita).
                    // SystemResponsiveness: padrão = 20 (menor = mais CPU p/ threads do sistema).
                    key.SetValue("NetworkThrottlingIndex", 10, Microsoft.Win32.RegistryValueKind.DWord);
                    key.SetValue("SystemResponsiveness", 10, Microsoft.Win32.RegistryValueKind.DWord);

                    _networkBoostApplied = true;
                    KitLugia.Core.Logger.Log("🌐 GameBoost V3: Network priorizado (throttling reduzido) — valor original guardado para reversão.");
                }
                catch (Exception ex)
                {
                    KitLugia.Core.Logger.LogWarning("ApplyNetworkBoostV3", ex.Message);
                }
            }
        }

        /// <summary>
        /// Devolve SystemResponsiveness/NetworkThrottlingIndex ao valor ORIGINAL do Windows.
        /// Chamado quando o último processo sai da faixa de boost, quando o GameBoost é
        /// desligado e no Dispose — para o tweak nunca "vazar" para a sessão seguinte.
        /// </summary>
        private void RevertNetworkBoost()
        {
            lock (_networkBoostLock)
            {
                if (!_networkBoostApplied) return;

                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", true);
                    if (key != null)
                    {
                        // Valor original ausente = o Windows nunca tinha a chave -> apaga a nossa.
                        if (_origSystemResponsiveness == null) key.DeleteValue("SystemResponsiveness", false);
                        else key.SetValue("SystemResponsiveness", _origSystemResponsiveness);

                        if (_origNetworkThrottlingIndex == null) key.DeleteValue("NetworkThrottlingIndex", false);
                        else key.SetValue("NetworkThrottlingIndex", _origNetworkThrottlingIndex);
                    }

                    _networkBoostApplied = false;
                    _origNetworkThrottlingIndex = null;
                    _origSystemResponsiveness = null;
                    KitLugia.Core.Logger.Log("🌐 GameBoost: tweak global de rede revertido ao valor original do Windows.");
                }
                catch (Exception ex)
                {
                    KitLugia.Core.Logger.LogWarning("RevertNetworkBoost", ex.Message);
                }
            }
        }

        /// <summary>
        /// RESGATE DE CRASH: se o Kit foi morto (taskkill, BSOD, queda de energia) COM o
        /// tweak de rede aplicado, o registro ficou com SystemResponsiveness=10 para sempre.
        /// No start seguinte, se o GameBoost está DESLIGADO e o valor 10 está gravado
        /// (sem termos menor isso ourselves ainda), devolvemos o padrão do Windows (20).
        /// Sem isso a "tela preta" do usuário continuaria mesmo com o Kit fechado.
        /// </summary>
        private static void RescueNetworkBoostOnStartup()
        {
            try
            {
                const string path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path, true);
                if (key == null) return;

                if (key.GetValue("SystemResponsiveness") is not int resp || resp != 10) return;

                // 10 é exatamente o que o ApplyNetworkBoostV3 grava. Devolvemos o padrão do
                // Windows (20), porque ninguém mais o vai fazer.
                //
                // BUG CORRIGIDO (10/10/2026): aqui havia um gate `if (!GamePriorityEnabled)`
                // que parecia proteger o boost ativo — e criava um prejuízo PERMANENTE.
                // Cenário real:
                //   1. o Kit morre com o boost de rede aplicado -> SystemResponsiveness = 10
                //   2. o utilizador abre o Kit COM o GameBoost ligado
                //   3. o gate impedia o resgate
                //   4. o ApplyNetworkBoostV3 corria e gravava _origSystemResponsiveness = 10
                //      (o valor ÓRFÃO, não o original do Windows)
                //   5. daí em diante cada revert "restaurava" 10 -> o 20 perdia-se para sempre
                //
                // O gate não protegia nada: a esta altura do arranque NADA foi escrito por
                // este processo (_networkBoostApplied é false), portanto um 10 só pode ser
                // órfão. Sem o gate, o resgate cobre o caso inteiro em vez de metade.
                key.SetValue("SystemResponsiveness", 20, Microsoft.Win32.RegistryValueKind.DWord);
                key.DeleteValue("NetworkThrottlingIndex", false);
                KitLugia.Core.Logger.Log("🌐 Resgate: tweak de rede órfão de uma execução anterior foi revertido (SystemResponsiveness 10 → 20).");
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogWarning("RescueNetworkBoostOnStartup", ex.Message);
            }
        }

        // ============================================================
// RESGATE APÓS CRASH — o motor tem de devolver a máquina ao estado
// original MESMO que o processo do Kit seja morto (power kill, BSOD,
// "terminar processo" no gestor de tarefas).
// ============================================================
// Sem isto, um crash com o boost aplicado deixaria processos em High ou
// BelowNormal para sempre e o utilizador nunca mais recuperaria a maquina sem
// reboot. O ficheiro guarda o que foi alterado, linha a linha:
//     boost|<pid>|<prioridade original>
//     throttle|<pid>
// É texto simples (sem JSON) porque tem de sobreviver a um kill sem halfway e
// nao pode falhar a ler por causa de um esquema de serializacao.

private static string CrashRescueFile => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "KitLugia", "boost_rescue.txt");

/// Grava o estado atual. Chamado sempre que o motor mexe num processo.
///
/// CUSTO (05/10/2026): a chamada vem de dentro do `foreach` do ProBalance, que
/// percorre TODOS os processos a cada ciclo. Sem deduplicar, N processos
/// rebaixados = N escritas (criar tmp + apagar + mover) por ciclo, num motor que
/// roda o dia inteiro. Como o conteudo so muda quando o conjunto muda, comparar
/// com a ultima escrita reduz as escritas ao minimo — tipicamente 1 quando entra
/// um processo em boost e 1 quando sai. O piso de tempo evita escritas seguidas
/// num alt-tab rapido.
/// </summary>
private static string _lastRescueContent = string.Empty;
private static DateTime _lastRescueWrite = DateTime.MinValue;

private void SaveCrashRescue(bool forcar = false)
{
    try
    {
        var sb = new StringBuilder();
        lock (_throttleLock)
        {
            foreach (var kv in _originalPriorities)
                sb.Append("boost|").Append(kv.Key).Append('|').Append((int)kv.Value).Append('\n');
            foreach (var pid in _throttledProcesses)
                sb.Append("throttle|").Append(pid).Append('\n');
        }
        // Os _boostTargets sem original conhecida entram como "boost -> Normal".
        foreach (var kv in _boostTargets)
            if (!sb.ToString().Contains($"boost|{kv.Key}|"))
                sb.Append("boost|").Append(kv.Key).Append('|').Append((int)ProcessPriorityClass.Normal).Append('\n');

        string conteudo = sb.ToString();
        if (!forcar &&
            conteudo == _lastRescueContent &&
            (DateTime.UtcNow - _lastRescueWrite) < TimeSpan.FromSeconds(2))
            return;
        if (!forcar && conteudo == _lastRescueContent) return;

        _lastRescueContent = conteudo;
        _lastRescueWrite = DateTime.UtcNow;

        string path = CrashRescueFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (conteudo.Length == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        // Escrita atomica: um kill a meio não pode deixar um ficheiro truncado.
        //
        // File.Move(..., overwrite: true) = MoveFileEx(MOVEFILE_REPLACE_EXISTING):
        // o destino e' trocado numa UNICA operacao de renomeacao, dentro do mesmo
        // volume. A versao anterior (Delete + Move) tinha uma janela entre as duas
        // chamadas em que o ficheiro NAO EXISTIA - morrer nesse instante perdia o
        // resgate e as prioridades ficavam presas para sempre. Medido em
        // tests/gbsave (morte real a meio da escrita, 640 KB de estado).
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, conteudo);
        File.Move(tmp, path, overwrite: true);
    }
    catch (Exception ex) { ConditionalLog.LogOnce("SaveCrashRescue", ex); }
}

private static void ClearCrashRescueFile()
{
    try { if (File.Exists(CrashRescueFile)) File.Delete(CrashRescueFile); }
    catch { }
    _lastRescueContent = string.Empty;   // o estado em disco e "nada": forca a proxima escrita
    _lastRescueWrite = DateTime.MinValue;
}

/// <summary>
/// Restaura o que uma execução anterior deixou por restaurar. Roda no arranque.
/// Só toca em processos que ESTEJAM mesmo no estado que deixámos (assim um PID
/// reciclado pelo Windows não é estragado).
/// </summary>
private static void RescueOrphanedBoostState()
{
    try
    {
        string path = CrashRescueFile;
        if (!File.Exists(path)) return;

        var linhas = File.ReadAllLines(path);
        int restaurados = 0;
        foreach (var linha in linhas)
        {
            var p = linha.Split('|');
            if (p.Length < 2) continue;
            if (!uint.TryParse(p[1], out uint pid) || pid == 0) continue;
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                if (p[0] == "throttle")
                {
                    if (proc.PriorityClass == ProcessPriorityClass.BelowNormal)
                    {
                        proc.PriorityClass = ProcessPriorityClass.Normal;
                        Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_NORMAL);
                        restaurados++;
                        KitLugia.Core.Logger.Log($"🛟 Resgate: {proc.ProcessName} (PID: {pid}) estava BelowNormal de uma execucao anterior → Normal");
                    }
                }
                else if (p[0] == "boost" && p.Length >= 3)
                {
                    var original = ProcessPriorityClass.Normal;
                    if (int.TryParse(p[2], out int ov))
                    {
                        try { original = (ProcessPriorityClass)ov; } catch { original = ProcessPriorityClass.Normal; }
                    }
                    // Só mexe se estiver no estado que o boost aplica.
                    if (proc.PriorityClass == ProcessPriorityClass.High ||
                        proc.PriorityClass == ProcessPriorityClass.AboveNormal)
                    {
                        proc.PriorityClass = original;
                        restaurados++;
                        KitLugia.Core.Logger.Log($"🛟 Resgate: {proc.ProcessName} (PID: {pid}) ficou com prioridade alterada de uma execucao anterior → {original}");
                    }
                    // Repõe TAMBEM a memory priority: o ProBalance deixa VERY_LOW e isso
                    // nao volta sozinho so com a mudanca de prioridade de processo (o
                    // teste T4 apanhou exatamente isso: prio voltava, mem ficava em 1).
                    try { Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_NORMAL); } catch { }
                }
            }
            catch { /* o processo ja morreu: nada a fazer */ }
        }

        ClearCrashRescueFile();
        if (restaurados > 0)
            KitLugia.Core.Logger.Log($"🛟 Resgate pos-crash: {restaurados} processo(s) devolvido(s) ao estado original.");
    }
    catch (Exception ex) { ConditionalLog.LogOnce("RescueOrphanedBoostState", ex); }
}

        /// <summary>Lê um bool do HKCU\Software\KitLugia\TraySettings sem depender de key aberta.</summary>
        private static bool ReadBoolSettingSafe(RegistryKey? key, string name)
        {
            try
            {
                if (key != null) return ReadBoolSetting(key, name, false);
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\KitLugia\TraySettings");
                return k != null && ReadBoolSetting(k, name, false);
            }
            catch { return false; }
        }


        private readonly HashSet<uint> _throttledProcesses = new();
        private readonly object _throttleLock = new();
        private DispatcherTimer? _proBalanceTimer; // Timer dedicado para ProBalance (executa independente)
        private const int ProBalanceSamplesRequired = 3; // 3 amostras consecutivas (3s timer = ~9s) antes de throttle
        private const int ProBalanceCooldownSec = 30;    // 30s de cooldown antes de re-throttle do mesmo processo
        private readonly Dictionary<uint, int> _proBalanceConsecutive = new();    // PID → contagem de amostras acima do threshold
        private readonly Dictionary<uint, DateTime> _proBalanceCooldowns = new(); // PID → fim do cooldown

        // Dispatcher para o ProBalance correto baseado no motor
        private void ApplyProBalance(uint foregroundPid)
        {

            if (!ProBalance)
            {
                return;
            }
            

            if (_customEngineConfig != null && _customEngineConfig.ProBalance)
            {
                ApplyProBalanceCustom(foregroundPid, _customEngineConfig.ProBalanceCpuThreshold);
                return;
            }

            switch (_currentEngine)
            {
                case GameBoostEngine.V1_Balanced:
                    // V1 ORIGINAL: Não aplica ProBalance (comportamento puro)
                    break;
                case GameBoostEngine.V2_StableFPS:
                    ApplyProBalanceV2(foregroundPid);
                    break;
                case GameBoostEngine.V3_Extreme:
                    ApplyProBalanceV3(foregroundPid);
                    break;
                default:
                    // Padrão também não aplica (V1)
                    break;
            }
        }


        private void ApplyProBalanceV2(uint foregroundPid)
        {
            ApplyProBalanceCore(foregroundPid, 8.0, "V2");
        }


        private void ApplyProBalanceV3(uint foregroundPid)
        {
            ApplyProBalanceCore(foregroundPid, 3.0, "V3");
        }


        private void ApplyProBalanceCustom(uint foregroundPid, int thresholdPercent)
        {
            ApplyProBalanceCore(foregroundPid, thresholdPercent, "Custom");
        }

        // Core do ProBalance com threshold configurável e amostras consecutivas
        private void ApplyProBalanceCore(uint foregroundPid, double cpuThreshold, string version)
        {
            ConditionalLog.Try("ApplyProBalanceCore", () =>
            {
                lock (_throttleLock)
                {
                    var now = DateTime.UtcNow;

                    // Restaura processos que saíram do foreground ou saíram de cooldown
                    var toRestore = _throttledProcesses.Where(p => p != foregroundPid).ToList();
                    foreach (var pid in toRestore)
                    {
                        try
                        {
                            using var proc = Process.GetProcessById((int)pid);
                            string name = proc.ProcessName.ToLower();

                            if (_protectedProcesses.Contains(name))
                            {
                                _throttledProcesses.Remove(pid);
                                _proBalanceConsecutive.Remove(pid);
                                _proBalanceCooldowns.Remove(pid);
                                continue;
                            }

                            if (proc.PriorityClass == ProcessPriorityClass.BelowNormal)
                            {
                                proc.PriorityClass = ProcessPriorityClass.Normal;
                                ConditionalLog.Try("ProBalanceRestore",
                                    () => Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_NORMAL));
                                KitLugia.Core.Logger.Log($"🔼 ProBalance {version}: {name} (PID: {pid}) restaurado para Normal");
                            }
                            _throttledProcesses.Remove(pid);
                            _proBalanceConsecutive.Remove(pid);
                            _proBalanceCooldowns.Remove(pid);
                        }
                        catch (Exception ex)
                        {
                            _throttledProcesses.Remove(pid);
                            _proBalanceConsecutive.Remove(pid);
                            _proBalanceCooldowns.Remove(pid);
                            ConditionalLog.LogOnce("ProBalanceRestoreFail", ex);
                        }
                    }

                    // Throttle com amostras consecutivas + cooldown
                    var currentProcess = Process.GetCurrentProcess();
                    foreach (var proc in GetCachedProcesses())
                    {
                        try
                        {
                            uint pid = (uint)proc.Id;
                            if (pid == foregroundPid || pid == (uint)currentProcess.Id || _throttledProcesses.Contains(pid))
                                continue;

                            string name = proc.ProcessName.ToLower();
                            if (_protectedProcesses.Contains(name))
                            {
                                _proBalanceConsecutive.Remove(pid);
                                _proBalanceCooldowns.Remove(pid);
                                continue;
                            }

                            // Verifica cooldown
                            if (_proBalanceCooldowns.TryGetValue(pid, out var cooldownEnd) && now < cooldownEnd)
                                continue;

                            // Pula processos que o Firemin trimou recentemente (evita conflito duplo)
                            if (_processProfiles.TryGetValue(name, out var prof) &&
                                (DateTime.Now - prof.LastTrimTime).TotalSeconds < 15)
                                continue;

                            double cpuUsage = GetProcessCpuUsage(proc);

                            if (cpuUsage > cpuThreshold)
                            {
                                // Incrementa contagem de amostras consecutivas
                                if (!_proBalanceConsecutive.TryGetValue(pid, out int count))
                                    count = 0;
                                count++;
                                _proBalanceConsecutive[pid] = count;

                                // Só throttle após N amostras consecutivas
                                if (count >= ProBalanceSamplesRequired && proc.PriorityClass >= ProcessPriorityClass.Normal)
                                {
                                    proc.PriorityClass = ProcessPriorityClass.BelowNormal;
                                    _throttledProcesses.Add(pid);
                                    _proBalanceConsecutive.Remove(pid);
                                    _proBalanceCooldowns[pid] = now.AddSeconds(ProBalanceCooldownSec);

                                    ConditionalLog.Try("ProBalanceMemoryPrio",
                                        () => Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_VERY_LOW));KitLugia.Core.Logger.Log(
                                        $"🔻 ProBalance {version}: {name} (PID: {pid}) throttled após {count} amostras (CPU: {cpuUsage:F1}%)");
                                    SaveCrashRescue();   // registado para o caso de crash
                                }
                            }
                            else
                            {
                                // Abaixo do threshold: reseta contagem
                                _proBalanceConsecutive.Remove(pid);
                                _proBalanceCooldowns.Remove(pid);
                            }
                        }
                        catch (Exception ex) { ConditionalLog.LogOnce("ProBalanceThrottle", ex); }
                        finally { proc.Dispose(); }
                    }
                }
            });
        }


        public void RestoreAllThrottledProcesses()
        {
            lock (_throttleLock)
            {
                int restaurados = 0, naoRestaurados = 0;
                var toRestore = _throttledProcesses.ToList();
                naoRestaurados = toRestore.Count;   // parte de "ainda throttled"
                foreach (var pid in toRestore)
            {
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    string name = proc.ProcessName.ToLower();

                    // Restaura para Normal
                    if (proc.PriorityClass == ProcessPriorityClass.BelowNormal)
                    {
                        proc.PriorityClass = ProcessPriorityClass.Normal;

                        // Restaura thread memory priority
                        try
                        {
                            Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_NORMAL);
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                        KitLugia.Core.Logger.Log($"🔼 ProBalance Global: {name} (PID: {pid}) restaurado para Normal");
                        restaurados++; naoRestaurados--;
                    }
                    _throttledProcesses.Remove(pid);
                }
                catch
                {
                    _throttledProcesses.Remove(pid);
                }
            }

            // BUG CORRIGIDO (05/10/2026): a mensagem repetia o mesmo numero duas vezes
            // ("N restaurados, N ainda throttled") porque contava depois de remover tudo.
            // Em caso de falha parecia que nada tinha sido restaurado.
            KitLugia.Core.Logger.Log($"⚖️ ProBalance: {restaurados} processo(s) restaurado(s), {naoRestaurados} ainda throttled");
            }

            // O botao "restaurar" tambem tem que desfazer o boost de prioridade: senao os
            // processos em faixa (foco ou sustentada) ficam com prioridade alterada para sempre.
            if (_boostTargets.Count > 0)
            {
                int qt = _boostTargets.Count;
                RevertAllBoostTargets();
                KitLugia.Core.Logger.Log($"🔼 Boost: {qt} processo(s) restaurado(s) para a prioridade original");
            }
            // Sem isto o ficheiro de resgate diria que ainda ha processos em boost e o
            // proximo arranque tentaria "restaurar" pids ja limpos.
            if (_throttledProcesses.Count == 0 && _boostTargets.Count == 0)
            {
                ClearCrashRescueFile();
            }
        }

        // REMOVIDO: ApplyProBalanceOld (não usado, substituído por ApplyProBalanceCore)

        // Helper: estima uso de CPU de um processo (CORRIGIDO: usa delta entre amostras)
        private double GetProcessCpuUsage(Process proc)
        {
            try
            {
                int pid = proc.Id;
                var now = DateTime.Now;
                var currentCpu = proc.TotalProcessorTime;

                if (_cpuTimeCache.TryGetValue(pid, out var prev))
                {
                    var elapsed = (now - prev.Timestamp).TotalSeconds;
                    var cpuDelta = (currentCpu - prev.CpuTime).TotalSeconds;

                    _cpuTimeCache[pid] = (now, currentCpu);

                    if (elapsed > 0 && cpuDelta >= 0)
                    {
                        return (cpuDelta / (Environment.ProcessorCount * elapsed)) * 100;
                    }
                }
                else
                {
                    _cpuTimeCache[pid] = (now, currentCpu);
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return 0;
        }


        private void RevertBoost(uint pid)
        {
            if (pid == 0)
            {
                Logger.Log("⚠️ RevertBoost: PID inválido (0)");
                return;
            }

            // GLOBAL: ThreadEfficiencyMode via pid (não precisa de handle)
            Win32Api.SetThreadEfficiencyForAllThreads(pid, true);
            Win32Api.RestoreTimerResolution();

            // PROCESS-LEVEL: Prioridade, I/O, Page, GameClassInfo, EcoQoS
            try
            {
                using var proc = Process.GetProcessById((int)pid);

                // Restaura prioridade ORIGINAL ao sair do foreground
                if (ForegroundBoostEnabled)
                {
                    if (_originalPriorities.TryGetValue(pid, out var originalPriority))
                    {
                        if (proc.PriorityClass != originalPriority)
                            proc.PriorityClass = originalPriority;
                        _originalPriorities.TryRemove(pid, out _);
                    }
                    else if (proc.PriorityClass == ProcessPriorityClass.High ||
                             proc.PriorityClass == ProcessPriorityClass.RealTime ||
                             proc.PriorityClass == ProcessPriorityClass.AboveNormal)
                    {
                        proc.PriorityClass = ProcessPriorityClass.Normal;
                    }
                }

                try { Win32Api.SetProcessIoPriority(proc.Handle, 2); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { Win32Api.SetProcessPagePriority(proc.Handle, 5); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_NORMAL); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                // BUG CORRIGIDO (05/10/2026): o revert fazia SetEcoQoS(handle, TRUE) — ou
                // seja, LIGAVA o power saving que o boost tinha desligado. EcoQoS
                // (PROCESS_POWER_THROTTLING_EXECUTION_SPEED) poe o Windows a pôr o
                // processo em Nucleos E / a baixar a frequencia: o app ficava LENTO DEPOIS
                // de sair do boost, e o usuario via "o Kit estragou o meu programa".
                // O estado neutro e' EcoQoS desligado (= o default do Windows).
                try { Win32Api.SetPowerThrottling(proc.Handle, false); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { Win32Api.SetProcessGameClassInfo(proc.Handle, false); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }


        private void SetEcoQoS(IntPtr processHandle, bool enableEcoMode)
        {
            try
            {
                // Só aplica no Windows 11 (build >= 22000)
                if (Environment.OSVersion.Version.Build < 22000) return;

                // Delega ao Win32Api: um unico SetProcessInformation que trata EcoQoS E
                // honra o pedido de timer resolution do processo (bit IGNORE_TIMER = 0).
                Win32Api.SetPowerThrottling(processHandle, enableEcoMode);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }


        private void ProBalanceTimerTick(object? sender, EventArgs e)
        {
            try
            {
                if (ProBalance && GamePriorityEnabled && _currentBoostedPid != 0)
                {
                    ApplyProBalance(_currentBoostedPid);
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }


        // Reverte o boost do foreground atual (usado ao desligar o toggle "Boost do App Ativo")
        public void RevertCurrentBoost()
        {
            try
            {
                // Reverte a faixa inteira (foco + sustentados): antes so o PID em foco voltava
                // e os processos na faixa sustentada ficavam com prioridade alterada para sempre.
                RevertAllBoostTargets();
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public void ShutdownGameBoost()
        {
            try
            {
                if (_useWinEventHook && _winEventHook != IntPtr.Zero)
                {
                    Win32Api.UnhookWinEvent(_winEventHook);
                    _winEventHook = IntPtr.Zero;
                    _useWinEventHook = false;
                    _winEventDelegate = null;
                }


                if (_proBalanceTimer != null)
                {
                    _proBalanceTimer.Tick -= ProBalanceTimerTick;
                    _proBalanceTimer.Stop();
                    _proBalanceTimer = null;
                }


                _foregroundCheckTimer?.Stop();
                _foregroundCheckTimer = null;

                // Reverte boost de TODOS os processos em boost (foco + faixa sustentada)
                // e o tweak global de rede (SystemResponsiveness) — o GameBoost desligado
                // tem que devolver a máquina ao estado original, senão o "travamento"
                // sobrevive ao Kit.
                //
                // BUG CORRIGIDO (05/10/2026): faltava os processos REBAIXADOS pelo
                // ProBalance. Eles vivem em _throttledProcesses (um conjunto separado dos
                // _boostTargets) e ficavam em BelowNormal PARA SEMPRE depois de desligar
                // o GameBoost — ou seja, "o Kit deixou os meus programas lentos" sem que
                // nenhum toggle estivesse ligado. RestoreAllThrottledProcesses() trata
                // os dois conjuntos (e no fim chama RevertAllBoostTargets).
                RestoreAllThrottledProcesses();
                RevertNetworkBoost();

                // O estado em disco tambem tem de sair: sem isto o resgate de arranque
                // tentaria restaurar PIDs velhos no proximo start.
                ClearCrashRescueFile();

                Win32Api.SetWin32PrioritySeparation(false);
                Win32Api.RestoreTimerResolution();

                // Limpa referências
                _lastForegroundHwnd = IntPtr.Zero;

                KitLugia.Core.Logger.Log("🎮 GameBoost desativado (SetWinEventHook Kernel Hook)");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        private void CheckAndCleanStandby(int systemUsagePercent)
        {
            try
            {
                long standbyMB = KitLugia.Core.MemoryOptimizer.GetStandbyListSizeMB();
                IslcLastCleanMB = standbyMB;

                if (standbyMB > IslcThresholdMB)
                {
                    // Purge e pesado (18GB+ pode travar UI) — roda em background
                    _ = System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            KitLugia.Core.MemoryOptimizer.PurgeStandbyList();
                            IslcLastCleanTime = DateTime.Now;
                            IslcCleanCount++;
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    });
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        private bool IsTaskbarWindow(IntPtr hwnd)
        {
            if (!Win32Api.IsWindowVisible(hwnd)) return false;

            IntPtr owner = Win32Api.GetWindow(hwnd, Win32Api.GW_OWNER);
            int exStyle = Win32Api.GetWindowLong(hwnd, Win32Api.GWL_EXSTYLE);

            // A window is on the taskbar if:
            // 1. It is visible (checked above)
            // 2. It has no owner AND is not a tool window
            // 3. OR it has the explicit WS_EX_APPWINDOW style
            bool isToolWindow = (exStyle & Win32Api.WS_EX_TOOLWINDOW) != 0;
            bool isAppWindow = (exStyle & Win32Api.WS_EX_APPWINDOW) != 0;

            if (owner == IntPtr.Zero && !isToolWindow) return true;
            if (isAppWindow) return true;

            return false;
        }

        private void CleanRamNow()
        {
            try
            {
                // Handle stutter backoff
                if (_stutterBackoffCycles > 0)
                {
                    _stutterBackoffCycles--;
                    return;
                }


                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        int before = GetMemoryUsagePercent();
                        var result = MemoryOptimizer.Optimize(SelectedCleaningMode);
                        int after = GetMemoryUsagePercent();
                        sw.Stop();

                        _lastCleanDurationMs = sw.ElapsedMilliseconds;

                        // If cleaning takes too long (> 800ms on a 32GB system), it might cause stutter
                        // Adaptive learning: wait more cycles before next auto-clean
                        if (_lastCleanDurationMs > 800)
                        {
                            _stutterBackoffCycles = 3; // Skip next 3 cycles (~1.5 min)
                        }

                        int freed = before - after;
                        string msg = freed > 0
                            ? $"RAM liberada! {before}% → {after}% ({freed}% liberado) [{_lastCleanDurationMs}ms]"
                            : $"Limpeza concluída. RAM: {after}%";

                        // BeginInvoke (nao Invoke): a thread do pool nao fica presa esperando a UI,
                        // que sob carga pode demorar segundos — era um dos focos de starvation.
                        PostTrayUi(() =>
                        {
                            UpdateTrayIcon(after);

                            _trayIcon?.ShowBalloonTip(
                                3000,
                                "KitLugia RAM Booster",
                                msg,
                                ToolTipIcon.Info
                            );
                        });
                    }
                    catch
                    {
                        // Silently ignore clean errors
                    }
                });
            }
            catch
            {
                // Silently ignore clean errors
            }
        }

        private int GetMemoryUsagePercent()
        {
            return MemoryOptimizer.GetMemoryStats().Percent;
        }

        private void UpdateTrayIcon(int percent)
        {
            try
            {
                if (_trayIcon == null) return;

                // Sem mudanca: nao recria bitmap/GDI nem chama Shell_NotifyIcon de novo.
                if (percent == _lastTrayPercent) return;

                // Determine color based on usage
                Color bgColor;
                if (percent >= 90) bgColor = Color.FromArgb(220, 53, 69);      // Red
                else if (percent >= 70) bgColor = Color.FromArgb(255, 193, 7);  // Yellow
                else bgColor = Color.FromArgb(40, 167, 69);                     // Green

                // Create a 16x16 icon with the percentage text
                var bmp = new Bitmap(16, 16);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(bgColor);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                    string text = percent.ToString();
                    float fontSize = text.Length > 2 ? 6.5f : 8f;
                    using var font = new Font("Segoe UI", fontSize, FontStyle.Bold);
                    using var brush = new SolidBrush(Color.White);

                    var size = g.MeasureString(text, font);
                    float x = (16 - size.Width) / 2;
                    float y = (16 - size.Height) / 2;
                    g.DrawString(text, font, brush, x, y);
                }

                var newIcon = System.Drawing.Icon.FromHandle(bmp.GetHicon());
                var oldIcon = _currentIcon;
                _lastTrayPercent = percent;
                _trayIcon.Icon = newIcon;
                _currentIcon = newIcon;

                // Cleanup old icon
                if (oldIcon != null)
                {
                    try { Win32Api.DestroyIcon(oldIcon.Handle); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }

                bmp.Dispose();
            }
            catch
            {
                // Fallback: use app icon if available
            }
        }

        public void ShowMinimizedNotification()
        {
            _trayIcon?.ShowBalloonTip(
                2000,
                "KitLugia",
                "Monitorando RAM em segundo plano. Clique duas vezes para abrir.",
                ToolTipIcon.Info
            );
        }


        public bool IsTrayIconHealthy()
        {
            try
            {
                if (_trayIcon == null)
                {
                    KitLugia.Core.Logger.Log("❌ Tray Icon é null");
                    return false;
                }

                if (!_trayIcon.Visible)
                {
                    KitLugia.Core.Logger.Log("❌ Tray Icon não está visível");
                    return false;
                }

                if (string.IsNullOrEmpty(_trayIcon.Text))
                {
                    KitLugia.Core.Logger.Log("❌ Tray Icon Text está vazio");
                    return false;
                }

                if (_trayIcon.ContextMenuStrip == null)
                {
                    KitLugia.Core.Logger.Log("❌ Tray Icon ContextMenu é null");
                    return false;
                }

                // Testa se consegue atualizar o ícone
                UpdateTrayIcon(GetMemoryUsagePercent());

                KitLugia.Core.Logger.Log("✅ Tray Icon está saudável");
                return true;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"❌ Erro na verificação de saúde do Tray Icon: {ex.Message}");
                return false;
            }
        }


        public bool RecoverTrayIcon()
        {
            try
            {
                KitLugia.Core.Logger.Log("🔄 Tentando recuperar Tray Icon...");

                // Dispose do antigo
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                }

                // Recria completamente
                _trayIcon = new NotifyIcon
                {
                    Text = "KitLugia RAM Monitor",
                    Visible = false
                };

                // Recria menu
                var menu = new ContextMenuStrip();
                var itemClean = new ToolStripMenuItem("🧹 Limpar RAM Agora");
                itemClean.Click += (s, e) => CleanRamNow();
                menu.Items.Add(itemClean);

                var itemOpen = new ToolStripMenuItem("🚀 Abrir KitLugia");
                itemOpen.Click += (s, e) => OnOpenMainWindow?.Invoke();
                menu.Items.Add(itemOpen);

                var itemExit = new ToolStripMenuItem("❌ Sair");
                itemExit.Click += (s, e) =>
                {
                    Dispose();
                    Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
                };
                menu.Items.Add(itemExit);

                _trayIcon.ContextMenuStrip = menu;
                _trayIcon.DoubleClick += (s, e) => OnOpenMainWindow?.Invoke();

                // Ativa
                UpdateTrayIcon(GetMemoryUsagePercent());
                _trayIcon.Visible = true;

                bool success = _trayIcon.Visible;
                KitLugia.Core.Logger.Log(success ? "✅ Tray Icon recuperado com sucesso" : "❌ Falha na recuperação do Tray Icon");

                return success;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"❌ Erro na recuperação do Tray Icon: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.HasShutdownFinished)
            {
                Application.Current.Dispatcher.Invoke(() => DisposeCore());
                return;
            }
            DisposeCore();
        }

        private void DisposeCore()
        {
            _explorerWatchTimer?.Dispose();
            _explorerWatchTimer = null;

            if (_monitorTimer != null)
            {
                _monitorTimer.Tick -= MonitorTick;
                _monitorTimer.Stop();
            }

            StopRamLimiterTimer();
            
            foreach (var job in _cpuJobObjects.Values)
                Win32Api.CloseHandle(job);
            _cpuJobObjects.Clear();

            StopAdvancedMonitor();

            try { _cpuTotalCounter?.Dispose(); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            _cpuTotalCounter = null;

            ClearProcessCache();

            _processCache.Clear();
            _processAlerts.Clear();
            _processBehaviors.Clear();
            _cpuTimeCache.Clear();

            ShutdownGameBoost();

            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            if (_currentIcon != null)
            {
                try { Win32Api.DestroyIcon(_currentIcon.Handle); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                _currentIcon = null;
            }
        }

        // =========================================================
        // PER-PROCESS RAM LIMITER (inspirado no Firemin)
        // Permite definir limites de RAM por processo e aplicar
        // EmptyWorkingSet quando o processo excede o limite.
        // =========================================================

        // P/Invoke: EmptyWorkingSet da psapi.dll — API que o Firemin usa.
        // Mais eficaz que SetProcessWorkingSetSize(-1,-1) porque força
        // a remoção imediata das páginas do working set para o standby list.
        [System.Runtime.InteropServices.DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        // P/Invoke: OpenProcess com PROCESS_ALL_ACCESS para obter handle com permissão
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_SET_QUOTA = 0x0100;

        // SetProcessWorkingSetSizeEx — versão estendida que aceita flags
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSizeEx(
            IntPtr hProcess,
            IntPtr dwMinimumWorkingSetSize,
            IntPtr dwMaximumWorkingSetSize,
            uint Flags);

        // Flag: desabilita o hard limit no máximo (soft limit — o Windows pode exceder se necessário)
        private const uint QUOTA_LIMITS_HARDWS_MAX_DISABLE = 0x00000008;

        // SetProcessInformation — ProcessMemoryPriority
        // Define prioridade de memória do processo. VERY_LOW = OS trimma primeiro.
        // Usa as definições do Win32Api (MEMORY_PRIORITY_INFORMATION, constants)
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(
            IntPtr hProcess, int processInformationClass,
            IntPtr processInformation, int processInformationSize);

        private const int ProcessMemoryPriorityClass = 0;

        /// <summary>
        /// Define a prioridade de memória de um processo.
        /// VERY_LOW: OS trimma páginas deste processo primeiro.
        /// NORMAL: prioridade padrão (restaura quando em foreground).
        /// </summary>
        private static bool SetProcessMemoryPriority(IntPtr hProcess, uint priority)
        {
            var mpi = new Win32Api.MEMORY_PRIORITY_INFORMATION { MemoryPriority = priority };
            IntPtr ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(
                System.Runtime.InteropServices.Marshal.SizeOf(mpi));
            try
            {
                System.Runtime.InteropServices.Marshal.StructureToPtr(mpi, ptr, false);
                return SetProcessInformation(hProcess, ProcessMemoryPriorityClass, ptr,
                    System.Runtime.InteropServices.Marshal.SizeOf(mpi));
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr); }
        }

        // GetProcessMemoryInfo — lê CommitSize, PeakWorkingSet, PageFaultCount
        // Essenciais para o RAM Limiter inteligente: saber o quanto o app REALMENTE precisa
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb;
            public uint PageFaultCount;
            public ulong PeakWorkingSetSize;
            public ulong WorkingSetSize;
            public ulong QuotaPeakPagedPoolUsage;
            public ulong QuotaPagedPoolUsage;
            public ulong QuotaPeakNonPagedPoolUsage;
            public ulong QuotaNonPagedPoolUsage;
            public ulong PagefileUsage;
            public ulong PeakPagefileUsage;
        }

        [System.Runtime.InteropServices.DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(
            IntPtr hProcess,
            out PROCESS_MEMORY_COUNTERS counters,
            uint size);

        /// <summary>
        /// Lê informações detalhadas de memória de um processo.
        /// Retorna (workingSetMB, commitMB, peakWsMB, pageFaults).
        /// </summary>
        private static (long workingSetMB, long commitMB, long peakWsMB, uint pageFaults) GetProcessMemoryDetails(IntPtr hProcess)
        {
            var counters = new PROCESS_MEMORY_COUNTERS();
            counters.cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf(counters);
            if (GetProcessMemoryInfo(hProcess, out counters, counters.cb))
            {
                return (
                    (long)(counters.WorkingSetSize / (1024 * 1024)),
                    (long)(counters.PagefileUsage / (1024 * 1024)),  // PagefileUsage = CommitSize = Private Bytes
                    (long)(counters.PeakWorkingSetSize / (1024 * 1024)),
                    counters.PageFaultCount
                );
            }
            return (0, 0, 0, 0);
        }

        // Caminho do arquivo de configuração de limites
        private static readonly string _processLimitsPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KitLugia", "process_ram_limits.json");

        /// <summary>
        /// Salva os limites de RAM em JSON com sistema robusto de backup e validação.
        /// </summary>
        public void SaveProcessLimits()
        {
            lock (_robustnessLock)
            {
                try
                {

                    if (!ValidateProcessLimitsIntegrity())
                    {
                        KitLugia.Core.Logger.Log("⚠️ Dados corrompidos detectados - usando backup");
                        RestoreFromBackup();
                        return;
                    }

                    var dir = System.IO.Path.GetDirectoryName(_processLimitsPath)!;
                    if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);

                    var list = _processRamLimits.Values.ToList();
                    

                    KitLugia.Core.Logger.Log($"💾 Salvando {_processRamLimits.Count} limite(s) de RAM:");
                    foreach (var limit in list)
                    {
                        KitLugia.Core.Logger.Log($"   - {limit.ProcessName}: {limit.LimitMB}MB (Enabled: {limit.Enabled})");
                    }
                    

                    string json = System.Text.Json.JsonSerializer.Serialize(list,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    

                    if (string.IsNullOrEmpty(json) || json.Length < 10)
                    {
                        throw new InvalidOperationException("JSON gerado está vazio ou inválido");
                    }
                    

                    CreateBackupIfNeeded();
                    

                    string tempPath = _processLimitsPath + ".tmp";
                    System.IO.File.WriteAllText(tempPath, json);
                    

                    if (!System.IO.File.Exists(tempPath) || new System.IO.FileInfo(tempPath).Length == 0)
                    {
                        throw new InvalidOperationException("Arquivo temporário não foi criado corretamente");
                    }
                    

                    if (System.IO.File.Exists(_processLimitsPath))
                    {
                        System.IO.File.Replace(tempPath, _processLimitsPath, null);
                    }
                    else
                    {
                        System.IO.File.Move(tempPath, _processLimitsPath);
                    }
                    
                    KitLugia.Core.Logger.Log($"✅ JSON salvo em: {_processLimitsPath}");
                    

                    _consecutiveErrors = 0;
                    _lastErrorTime = DateTime.MinValue;
                    _isInSafeMode = false;
                }
                catch (Exception ex)
                {
                    HandleRobustnessError("SaveProcessLimits", ex);
                }
            }
        }

        /// <summary>
        /// Carrega os limites de RAM do JSON com sistema robusto de fallback e validação.
        /// </summary>
        public void LoadProcessLimits()
        {
            lock (_robustnessLock)
            {
                try
                {

                    if (!LoadFromMainFile())
                    {

                        KitLugia.Core.Logger.Log("🔄 Falha ao carregar arquivo principal, tentando backup...");
                        if (!LoadFromBackup())
                        {

                            KitLugia.Core.Logger.Log("⚠️ Falha ao carregar backup, criando configurações padrão");
                            CreateDefaultConfiguration();
                        }
                    }
                    

                    if (!ValidateProcessLimitsIntegrity())
                    {
                        KitLugia.Core.Logger.Log("⚠️ Dados carregados estão corrompidos, tentando recovery...");
                        AttemptDataRecovery();
                    }
                    
                    KitLugia.Core.Logger.Log($"💾 {_processRamLimits.Count} limite(s) de RAM carregado(s) com sucesso.");

                    // Inicia o timer dedicado se houver limites configurados
                    if (!_processRamLimits.IsEmpty)
                    {
                        if (Application.Current?.Dispatcher == null || Application.Current.Dispatcher.HasShutdownFinished) return;
                        Application.Current.Dispatcher.BeginInvoke(
                            new System.Action(StartRamLimiterTimer),
                            System.Windows.Threading.DispatcherPriority.Background);
                        KitLugia.Core.Logger.Log("🔄 Timer do RAM Limiter iniciado automaticamente");
                    }
                    else
                    {
                        KitLugia.Core.Logger.Log("⏸️ Nenhum limite de RAM configurado - timer não iniciado");
                    }
                    

                    _consecutiveErrors = 0;
                    _lastErrorTime = DateTime.MinValue;
                    _isInSafeMode = false;
                }
                catch (Exception ex)
                {
                    HandleRobustnessError("LoadProcessLimits", ex);
                }
            }
        }

        /// <summary>
        /// Inicia o timer dedicado do RAM Limiter.
        /// </summary>
        private void StartRamLimiterTimer()
        {
            if (_ramLimiterTimer != null) return; // Já iniciado

            _ramLimiterTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(_ramLimiterIntervalMs)
            };
            _ramLimiterTimer.Tick += (s, e) => { ApplyProcessRamLimits(); ApplyProcessCpuLimits(); ApplyProcessEngineLimits(); };
            _ramLimiterTimer.Start();
            KitLugia.Core.Logger.Log($"💾 RAM Limiter timer iniciado ({_ramLimiterIntervalMs}ms)");
        }

        /// <summary>
        /// Para o timer dedicado do RAM Limiter.
        /// </summary>
        private void StopRamLimiterTimer()
        {
            _ramLimiterTimer?.Stop();
            _ramLimiterTimer = null;
        }

        /// <summary>
        /// Retorna todos os limites configurados.
        /// </summary>
        public IReadOnlyCollection<ProcessRamLimit> GetProcessRamLimits()
            => _processRamLimits.Values.ToList().AsReadOnly();

        /// <summary>
        /// Adiciona ou atualiza um limite de RAM para um processo.
        /// </summary>
        public void SetProcessRamLimit(string processName, long limitMB, bool enabled = true)
        {
            if (string.IsNullOrWhiteSpace(processName)) return;
            string key = processName.ToLowerInvariant().Replace(".exe", "");

            _processRamLimits.AddOrUpdate(key,
                _ => new ProcessRamLimit { ProcessName = key, LimitMB = limitMB, Enabled = enabled, DirectMode = true },
                (_, existing) => { existing.LimitMB = limitMB; existing.Enabled = enabled; return existing; });

            SaveProcessLimits();

            // Inicia o timer dedicado se ainda não estiver rodando
            if (!_processRamLimits.IsEmpty)
            {
                if (Application.Current?.Dispatcher == null || Application.Current.Dispatcher.HasShutdownFinished) return;
                Application.Current.Dispatcher.Invoke(() => StartRamLimiterTimer());
            }

            KitLugia.Core.Logger.Log($"💾 Limite de RAM definido: {key} → {limitMB} MB ({(enabled ? "ativo" : "inativo")})");
        }

        public ProcessEngineConfig? GetProcessEngineConfig(string processName)
        {
            string key = processName.ToLowerInvariant().Replace(".exe", "");
            if (_processRamLimits.TryGetValue(key, out var limit))
                return limit.EngineConfig;
            return null;
        }

        /// <summary>
        /// Alterna o modo DIRETO (estilo Firemin) de um limite: true = o kit só obedece
        /// (passou do valor, esvazia); false = governador com verificações. Persiste no JSON.
        /// </summary>
        public bool ToggleProcessRamDirectMode(string processName)
        {
            string key = processName.ToLowerInvariant().Replace(".exe", "");
            if (_processRamLimits.TryGetValue(key, out var limit))
            {
                limit.DirectMode = !limit.DirectMode;
                SaveProcessLimits();
                KitLugia.Core.Logger.Log(
                    $"💾 '{key}' modo {(limit.DirectMode ? "DIRETO ⚡ (obedece o valor, estilo Firemin)" : "GOVERNADOR 🧠 (com verificações)")}.");
                return limit.DirectMode;
            }
            return false;
        }

        public void SetProcessEngineConfig(string processName, ProcessEngineConfig config)
        {
            string key = processName.ToLowerInvariant().Replace(".exe", "");
            if (_processRamLimits.TryGetValue(key, out var limit))
            {
                limit.EngineConfig = config;
                SaveProcessLimits();
            }
        }

        /// <summary>
        /// Remove o limite de RAM de um processo.
        /// </summary>
        public void RemoveProcessRamLimit(string processName)
        {
            string key = processName.ToLowerInvariant().Replace(".exe", "");
            _processRamLimits.TryRemove(key, out _);
            SaveProcessLimits();
        }

        /// <summary>
        /// <summary>
        /// Calcula o threshold ISLC ideal baseado na RAM total do sistema.
        /// Baseado nas recomendações da comunidade ISLC (WagnardSoft):
        /// - 8GB RAM → 1024MB threshold
        /// - 16GB RAM → 2048MB threshold  
        /// - 32GB RAM → 4096MB threshold
        /// - 64GB+ RAM → 8192MB threshold
        /// Fórmula: TotalRAM / 8 (mínimo 1024MB)
        /// </summary>
        public static long SuggestIslcThresholdMB()
        {
            try
            {
                var memStatus = new Win32Api.MEMORYSTATUSEX();
                if (Win32Api.GlobalMemoryStatusEx(memStatus))
                {
                    long totalRamMB = (long)(memStatus.ullTotalPhys / (1024 * 1024));
                    // Threshold = 1/8 da RAM total (mínimo 1024MB, máximo 16384MB)
                    return Math.Clamp(totalRamMB / 8, 1024, 16384);
                }
            }
            catch { /* fallback */ }
            return 1024; // default para 8GB
        }

        /// <summary>
        /// Aplica os limites de RAM configurados com trim gradual.
        /// </summary>
        /// <summary>
        /// Aplica a prioridade de memória (VERY_LOW ao conter, NORMAL ao liberar) em todas
        /// as instâncias. O handle PRECISA de PROCESS_SET_INFORMATION (0x0200): sem ele o
        /// SetProcessInformation falha mudo e a VERY_LOW nunca entrava (era o caso do
        /// fallback antigo, que abria o handle só com SET_QUOTA|QUERY).
        /// </summary>
        private static bool ApplyMemPriority(System.Diagnostics.Process[] processes, uint level)
        {
            bool any = false;
            foreach (var proc in processes)
            {
                IntPtr h = IntPtr.Zero;
                try
                {
                    h = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION | 0x0200, false, proc.Id);
                    if (h != IntPtr.Zero && SetProcessMemoryPriority(h, level))
                        any = true;
                }
                catch { }
                finally { if (h != IntPtr.Zero) CloseHandle(h); }
            }
            return any;
        }

        private void ApplyProcessRamLimits()
        {
            if (_processRamLimits.IsEmpty) return;

            IntPtr foregroundHwnd = Win32Api.GetForegroundWindow();
            uint foregroundPid = 0;
            if (foregroundHwnd != IntPtr.Zero)
                Win32Api.GetWindowThreadProcessId(foregroundHwnd, out foregroundPid);

            // NOTA: o governador contém por degraus independente da pressão do sistema
            // (o teto "mole" da versão anterior era decorativo e foi removido).
            // RamPressurePercent continua persistido por compatibilidade.

            foreach (var limit in _processRamLimits.Values)
            {
                if (!limit.Enabled)
                {
                    // Desligar o limite na TELA precisa desfazer o que ele aplicou no PROCESSO.
                    // Sem isto, o teto de working set ficava grudado mesmo com o limite off.
                    if (limit.WorkingSetCeilingApplied)
                    {
                        var vivos = Process.GetProcessesByName(limit.ProcessName);
                        try { foreach (var p in vivos) ReleaseWorkingSetCeiling(p, limit); }
                        finally { foreach (var p in vivos) try { p.Dispose(); } catch { } }
                        KitLugia.Core.Logger.Log($"🧹 RAM Limiter: '{limit.ProcessName}' desativado — teto de memória liberado.");
                    }
                    continue;
                }

                try
                {
                    var processes = Process.GetProcessesByName(limit.ProcessName);
                    if (processes.Length == 0)
                    {
                        limit.LastKnownMB = 0;
                        limit.ConsecutiveTrimCount = 0;
                        limit.IsForeground = false;
                        // Processo parou: o estado do governador é da sessão anterior.
                        limit.StormBackoffLevel = 0;
                        limit.LastFaultsPerSec = 0;
                        continue;
                    }

                    // Soma RAM de todas as instancias e detecta foreground
                    long totalRamMB = 0;
                    bool anyForeground = false;
                    var wsByPid = new System.Collections.Generic.Dictionary<int, long>();

                    foreach (var proc in processes)
                    {
                        try
                        {
                            long ws = proc.WorkingSet64 / (1024 * 1024);
                            totalRamMB += ws;
                            wsByPid[proc.Id] = ws;
                            if ((uint)proc.Id == foregroundPid)
                                anyForeground = true;
                        }
                        catch { }
                    }

                    limit.LastKnownMB = totalRamMB;
                    limit.IsForeground = anyForeground;
                    if (totalRamMB > limit.PeakRamMB) limit.PeakRamMB = totalRamMB;

                    // Nao trimma se esta em foreground
                    if (anyForeground)
                    {
                        foreach (var proc in processes) try { proc.Dispose(); } catch { }
                        continue;
                    }

                    // ── Medição do governador (1 handle QUERY por instância, barato) ──
                    // Preenche o que a UI mostra (Commit) e o que o anti-storm precisa
                    // (faults/s). Antes CommitSizeMB era sempre 0 e o storm nunca era medido.
                    ulong faultsNow = 0;
                    long commitMB = 0, peakWsMB = 0;
                    foreach (var proc in processes)
                    {
                        IntPtr hq = IntPtr.Zero;
                        try
                        {
                            hq = OpenProcess(PROCESS_QUERY_INFORMATION, false, proc.Id);
                            if (hq != IntPtr.Zero)
                            {
                                var det = GetProcessMemoryDetails(hq);
                                commitMB += det.commitMB;
                                if (det.peakWsMB > peakWsMB) peakWsMB = det.peakWsMB;
                                faultsNow += det.pageFaults;
                            }
                        }
                        catch { }
                        finally { if (hq != IntPtr.Zero) CloseHandle(hq); }
                    }
                    DateTime nowMeas = DateTime.Now;
                    double faultsPerSec = 0;
                    if (limit.LastFaultTime != DateTime.MinValue)
                    {
                        double dt = (nowMeas - limit.LastFaultTime).TotalSeconds;
                        // PID reciclado zera o contador: faults menor que antes = medida nova.
                        if (dt > 0.5 && faultsNow >= limit.LastFaultTotal)
                            faultsPerSec = (faultsNow - limit.LastFaultTotal) / dt;
                    }
                    limit.LastFaultTotal = faultsNow;
                    limit.LastFaultTime = nowMeas;
                    limit.LastFaultsPerSec = faultsPerSec;
                    limit.CommitSizeMB = commitMB;
                    if (peakWsMB > limit.PeakWorkingSetMB) limit.PeakWorkingSetMB = peakWsMB;
                    if (limit.RestingWorkingSetMB == 0 || totalRamMB < limit.RestingWorkingSetMB)
                        limit.RestingWorkingSetMB = totalRamMB;
                    limit.CheckCount++;

                    bool exceedsLimit = totalRamMB > limit.LimitMB;

                    // VERY_LOW: o Windows descarta as páginas DESTE processo primeiro quando
                    // precisa de RAM — contém sem forçar fault. Custo zero, sem rajada de disco.
                    // Aplicado 1x ao exceder; devolvido a NORMAL ao normalizar.
                    if (exceedsLimit && !limit.MemPriorityApplied)
                    {
                        if (ApplyMemPriority(processes, Win32Api.MEMORY_PRIORITY_VERY_LOW))
                        {
                            limit.MemPriorityApplied = true;
                            KitLugia.Core.Logger.Log($"🧠 RAM Limiter: '{limit.ProcessName}' com prioridade de memória MUITO BAIXA (descarte preferencial).");
                        }
                    }
                    else if (!exceedsLimit && limit.MemPriorityApplied)
                    {
                        ApplyMemPriority(processes, Win32Api.MEMORY_PRIORITY_NORMAL);
                        limit.MemPriorityApplied = false;
                    }

                    // Cooldown com backoff de storm (cada nível dobra a espera, até 8x).
                    TimeSpan cooldown = GetTrimCooldown(limit);
                    if (limit.StormBackoffLevel > 0)
                        cooldown = TimeSpan.FromMilliseconds(cooldown.TotalMilliseconds * (1 << limit.StormBackoffLevel));
                    bool cooldownPassed = (DateTime.Now - limit.LastTrimTime) >= cooldown;

                    if (exceedsLimit && cooldownPassed)
                    {
                        // ── MODO DIRETO (estilo Firemin): passou do valor, limpa. ──
                        // Sem idle/storm/piso: EmptyWorkingSet em todas as instâncias +
                        // VERY_LOW. Proteções que ficam: nunca em foreground (já pulado acima)
                        // e cooldown mínimo de 2 s (não gira em loop insano).
                        if (limit.DirectMode)
                        {
                            TimeSpan diretoCd = TimeSpan.FromMilliseconds(Math.Max(2000, _ramLimiterIntervalMs));
                            if ((DateTime.Now - limit.LastTrimTime) >= diretoCd)
                            {
                                var limpos = new System.Collections.Generic.HashSet<int>();
                                foreach (var proc in processes)
                                {
                                    IntPtr he = IntPtr.Zero;
                                    try
                                    {
                                        he = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION, false, proc.Id);
                                        if (he != IntPtr.Zero && EmptyWorkingSet(he))
                                            limpos.Add(proc.Id);
                                    }
                                    catch { }
                                    finally { if (he != IntPtr.Zero) CloseHandle(he); }
                                }
                                if (!limit.MemPriorityApplied)
                                {
                                    if (ApplyMemPriority(processes, Win32Api.MEMORY_PRIORITY_VERY_LOW))
                                        limit.MemPriorityApplied = true;
                                }
                                if (limpos.Count > 0)
                                {
                                    limit.WorkingSetCeilingApplied = false; // direto não usa teto
                                    KitLugia.Core.Logger.Log(
                                        $"⚡ RAM Limiter DIRETO: {limit.ProcessName} em {totalRamMB} MB (limite {limit.LimitMB} MB) → " +
                                        $"esvaziado em {limpos.Count} processo(s).");
                                }
                                limit.LastTrimTime = DateTime.Now;
                                limit.ConsecutiveTrimCount++;
                            }
                            foreach (var proc in processes) try { proc.Dispose(); } catch { }
                            continue;
                        }
                        // ── GOVERNADOR v4 (medido em 07/10/2026, harness ramgov) ──
                        // FATO 1: SetProcessWorkingSetSizeEx(min,max,duro) retorna TRUE mas NÃO
                        //   reduz nada em 20 s (321 MB → 321 MB), parado ou ativo. O teto min/max
                        //   é "preferência" que o balance set manager só aplica sob pressão.
                        // FATO 2: EmptyWorkingSet retorna TRUE e esvazia NA HORA (321 MB → 0 MB).
                        //   É o único mecanismo com efeito imediato — igual ao Firemin.
                        // FATO 3: esvaziar app ATIVO gera storm (ele re-faulta tudo que usa);
                        //   esvaziar app IDLE com RAM livre é barato (páginas vão p/ standby =
                        //   soft fault de microssegundos, não vão p/ disco).
                        // REGRA: IDLE + RAM livre → esvazia (barato) + teto (segura recrescer);
                        //   ATIVO ou RAM curta → só VERY_LOW + teto (sem esvaziar, sem storm).
                        const double IdleFaultsPerSec = 100;
                        const double IdleCpuPercent = 3.0;
                        const double CalmFaultsPerSec = 500;
                        const long FreeRamParaEsvaziarMB = 2048;

                        long freeRamMB = 0;
                        try { freeRamMB = (long)(MemoryOptimizer.GetMemoryStats().FreeGB * 1024); } catch { }

                        // CPU% por instância (delta de TotalProcessorTime, 1 leitura por ciclo).
                        var cpuByPid = new System.Collections.Generic.Dictionary<int, double>();
                        foreach (var proc in processes)
                        {
                            try
                            {
                                long ticks = proc.TotalProcessorTime.Ticks;
                                DateTime nowC = DateTime.Now;
                                string ck = limit.ProcessName + ":" + proc.Id;
                                if (_govCpuPrev.TryGetValue(ck, out var prev) && (nowC - prev.when).TotalSeconds > 0.5)
                                {
                                    double cpu = (ticks - prev.ticks) / TimeSpan.TicksPerSecond
                                        / Math.Max(1, Environment.ProcessorCount)
                                        / (nowC - prev.when).TotalSeconds * 100.0;
                                    cpuByPid[proc.Id] = Math.Max(0, cpu);
                                }
                                _govCpuPrev[ck] = (ticks, nowC);
                            }
                            catch { }
                        }
                        // Limpa PIDs mortos do cache de CPU.
                        try
                        {
                            var vivos = new System.Collections.Generic.HashSet<int>(wsByPid.Keys);
                            foreach (var k in _govCpuPrev.Keys.ToList())
                                if (k.StartsWith(limit.ProcessName + ":", StringComparison.OrdinalIgnoreCase)
                                    && int.TryParse(k.Substring(limit.ProcessName.Length + 1), out int p)
                                    && !vivos.Contains(p))
                                    _govCpuPrev.TryRemove(k, out _);
                        }
                        catch { }

                        double maxCpu = 0;
                        foreach (var v in cpuByPid.Values) if (v > maxCpu) maxCpu = v;
                        bool idle = faultsPerSec < IdleFaultsPerSec && maxCpu < IdleCpuPercent;

                        // ANTI-STORM RELATIVO ao baseline (não absoluto): apps barulhentos
                        // (Discord em chamada faulta 20k+/s sozinho) travariam o governador para
                        // sempre com limiar fixo. Storm de verdade é EXPLOSÃO após corte nosso:
                        // faults/s > 5x o baseline E corte recente.
                        double stormLimiar = Math.Max(1500, limit.BaselineFaultsPerSec * 5);
                        if (faultsPerSec > stormLimiar && limit.ConsecutiveTrimCount > 0)
                        {
                            limit.StormBackoffLevel = Math.Min(3, limit.StormBackoffLevel + 1);
                            limit.LastTrimTime = DateTime.Now;
                            // Este ciclo NÃO cortou: os faults medidos são o "normal barulhento"
                            // do app — alimentam o baseline (antes ele ficava 0 para sempre quando
                            // o WS nunca voltava ao limite, e tudo virava "storm").
                            if (limit.BaselineFaultsPerSec <= 0) limit.BaselineFaultsPerSec = faultsPerSec;
                            else limit.BaselineFaultsPerSec = limit.BaselineFaultsPerSec * 0.8 + faultsPerSec * 0.2;
                            KitLugia.Core.Logger.Log(
                                $"⛈️ RAM Limiter: '{limit.ProcessName}' com storm de page faults " +
                                $"({faultsPerSec:F0}/s, baseline {limit.BaselineFaultsPerSec:F0}/s) — pausando cortes (backoff x{1 << limit.StormBackoffLevel}).");
                            foreach (var proc in processes) try { proc.Dispose(); } catch { }
                            continue;
                        }
                        if (faultsPerSec < CalmFaultsPerSec && limit.StormBackoffLevel > 0)
                            limit.StormBackoffLevel--;
                        // Baseline: média dos faults quando NÃO houve corte recente (o "normal").
                        if (limit.ConsecutiveTrimCount == 0)
                        {
                            if (limit.BaselineFaultsPerSec <= 0) limit.BaselineFaultsPerSec = faultsPerSec;
                            else limit.BaselineFaultsPerSec = limit.BaselineFaultsPerSec * 0.8 + faultsPerSec * 0.2;
                        }

                        // Processos tocados neste ciclo (o empty e o teto contam 1x cada —
                        // sem o conjunto, 32 processos com os dois virariam "64 processo(s)").
                        var tocados = new System.Collections.Generic.HashSet<int>();
                        bool esvaziou = false;
                        if (idle && freeRamMB >= FreeRamParaEsvaziarMB)
                        {
                            // IDLE + RAM livre: esvazia (vai p/ standby, barato) e põe o teto
                            // para segurar o recrescimento. É o ciclo do Firemin.
                            foreach (var proc in processes)
                            {
                                IntPtr he = IntPtr.Zero;
                                try
                                {
                                    he = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION, false, proc.Id);
                                    if (he != IntPtr.Zero && EmptyWorkingSet(he))
                                    {
                                        tocados.Add(proc.Id);
                                        esvaziou = true;
                                    }
                                }
                                catch { }
                                finally { if (he != IntPtr.Zero) CloseHandle(he); }
                            }
                        }

                        // Teto de contenção (sempre, barato): segura o recrescimento quando o
                        // manager passar. Distribuído proporcionalmente entre instâncias.
                        // PISO = max(mínimo por tipo, 50% do limite). SEM commit floor: o commit
                        // conta memória NÃO residente (standby/pagefile) e 70% dele passava do
                        // teto (ex.: teto 840 MB com piso 3184 MB) — o "teto" aplicado era o
                        // próprio piso gigante e não continha nada. O commit continua sendo
                        // medido e exibido na UI, mas não entra no piso.
                        long tetoTotalMB = Math.Max(limit.LimitMB, totalRamMB - Math.Max(32, totalRamMB * 15 / 100));
                        long pisoTotalMB = Math.Max(limit.GetEffectiveMinMB(), limit.LimitMB * 50 / 100);
                        if (pisoTotalMB > tetoTotalMB)
                        {
                            KitLugia.Core.Logger.Log(
                                $"⚠️ RAM Limiter: '{limit.ProcessName}' piso ({pisoTotalMB} MB) acima do teto " +
                                $"({tetoTotalMB} MB) — teto ajustado para o piso (limite {limit.LimitMB} MB baixo demais para este app).");
                            tetoTotalMB = pisoTotalMB;
                        }
                        foreach (var proc in processes)
                        {
                            try
                            {
                                long ws = wsByPid.TryGetValue(proc.Id, out long w) ? w : 0;
                                double share = totalRamMB > 0 ? (double)ws / totalRamMB : 1.0;
                                long tetoBytes = Math.Max(16L * 1024 * 1024, (long)(tetoTotalMB * 1024 * 1024 * share));
                                long pisoBytes = Math.Max(16L * 1024 * 1024, (long)(pisoTotalMB * 1024 * 1024 * share));
                                if (tetoBytes < pisoBytes) tetoBytes = pisoBytes;

                                IntPtr handle = OpenProcess(
                                    PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION | 0x0200,
                                    false, proc.Id);
                                if (handle != IntPtr.Zero)
                                {
                                    try
                                    {
                                        if (SetProcessWorkingSetSizeEx(handle, (IntPtr)pisoBytes, (IntPtr)tetoBytes, 0u))
                                        {
                                            tocados.Add(proc.Id);
                                            limit.WorkingSetCeilingApplied = true;
                                        }
                                    }
                                    finally { CloseHandle(handle); }
                                }
                            }
                            catch { }
                        }

                        if (tocados.Count > 0)
                        {
                            KitLugia.Core.Logger.Log(
                                $"🧠 RAM Limiter: {limit.ProcessName} em {totalRamMB} MB (limite {limit.LimitMB} MB) → " +
                                $"{(esvaziou ? "esvaziado (idle) + " : "")}teto {tetoTotalMB} MB " +
                                $"(piso {pisoTotalMB} MB, cpu {maxCpu:F1}%, faults {faultsPerSec:F0}/s, RAM livre {freeRamMB} MB) " +
                                $"em {tocados.Count} processo(s).");
                        }

                        limit.LastTrimTime = DateTime.Now;
                        limit.ConsecutiveTrimCount++;
                    }
                    else if (!exceedsLimit)
                    {
                        // Voltou para dentro do limite: o limite deixa de valer e o teto que o
                        // kit aplicou no processo precisa ser liberado. Sem isso o processo
                        // ficaria espremido dentro do teto antigo ate o fim da sessao.
                        if (limit.WorkingSetCeilingApplied)
                        {
                            foreach (var proc in processes) ReleaseWorkingSetCeiling(proc, limit);
                        }
                        if (limit.ConsecutiveTrimCount > 0)
                            limit.ConsecutiveTrimCount = 0;
                        if (limit.StormBackoffLevel > 0)
                            limit.StormBackoffLevel = 0;
                    }

                    foreach (var proc in processes) try { proc.Dispose(); } catch { }
                }
                catch { }
            }
        }

        /// <summary>
        /// LIBERA o teto de working set que o RAM Limiter aplicou no processo
        /// (SetProcessWorkingSetSizeEx com min = max = -1 = "use o padrao do sistema").
        ///
        /// POR QUE ISSO EXISTE: o teto e uma ALTERACAO NO PROCESSO, nao uma preferencia do
        /// kit. Antes ele era aplicado e nunca desfeito — o processo ficava espremido dentro
        /// do teto antigo por mais tempo do que devia (o limite acabava, o teto nao), com
        /// releitura de pagina do disco: exatamente o stutter que o RAM Limiter existe para
        /// evitar. E desligar o limite na tela deixava o teto grudado ate o fim da sessao.
        /// </summary>
        private void ReleaseWorkingSetCeiling(Process proc, ProcessRamLimit limit)
        {
            try
            {
                IntPtr handle = OpenProcess(
                    PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION,
                    false, proc.Id);
                if (handle == IntPtr.Zero) return;
                try
                {
                    if (SetProcessWorkingSetSizeEx(handle, (IntPtr)(-1), (IntPtr)(-1), 0))
                        limit.WorkingSetCeilingApplied = false;
                }
                finally { CloseHandle(handle); }
            }
            catch { }
        }

        private void ApplyProcessCpuLimits()
        {
            if (_processRamLimits.IsEmpty) return;

            foreach (var limit in _processRamLimits.Values)
            {
                if (!limit.Enabled) continue;
                var cfg = limit.EngineConfig;
                if (cfg == null || !cfg.CpuLimitEnabled) continue;
                // ProBalance modo HardCap gerencia o próprio job (evita conflito de nome)
                if (cfg.ProBalance && cfg.ProBalanceMode == "HardCap") continue;

                string key = limit.ProcessName.ToLowerInvariant().Replace(".exe", "");
                int percent = Math.Clamp(cfg.CpuLimitPercent, 1, 99);

                try
                {
                    var processes = Process.GetProcessesByName(limit.ProcessName);
                    if (processes.Length == 0)
                    {
                        foreach (var p in processes) p.Dispose();
                        continue;
                    }

                    EnsureCpuJob(key, processes, percent);
                    foreach (var p in processes) p.Dispose();
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
        }

        /// <summary>
        /// Cria/atualiza o Job Object de hard cap de CPU do processo (percentual absoluto).
        /// Nao faz dispose dos processos (quem chama é responsável).
        /// </summary>
        private void EnsureCpuJob(string key, Process[] processes, int percent)
        {
            if (_cpuJobObjects.TryGetValue(key, out var existingJob) && existingJob != IntPtr.Zero)
            {
                Win32Api.CloseHandle(existingJob);
                _cpuJobObjects.Remove(key);
            }

            IntPtr hJob = Win32Api.CreateJobObject(IntPtr.Zero, $"KitLugia_CPULimit_{key}");
            if (hJob == IntPtr.Zero) return;

            var cpuInfo = new Win32Api.JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
            {
                ControlFlags = Win32Api.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | Win32Api.JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
                CpuRate = (uint)(percent * 100)
            };

            int size = System.Runtime.InteropServices.Marshal.SizeOf(cpuInfo);
            IntPtr ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
            bool setOk = false;
            try
            {
                System.Runtime.InteropServices.Marshal.StructureToPtr(cpuInfo, ptr, false);
                setOk = Win32Api.SetInformationJobObject(hJob, Win32Api.JobObjectCpuRateControlInformation, ptr, (uint)size);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr); }

            if (!setOk)
            {
                Win32Api.CloseHandle(hJob);
                return;
            }

            int assigned = 0;
            foreach (var proc in processes)
            {
                try
                {
                    if (Win32Api.AssignProcessToJobObject(hJob, proc.Handle))
                        assigned++;
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            if (assigned > 0)
                _cpuJobObjects[key] = hJob;
            else
                Win32Api.CloseHandle(hJob);
        }

        /// <summary>
        /// Remove o hard cap de CPU do processo (fecha o Job Object — o cap deixa de valer).
        /// </summary>
        private void ReleaseCpuJob(string key)
        {
            if (_cpuJobObjects.TryGetValue(key, out var hJob) && hJob != IntPtr.Zero)
            {
                Win32Api.CloseHandle(hJob);
                _cpuJobObjects.Remove(key);
            }
        }

        private static ProcessPriorityClass ParsePriorityClass(string? priority)
        {
            // SEGURANÇA (03/10/2026): "realtime" é TRADUZIDO PARA HIGH, nunca RealTime.
            // Este é o caminho do motor POR PROCESSO (monitor RAM / EngineConfig) — a config
            // salva pelo usuario ainda pode conter "realtime" de versões antigas.
            // RealTime em processo de usuario pode congelar o Windows (tela preta / BSOD).
            if (priority?.Equals("realtime", StringComparison.OrdinalIgnoreCase) == true)
            {
                KitLugia.Core.Logger.Log("⚠️ Perfil: 'realtime' bloqueado por segurança — usando HIGH.");
                return ProcessPriorityClass.High;
            }

            return priority?.ToLowerInvariant() switch
            {
                "idle" => ProcessPriorityClass.Idle,
                "belownormal" => ProcessPriorityClass.BelowNormal,
                "normal" => ProcessPriorityClass.Normal,
                "abovenormal" => ProcessPriorityClass.AboveNormal,
                "high" => ProcessPriorityClass.High,
                _ => ProcessPriorityClass.Normal
            };
        }

        /// <summary>
        /// Motor por processo (standalone): aplica prioridade/I-O/pagina/memoria/EcoQoS
        /// estáticos do EngineConfig e roda o ProBalance dinâmico em 3 modos:
        ///  - Classic: BelowNormal temporário após N amostras (estilo Process Lasso)
        ///  - EcoQoS:  API oficial Win11 (SetProcessInformation PROCESS_POWER_THROTTLING)
        ///  - HardCap: Job Object hard cap enquanto estiver acima do threshold
        /// Nunca throttla o processo em primeiro plano (regra do ProBalance original).
        /// </summary>
        private void ApplyProcessEngineLimits()
        {
            if (_processRamLimits.IsEmpty) return;

            IntPtr foregroundHwnd = Win32Api.GetForegroundWindow();
            uint foregroundPid = 0;
            if (foregroundHwnd != IntPtr.Zero)
                Win32Api.GetWindowThreadProcessId(foregroundHwnd, out foregroundPid);
            var now = DateTime.UtcNow;

            foreach (var limit in _processRamLimits.Values)
            {
                var cfg = limit.EngineConfig;
                string key = limit.ProcessName.ToLowerInvariant().Replace(".exe", "");

                // Limite desligado/removido: garante restauração do que estava throttled
                if (!limit.Enabled || cfg == null)
                {
                    if (limit.IsProBalanceThrottled)
                    {
                        RestoreEngineProcess(key);
                        limit.IsProBalanceThrottled = false;
                        limit.ProBalanceSampleCount = 0;
                    }
                    continue;
                }

                Process[] processes;
                try { processes = Process.GetProcessesByName(limit.ProcessName); }
                catch { continue; }
                if (processes.Length == 0) { foreach (var p in processes) p.Dispose(); continue; }

                try
                {
                    bool anyForeground = false;
                    foreach (var proc in processes)
                    {
                        try { if ((uint)proc.Id == foregroundPid) { anyForeground = true; break; } } catch { }
                    }

                    var targetPriority = ParsePriorityClass(cfg.CpuPriority);

                    // ── 1. Motor estático (idempotente): prioridade/I-O/página/memória/EcoQoS ──
                    foreach (var proc in processes)
                    {
                        try
                        {
                            if (proc.HasExited) continue;

                            // Prioridade: não sobrescreve o throttle ativo do ProBalance clássico
                            if (!(cfg.ProBalance && limit.IsProBalanceThrottled))
                            {
                                try
                                {
                                    if (proc.PriorityClass != targetPriority && targetPriority != ProcessPriorityClass.RealTime)
                                        proc.PriorityClass = targetPriority;
                                }
                                catch (System.ComponentModel.Win32Exception) { /* protegido (PPL/elevado) — ignora */ }
                            }

                            try {                                    Win32Api.SetProcessIoPriority(proc.Handle, SafeIoPriority(cfg.IoPriorityLevel)); } catch { }
                            try { Win32Api.SetProcessPagePriority(proc.Handle, cfg.PagePriorityLevel == 0 ? 5 : cfg.PagePriorityLevel); } catch { }
                            try { Win32Api.SetThreadMemoryPriority(proc.Handle, (uint)(cfg.ThreadMemoryPriority == 0 ? 5 : cfg.ThreadMemoryPriority)); } catch { }
                            // EcoQoS estático só quando o modo ProBalance não controla ele (evita briga)
                            if (cfg.ProBalanceMode != "EcoQoS")
                                try { SetEcoQoS(proc.Handle, cfg.EcoQoSEnabled); } catch { }
                            if (cfg.ThreadEfficiencyMode)
                                try { Win32Api.SetThreadEfficiencyForAllThreads((uint)proc.Id, true); } catch { }
                        }
                        catch { }
                    }

                    // ── 2. ProBalance dinâmico (standalone — quebra o tabu: funciona sem GameBoost) ──
                    if (cfg.ProBalance)
                    {
                        double cpu = 0;
                        foreach (var proc in processes)
                        {
                            try { cpu = Math.Max(cpu, GetProcessCpuUsage(proc)); } catch { }
                        }

                        bool over = cpu > cfg.ProBalanceCpuThreshold;

                        switch (cfg.ProBalanceMode)
                        {
                            case "EcoQoS":
                                foreach (var proc in processes)
                                {
                                    try { SetEcoQoS(proc.Handle, over && !anyForeground); } catch { }
                                }
                                limit.IsProBalanceThrottled = over && !anyForeground;
                                break;

                            case "HardCap":
                                if (over && !anyForeground)
                                {
                                    EnsureCpuJob(key, processes, Math.Clamp(cfg.ProBalanceCpuThreshold, 1, 99));
                                    limit.IsProBalanceThrottled = true;
                                }
                                else if (!over && limit.IsProBalanceThrottled)
                                {
                                    ReleaseCpuJob(key);
                                    limit.IsProBalanceThrottled = false;
                                }
                                break;

                            default: // Classic — estilo Process Lasso (BelowNormal temporário)
                                if (over && !anyForeground && !limit.IsProBalanceThrottled)
                                {
                                    limit.ProBalanceSampleCount++;
                                    if (limit.ProBalanceSampleCount >= ProBalanceSamplesRequired)
                                    {
                                        foreach (var proc in processes)
                                        {
                                            try { if (proc.PriorityClass >= ProcessPriorityClass.Normal) proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                                            try { Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_VERY_LOW); } catch { }
                                        }
                                        limit.IsProBalanceThrottled = true;
                                        limit.ProBalanceCooldownUntil = now.AddSeconds(ProBalanceCooldownSec);
                                        limit.ProBalanceSampleCount = 0;
                                        KitLugia.Core.Logger.Log($"⚖️ ProBalance {cfg.ProBalanceMode}: {limit.ProcessName} throttled (CPU {cpu:F1}% > {cfg.ProBalanceCpuThreshold}%)");
                                    }
                                }
                                else if (limit.IsProBalanceThrottled && (now >= limit.ProBalanceCooldownUntil || !over))
                                {
                                    RestoreEngineProcess(key);
                                    limit.IsProBalanceThrottled = false;
                                    limit.ProBalanceSampleCount = 0;
                                    KitLugia.Core.Logger.Log($"🔼 ProBalance {cfg.ProBalanceMode}: {limit.ProcessName} restaurado para Normal");
                                }
                                else if (!over) limit.ProBalanceSampleCount = 0;
                                break;
                        }
                    }
                    else if (limit.IsProBalanceThrottled)
                    {
                        RestoreEngineProcess(key);
                        if (cfg.ProBalanceMode == "HardCap") ReleaseCpuJob(key);
                        limit.IsProBalanceThrottled = false;
                        limit.ProBalanceSampleCount = 0;
                    }
                }
                catch (Exception ex) { ConditionalLog.LogOnce("ApplyProcessEngineLimits", ex); }
                finally { foreach (var p in processes) try { p.Dispose(); } catch { } }
            }
        }

        /// <summary>
        /// Restaura prioridade Normal + memória Normal de um processo que estava throttled
        /// pelo ProBalance por processo (todos os processos com aquele nome).
        /// </summary>
        private void RestoreEngineProcess(string key)
        {
            try
            {
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        if (proc.ProcessName.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                            proc.ProcessName.Equals(key + ".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            try { if (proc.PriorityClass == ProcessPriorityClass.BelowNormal) proc.PriorityClass = ProcessPriorityClass.Normal; } catch { }
                            try { Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_NORMAL); } catch { }
                        }
                    }
                    catch { }
                    finally { try { proc.Dispose(); } catch { } }
                }
            }
            catch { ConditionalLog.LogOnce("RestoreEngineProcess", null); }
        }

        /// <summary>
        /// Retorna o cooldown dinâmico baseado no comportamento do processo.
        /// </summary>
        private TimeSpan GetTrimCooldown(ProcessRamLimit limit)
        {
            // Base: 5 segundos (testado: app precisa de tempo para recuperar entre trims)
            // + 2s por trim consecutivo (max 20s) — se o app não estabiliza, espera mais
            int baseMs = 5000;
            int penaltyMs = Math.Min(15000, 2000 * limit.ConsecutiveTrimCount);
            return TimeSpan.FromMilliseconds(baseMs + penaltyMs);
        }

        /// <summary>
        /// Retorna o uso atual de RAM de todos os processos monitorados.
        /// </summary>
        public List<(string Name, long CurrentMB, long LimitMB, bool Exceeded)> GetProcessRamStatus()
        {
            var result = new List<(string, long, long, bool)>();
            foreach (var limit in _processRamLimits.Values)
            {
                result.Add((limit.ProcessName, limit.LastKnownMB, limit.LimitMB,
                    limit.LastKnownMB > limit.LimitMB));
            }
            return result;
        }
        

        
        /// <summary>
        /// Inicia o monitor avançado de processos com análise comportamental
        /// </summary>
        public void StartAdvancedMonitor()
        {
            if (_advancedMonitorTimer != null) return; // Já iniciado
            
            _advancedMonitorTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(_advancedMonitorIntervalMs)
            };
            _advancedMonitorTimer.Tick += AdvancedMonitor_Tick;
            _advancedMonitorTimer.Start();
            
            KitLugia.Core.Logger.Log($"🔥 Monitor Avançado iniciado ({_advancedMonitorIntervalMs}ms)");
        }
        
        /// <summary>
        /// Para o monitor avançado
        /// </summary>
        public void StopAdvancedMonitor()
        {
            _advancedMonitorTimer?.Stop();
            _advancedMonitorTimer = null;
            KitLugia.Core.Logger.Log("🔥 Monitor Avançado parado");
        }
        
        /// <summary>
        /// Evento principal do monitor avançado
        /// </summary>
        private void AdvancedMonitor_Tick(object? sender, EventArgs e)
        {
            try
            {
                UpdateSystemStats();
                UpdateProcessCache();
                AnalyzeProcessBehaviors();
                CheckSmartAlerts();
                UpdateTrayIconAdvanced();
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("AdvancedMonitor_Tick", ex.Message);
            }
        }
        
        /// <summary>
        /// Atualiza estatísticas do sistema (CPU, RAM total)
        /// </summary>
        private void UpdateSystemStats()
        {
            if (DateTime.Now - _lastSystemStatsUpdate < TimeSpan.FromSeconds(1))
                return; // Limitar atualizações a 1 por segundo
                
            try
            {
                // CORREÇÃO: Usar GlobalMemoryStatusEx para RAM total REAL do sistema
                var memStatus = new Win32Api.MEMORYSTATUSEX();
                Win32Api.GlobalMemoryStatusEx(memStatus);
                _totalSystemRamMB = (long)(memStatus.ullTotalPhys / (1024 * 1024));
                _availableRamMB = (long)(memStatus.ullAvailPhys / (1024 * 1024));
                
                // CPU usage (Performance Counter REUSADO: delta real desde a ultima
                // leitura, sem custo de init PDH por tick e sem o "primeiro NextValue()=0").
                try
                {
                    _cpuTotalCounter ??= new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total");
                    _currentCpuUsage = _cpuTotalCounter.NextValue();
                }
                catch
                {
                    // PDH indisponivel (ex: contador corrompido): zera em vez de falhar o tick.
                    _currentCpuUsage = 0;
                }
                
                _lastSystemStatsUpdate = DateTime.Now;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("UpdateSystemStats", ex.Message);
            }
        }
        
        /// <summary>
        /// Atualiza cache de processos com informações detalhadas
        /// </summary>
        private void UpdateProcessCache()
        {
            try
            {
                // Coleta ENXUTA: so o que os consumidores usam (WorkingSet/Cpu p/
                // behavior+alerts; Responding p/ alerta "Nao Responsivo", amostrado
                // a cada 5 ticks pois custa SendMessageTimeout por processo GUI).
                // StartTime/VirtualMemory/Threads/HandleCount/Title nao tinham
                // consumidor e custavam 1+ syscall por processo por tick.
                _processCacheTick = unchecked(_processCacheTick + 1);
                bool sampleResponding = (_processCacheTick % 5) == 0;

                var processes = Process.GetProcesses();
                _activeProcessCount = 0;
                var livePids = new System.Collections.Generic.HashSet<int>();

                foreach (var proc in processes)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(proc.ProcessName)) continue;

                        string name = proc.ProcessName.ToLowerInvariant();
                        bool isResponding = true;
                        if (sampleResponding)
                        {
                            isResponding = proc.Responding;
                        }
                        else if (_processCache.TryGetValue(name, out var prevInfo))
                        {
                            isResponding = prevInfo.IsResponding;
                        }

                        var processInfo = new ProcessInfo
                        {
                            ProcessId = proc.Id,
                            ProcessName = name,
                            WorkingSetMB = proc.WorkingSet64 / (1024 * 1024),
                            IsResponding = isResponding,
                            CpuUsage = GetProcessCpuUsage(proc)
                        };

                        _processCache.AddOrUpdate(processInfo.ProcessName, processInfo, (_, _) => processInfo);
                        _activeProcessCount++;
                        livePids.Add(proc.Id);
                    }
                    catch
                    {
                        // Ignora processos que não podem ser acessados
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }

                // Poda do _cpuTimeCache (keyed por PID: sem poda, PIDs mortos
                // acumulam para sempre). Barato: remove quem nao esta vivo.
                foreach (var pid in _cpuTimeCache.Keys)
                {
                    if (!livePids.Contains(pid))
                        _cpuTimeCache.TryRemove(pid, out _);
                }

                // Poda conservadora dos behaviors (keyed por nome): nomes de
                // instaladores/temp somem, mas a entrada ficava para sempre.
                if (_processBehaviors.Count > 500)
                {
                    var cutoff = DateTime.Now - TimeSpan.FromMinutes(30);
                    foreach (var kvp in _processBehaviors)
                    {
                        if (kvp.Value.LastSeen < cutoff)
                            _processBehaviors.TryRemove(kvp.Key, out _);
                    }
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("UpdateProcessCache", ex.Message);
            }
        }
        
        /// <summary>
        /// Analisa comportamento dos processos ao longo do tempo
        /// </summary>
        private void AnalyzeProcessBehaviors()
        {
            if (!_enableBehaviorAnalysis) return;
            
            try
            {
                foreach (var kvp in _processCache)
                {
                    var processName = kvp.Key;
                    var currentInfo = kvp.Value;
                    
                    var behavior = _processBehaviors.GetOrAdd(processName, _ => new ProcessBehavior
                    {
                        ProcessName = processName,
                        FirstSeen = DateTime.Now,
                        LastSeen = DateTime.Now,
                        PeakRamMB = currentInfo.WorkingSetMB,
                        PeakCpuUsage = currentInfo.CpuUsage,
                        SampleCount = 1,
                        AverageRamMB = currentInfo.WorkingSetMB,
                        AverageCpuUsage = currentInfo.CpuUsage
                    });
                    
                    // Atualiza estatísticas
                    behavior.LastSeen = DateTime.Now;
                    behavior.SampleCount++;
                    
                    if (currentInfo.WorkingSetMB > behavior.PeakRamMB)
                        behavior.PeakRamMB = currentInfo.WorkingSetMB;
                        
                    if (currentInfo.CpuUsage > behavior.PeakCpuUsage)
                        behavior.PeakCpuUsage = currentInfo.CpuUsage;
                        
                    // Média móvel (simplificada)
                    behavior.AverageRamMB = (long)((behavior.AverageRamMB * 0.9) + (currentInfo.WorkingSetMB * 0.1));
                    behavior.AverageCpuUsage = (behavior.AverageCpuUsage * 0.9) + (currentInfo.CpuUsage * 0.1);
                    
                    // Detecta anomalias
                    var ramAnomaly = currentInfo.WorkingSetMB > behavior.AverageRamMB * 2.0;
                    var cpuAnomaly = currentInfo.CpuUsage > behavior.AverageCpuUsage * 2.0;
                    
                    if (ramAnomaly || cpuAnomaly)
                    {
                        behavior.AnomalyCount++;
                        KitLugia.Core.Logger.Log($"⚠️ Anomalia detectada: {processName} RAM:{currentInfo.WorkingSetMB}MB CPU:{currentInfo.CpuUsage:F1}%");
                    }
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("AnalyzeProcessBehaviors", ex.Message);
            }
        }
        
        /// <summary>
        /// Verifica alertas inteligentes baseados em limiares
        /// </summary>
        private void CheckSmartAlerts()
        {
            if (!_enableSmartAlerts) return;
            
            try
            {
                foreach (var kvp in _processCache)
                {
                    var processName = kvp.Key;
                    var info = kvp.Value;
                    
                    var alert = _processAlerts.GetOrAdd(processName, _ => new ProcessAlert
                    {
                        ProcessName = processName,
                        LastAlertTime = DateTime.MinValue
                    });
                    
                    bool shouldAlert = false;
                    string alertType = "";
                    
                    // Alerta de RAM
                    if (info.WorkingSetMB > _highRamThresholdMB)
                    {
                        shouldAlert = true;
                        alertType = $"RAM Alta: {info.WorkingSetMB}MB > {_highRamThresholdMB}MB";
                    }
                    
                    // Alerta de CPU
                    if (info.CpuUsage > _highCpuThresholdPercent)
                    {
                        shouldAlert = true;
                        alertType += (string.IsNullOrEmpty(alertType) ? "" : ", ") + $"CPU Alta: {info.CpuUsage:F1}% > {_highCpuThresholdPercent}%";
                    }
                    
                    // Processo não responsivo
                    if (!info.IsResponding)
                    {
                        shouldAlert = true;
                        alertType += (string.IsNullOrEmpty(alertType) ? "" : ", ") + "Não Responsivo";
                    }
                    
                    // Envia alerta se passou o cooldown (5 minutos)
                    if (shouldAlert && DateTime.Now - alert.LastAlertTime > TimeSpan.FromMinutes(5))
                    {
                        alert.LastAlertTime = DateTime.Now;
                        alert.AlertCount++;
                        
                        KitLugia.Core.Logger.Log($"🚨 Alerta: {processName} - {alertType}");
                        
                        // Mostra notificação no tray (se habilitado)
                        if (_trayIcon != null && _trayIcon.Visible)
                        {
                            _trayIcon.ShowBalloonTip(3000, "KitLugia Monitor", 
                                $"{processName}: {alertType}", ToolTipIcon.Warning);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("CheckSmartAlerts", ex.Message);
            }
        }
        
        /// <summary>
        /// Atualiza ícone do tray com informações avançadas
        /// </summary>
        private void UpdateTrayIconAdvanced()
        {
            try
            {
                if (_trayIcon == null) return;
                
                var ramPercent = RamUsagePercent;
                var cpuPercent = _currentCpuUsage;
                
                // Ícone dinâmico baseado no uso
                var iconText = $"RAM:{ramPercent:F0}% CPU:{cpuPercent:F0}%";
                _trayIcon.Text = iconText;
                
                // Atualiza tooltip detalhado
                var tooltip = $"KitLugia Monitor Avançado\n" +
                           $"RAM: {(_totalSystemRamMB - _availableRamMB)}MB / {_totalSystemRamMB}MB ({ramPercent:F1}%)\n" +
                           $"CPU: {cpuPercent:F1}%\n" +
                           $"Processos Ativos: {_activeProcessCount}\n" +
                           $"Limites RAM: {_processRamLimits.Count} configurados";
                           
                _trayIcon.Text = tooltip.Length > 63 ? tooltip.Substring(0, 60) + "..." : tooltip;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("UpdateTrayIconAdvanced", ex.Message);
            }
        }
        
        /// <summary>
        /// Obtém o título da janela principal de um processo
        /// </summary>
        private string GetMainWindowTitle(int processId)
        {
            // Implementação simplificada - poderia usar EnumWindows para mais precisão
            return $"PID:{processId}";
        }
        
        

        
        /// <summary>
        /// Valida integridade dos dados de limites de processo
        /// </summary>
        private bool ValidateProcessLimitsIntegrity()
        {
            try
            {
                if (_processRamLimits == null) return false;
                
                foreach (var kvp in _processRamLimits)
                {
                    var limit = kvp.Value;
                    if (string.IsNullOrEmpty(limit.ProcessName)) return false;
                    if (limit.LimitMB <= 0 || limit.LimitMB > 1024 * 1024) return false; // Máximo 1TB
                    if (limit.PeakRamMB < 0) return false;
                    if (limit.LastTrimTime > DateTime.Now) return false;
                    if (limit.ConsecutiveTrimCount < 0) return false;
                }
                
                return true;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        
        /// <summary>
        /// Carrega do arquivo principal
        /// </summary>
        private bool LoadFromMainFile()
        {
            try
            {
                if (!System.IO.File.Exists(_processLimitsPath))
                {
                    KitLugia.Core.Logger.Log($"📂 Arquivo principal não encontrado: {_processLimitsPath}");
                    return false;
                }

                return LoadFromFile(_processLimitsPath, "principal");
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("LoadFromMainFile", ex.Message);
                return false;
            }
        }
        
        /// <summary>
        /// Carrega do arquivo de backup
        /// </summary>
        private bool LoadFromBackup()
        {
            try
            {
                if (!System.IO.File.Exists(_backupPath))
                {
                    KitLugia.Core.Logger.Log($"📂 Arquivo de backup não encontrado: {_backupPath}");
                    return false;
                }

                return LoadFromFile(_backupPath, "backup");
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("LoadFromBackup", ex.Message);
                return false;
            }
        }
        
        /// <summary>
        /// Carrega de um arquivo específico
        /// </summary>
        private bool LoadFromFile(string filePath, string sourceName)
        {
            try
            {
                string json = System.IO.File.ReadAllText(filePath);
                

                if (string.IsNullOrWhiteSpace(json) || json.Length < 10)
                {
                    KitLugia.Core.Logger.Log($"⚠️ JSON {sourceName} está vazio ou muito pequeno");
                    return false;
                }
                
                var list = System.Text.Json.JsonSerializer.Deserialize<List<ProcessRamLimit>>(json);
                if (list == null)
                {
                    KitLugia.Core.Logger.Log($"⚠️ JSON {sourceName} não pôde ser desserializado");
                    return false;
                }

                _processRamLimits.Clear();
                int loadedCount = 0;
                foreach (var limit in list)
                {
                    if (!string.IsNullOrEmpty(limit.ProcessName) && limit.LimitMB > 0)
                    {
                        _processRamLimits[limit.ProcessName] = limit;
                        loadedCount++;
                        KitLugia.Core.Logger.Log($"   ✅ {limit.ProcessName}: {limit.LimitMB}MB (Enabled: {limit.Enabled})");
                    }
                    else
                    {
                        KitLugia.Core.Logger.Log($"   ⚠️ Ignorando limite inválido: {limit.ProcessName}");
                    }
                }

                KitLugia.Core.Logger.Log($"💾 {loadedCount}/{list.Count} limite(s) carregado(s) do {sourceName}.");
                return loadedCount > 0;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError($"LoadFrom{sourceName}", ex.Message);
                return false;
            }
        }
        
        /// <summary>
        /// Cria configurações padrão
        /// </summary>
        private void CreateDefaultConfiguration()
        {
            try
            {
                _processRamLimits.Clear();
                

                var defaultLimits = new List<(string name, long limitMB)>
                {
                    ("chrome", 2048),
                    ("firefox", 1536),
                    ("code", 1024),
                    ("explorer", 512),
                    ("msedge", 2048)
                };
                
                foreach (var (name, limit) in defaultLimits)
                {
                    _processRamLimits[name] = new ProcessRamLimit
                    {
                        ProcessName = name,
                        LimitMB = limit,
                        Enabled = false, // Desabilitado por padrão
                        Description = $"Limite padrão para {name}",
                        LastKnownMB = 0,
                        PeakRamMB = 0,
                        LastTrimTime = DateTime.MinValue,
                        ConsecutiveTrimCount = 0
                    };
                }
                
                KitLugia.Core.Logger.Log("🔧 Configurações padrão criadas com sucesso");
                SaveProcessLimits(); // Salva as configurações padrão
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("CreateDefaultConfiguration", ex.Message);
            }
        }
        
        /// <summary>
        /// Cria backup se necessário
        /// </summary>
        private void CreateBackupIfNeeded()
        {
            try
            {
                if (DateTime.Now - _lastBackupTime < _backupInterval) return;
                
                if (!System.IO.File.Exists(_processLimitsPath)) return;
                
                var backupDir = System.IO.Path.GetDirectoryName(_backupPath)!;
                if (!System.IO.Directory.Exists(backupDir)) System.IO.Directory.CreateDirectory(backupDir);
                
                System.IO.File.Copy(_processLimitsPath, _backupPath, true);
                _lastBackupTime = DateTime.Now;
                
                KitLugia.Core.Logger.Log($"💾 Backup criado: {_backupPath}");
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("CreateBackupIfNeeded", ex.Message);
            }
        }
        
        /// <summary>
        /// Restaura do backup
        /// </summary>
        private void RestoreFromBackup()
        {
            try
            {
                if (!System.IO.File.Exists(_backupPath))
                {
                    KitLugia.Core.Logger.Log("⚠️ Backup não encontrado para restauração");
                    return;
                }
                
                System.IO.File.Copy(_backupPath, _processLimitsPath, true);
                KitLugia.Core.Logger.Log("🔄 Configurações restauradas do backup");
                
                // Tenta carregar novamente
                LoadFromMainFile();
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("RestoreFromBackup", ex.Message);
            }
        }
        
        /// <summary>
        /// Tenta recuperar dados corrompidos
        /// </summary>
        private void AttemptDataRecovery()
        {
            try
            {
                KitLugia.Core.Logger.Log("🔧 Iniciando recuperação de dados...");
                
                // Remove entradas inválidas
                var invalidKeys = _processRamLimits.Where(kvp => 
                    string.IsNullOrEmpty(kvp.Value.ProcessName) || 
                    kvp.Value.LimitMB <= 0).Select(kvp => kvp.Key).ToList();
                    
                foreach (var key in invalidKeys)
                {
                    _processRamLimits.TryRemove(key, out _);
                    KitLugia.Core.Logger.Log($"🗑️ Removida entrada inválida: {key}");
                }
                
                // Corrige valores inválidos
                foreach (var kvp in _processRamLimits)
                {
                    var limit = kvp.Value;
                    
                    if (limit.LimitMB > 1024 * 1024) // Máximo 1TB
                    {
                        limit.LimitMB = 2048; // 2GB padrão
                        KitLugia.Core.Logger.Log($"🔧 Corrigido limite exagerado: {limit.ProcessName}");
                    }
                    
                    if (limit.PeakRamMB < 0) limit.PeakRamMB = 0;
                    if (limit.LastTrimTime > DateTime.Now) limit.LastTrimTime = DateTime.MinValue;
                    if (limit.ConsecutiveTrimCount < 0) limit.ConsecutiveTrimCount = 0;
                }
                
                KitLugia.Core.Logger.Log("✅ Recuperação de dados concluída");
                SaveProcessLimits();
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("AttemptDataRecovery", ex.Message);
                CreateDefaultConfiguration(); // Último recurso
            }
        }
        
        /// <summary>
        /// Manipula erros de forma robusta
        /// </summary>
        private void HandleRobustnessError(string operation, Exception ex)
        {
            _consecutiveErrors++;
            _lastErrorTime = DateTime.Now;
            
            KitLugia.Core.Logger.LogError($"{operation} (Erro #{_consecutiveErrors})", ex.Message);
            

            if (_consecutiveErrors >= _maxConsecutiveErrors)
            {
                _isInSafeMode = true;
                KitLugia.Core.Logger.Log($"🚨 Sistema entrando em modo seguro após {_maxConsecutiveErrors} erros consecutivos");
                
                // Desativa funcionalidades críticas para evitar mais erros
                StopRamLimiterTimer();
                StopAdvancedMonitor();
                
                // Tenta restaurar do backup
                if (operation.Contains("Load"))
                {
                    RestoreFromBackup();
                }
            }
            

            if (DateTime.Now - _lastErrorTime < _errorCooldown)
            {
                KitLugia.Core.Logger.Log("⏱️ Sistema em cooldown devido a erros recentes");
                return;
            }
        }
        
        /// <summary>
        /// Verifica saúde do sistema
        /// </summary>
        public bool IsSystemHealthy()
        {
            return !_isInSafeMode && 
                   _consecutiveErrors < _maxConsecutiveErrors &&
                   (DateTime.Now - _lastErrorTime) > _errorCooldown;
        }
        
        /// <summary>
        /// Força saída do modo seguro
        /// </summary>
        public void ExitSafeMode()
        {
            lock (_robustnessLock)
            {
                _isInSafeMode = false;
                _consecutiveErrors = 0;
                _lastErrorTime = DateTime.MinValue;
                
                KitLugia.Core.Logger.Log("✅ Sistema saiu do modo seguro");
                
                // Reinicia serviços se necessário
                if (!_processRamLimits.IsEmpty)
                {
                    StartRamLimiterTimer();
                }
                
                if (_enableSmartAlerts || _enableBehaviorAnalysis)
                {
                    StartAdvancedMonitor();
                }
            }
        }
        
        /// <summary>
        /// Mostra relatório completo de status do Tray e RAM Limiter
        /// </summary>
        private void ShowTrayStatusReport()
        {
            try
            {
                KitLugia.Core.Logger.Log("🔔 === STATUS DO TRAY ICON SERVICE ===");
                KitLugia.Core.Logger.Log($"📊 Tray Habilitado: {(IsTrayEnabled ? "✅ SIM" : "❌ NÃO")}");
                KitLugia.Core.Logger.Log($"🔄 Close to Tray: {(CloseToTray ? "✅ ATIVO" : "❌ INATIVO")}");
                KitLugia.Core.Logger.Log($"🧹 Auto Clean: {(AutoCleanEnabled ? "✅ ATIVO" : "❌ INATIVO")} (Limite: {AutoCleanThresholdPercent}%)");
                KitLugia.Core.Logger.Log($"🎮 GameBoost: {(GamePriorityEnabled ? "✅ ATIVO" : "❌ INATIVO")}");
                KitLugia.Core.Logger.Log($"📈 Monitor Avançado: {(EnableSmartAlerts || EnableBehaviorAnalysis ? "✅ ATIVO" : "❌ INATIVO")}");
                KitLugia.Core.Logger.Log($"⚡ Smart Alerts: {(EnableSmartAlerts ? "✅ ATIVO" : "❌ INATIVO")} (RAM: {HighRamThresholdMB}MB, CPU: {HighCpuThresholdPercent}%)");
                KitLugia.Core.Logger.Log($"🔍 Behavior Analysis: {(EnableBehaviorAnalysis ? "✅ ATIVO" : "❌ INATIVO")}");
                
                // Status do RAM Limiter
                var activeLimits = _processRamLimits.Where(kvp => kvp.Value.Enabled).ToList();
                if (activeLimits.Any())
                {
                    KitLugia.Core.Logger.Log($"💾 RAM Limiter: ✅ ATIVO ({activeLimits.Count} processo(s) monitorado(s))");
                    foreach (var (name, limit) in activeLimits)
                    {
                        KitLugia.Core.Logger.Log($"   🎯 {name}: {limit.LimitMB}MB {(limit.Enabled ? "✅" : "❌")}");
                    }
                }
                else
                {
                    KitLugia.Core.Logger.Log("💾 RAM Limiter: ❌ INATIVO (nenhum processo selecionado)");
                }
                
                // Status de saúde do sistema
                if (IsSystemHealthy())
                {
                    KitLugia.Core.Logger.Log("🏥 Sistema: ✅ SAUDÁVEL");
                }
                else
                {
                    KitLugia.Core.Logger.Log("🏥 Sistema: ⚠️ MODO SEGURO");
                }
                
                KitLugia.Core.Logger.Log("🔔 ======================================");
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("ShowTrayStatusReport", ex.Message);
            }
        }
        

        
        public class ProcessInfo
        {
            public int ProcessId { get; set; }
            public string ProcessName { get; set; } = "";
            public long WorkingSetMB { get; set; }
            public long VirtualMemoryMB { get; set; }
            public DateTime StartTime { get; set; }
            public bool IsResponding { get; set; }
            public string MainWindowTitle { get; set; } = "";
            public double CpuUsage { get; set; }
            public int ThreadCount { get; set; }
            public int HandleCount { get; set; }
        }
        
        public class ProcessAlert
        {
            public string ProcessName { get; set; } = "";
            public DateTime LastAlertTime { get; set; }
            public int AlertCount { get; set; }
            public string LastAlertType { get; set; } = "";
        }
        
        public class ProcessBehavior
        {
            public string ProcessName { get; set; } = "";
            public DateTime FirstSeen { get; set; }
            public DateTime LastSeen { get; set; }
            public long PeakRamMB { get; set; }
            public double PeakCpuUsage { get; set; }
            public long AverageRamMB { get; set; }
            public double AverageCpuUsage { get; set; }
            public int SampleCount { get; set; }
            public int AnomalyCount { get; set; }
            public TimeSpan TotalRuntime => LastSeen - FirstSeen;
        }
        

        public class ProcessRamLimit
        {
            public string ProcessName { get; set; } = "";
            public long LimitMB { get; set; } = 1024;
            public long MinLimitMB { get; set; } = 0; // 0 = auto-detect based on process type
            public bool Enabled { get; set; } = false;
            public string Description { get; set; } = "";
            public bool IsForeground { get; set; } = false;
            public long LastKnownMB { get; set; } = 0;
            public long PeakRamMB { get; set; } = 0;
            public DateTime LastTrimTime { get; set; } = DateTime.MinValue;
            public int ConsecutiveTrimCount { get; set; } = 0;
            /// true enquanto houver um teto de working set aplicado nos processos deste limite
            /// (precisa ser liberado quando o processo volta abaixo do limite ou o limite e desligado).
            public bool WorkingSetCeilingApplied { get; set; } = false;
            public ProcessEngineConfig? EngineConfig { get; set; } = null;
            public bool SafeAutoRegulate { get; set; } = true; // Modo auto-regulavel (padrao: ativo)

            // ── ProBalance por processo (standalone — roda no tick do RAM Limiter, sem GameBoost) ──
            public int ProBalanceSampleCount { get; set; } = 0;          // amostras consecutivas acima do threshold
            public DateTime ProBalanceCooldownUntil { get; set; } = DateTime.MinValue; // fim do cooldown (modo Clássico)
            public bool IsProBalanceThrottled { get; set; } = false;     // throttled neste momento

            // ── Resting State Tracker ──
            // Aprende o "estado natural" do app ao longo do tempo.
            // O trim NUNCA vai abaixo do resting state (o mínimo que o app precisa para funcionar).
            public long RestingWorkingSetMB { get; set; } = 0; // menor WS observado (natural)
            public long CommitSizeMB { get; set; } = 0; // commit total (Private Bytes)
            public long PeakWorkingSetMB { get; set; } = 0; // pico de WS
            public uint LastPageFaultCount { get; set; } = 0; // page faults no último check
            public int StormBackoffLevel { get; set; } = 0; // 0=normal, 1=cooldown 2x, 2=cooldown 4x
            public int CheckCount { get; set; } = 0; // quantas vezes foi verificado
            public long EffectiveFloorFromCommit { get; set; } = 0; // floor baseado no commit (70% do commit quando commit > limite)
            public bool CommitFloorLogged { get; set; } = false; // log do commit floor: 1x por sessão
            // ── Governador (ligado): VERY_LOW + degraus + backoff por storm ──
            public bool MemPriorityApplied { get; set; } = false; // VERY_LOW aplicado (voltar p/ NORMAL ao liberar)
            public ulong LastFaultTotal { get; set; } = 0; // faults acumulados na medição anterior
            public DateTime LastFaultTime { get; set; } = DateTime.MinValue; // quando mediu
            public double LastFaultsPerSec { get; set; } = 0; // faults/s do último ciclo
            public double BaselineFaultsPerSec { get; set; } = 0; // "normal" do app (storm = 5x isso)
            // ── Modo DIRETO (estilo Firemin) ──
            // Governador = com verificações (idle/storm/piso). Direto = o kit só obedece:
            // passou do limite → EmptyWorkingSet + VERY_LOW, sem idle/storm/piso.
            // Proteções mínimas que ficam nos dois: nunca em foreground + cooldown.
            public bool DirectMode { get; set; } = false;

            /// <summary>
            /// Retorna o mínimo seguro de working set para este processo (em MB).
            /// Se MinLimitMB > 0, usa o valor manual; senão auto-detecta pelo tipo do processo.
            /// </summary>
            public long GetEffectiveMinMB()
            {
                if (MinLimitMB > 0) return MinLimitMB;
                return GetSafeMinimumMB(ProcessName);
            }

            /// <summary>
            /// Mínimos seguros por tipo de processo — evita EmptyWorkingSet total que causa crash.
            /// Baseado em: Electron precisa de 50-80MB, browsers de 80-150MB, apps comuns 20-40MB.
            /// </summary>
            private static long GetSafeMinimumMB(string processName)
            {
                string name = processName.ToLowerInvariant();

                // Electron apps (Discord, Teams, Slack, VS Code, Spotify, etc.)
                if (name is "discord" or "discord" or "slack" or "teams" or "teams" or
                    "code" or "spotify" or "telegram" or "signal" or "whatsapp" or
                    "notion" or "figma" or "obsidian" or "electron")
                    return 60;

                // Chromium browsers (multi-process — each renderer needs ~50MB)
                if (name.Contains("opera") || name.Contains("chrome") || name.Contains("msedge") ||
                    name.Contains("brave") || name.Contains("vivaldi") || name.Contains("waterfox") ||
                    name.Contains("chromium") || name.Contains("iron"))
                    return 80;

                // Firefox (single-process with compartments)
                if (name.Contains("firefox") || name.Contains("palemoon") || name.Contains("basilisk"))
                    return 70;

                // Heavy apps (games, creative, IDEs)
                if (name.Contains("unity") || name.Contains("unreal") || name.Contains("blender") ||
                    name.Contains("photoshop") || name.Contains("premiere") || name.Contains("afterfx") ||
                    name.Contains("maya") || name.Contains("3dsmax") || name.Contains("zbrush"))
                    return 150;

                // Gaming platforms (Steam, Epic, etc.)
                if (name.Contains("steam") || name.Contains("epicgames") || name.Contains("gog"))
                    return 80;

                // Apps comuns — mínimo mais conservador
                return 30;
            }
        }

        public class ProcessEngineConfig
        {
            public string CpuPriority { get; set; } = "High";
            public int IoPriorityLevel { get; set; } = 3;
            // BUG CORRIGIDO (05/10/2026): o default ERA 1, que na escala oficial
            // MEMORY_PRIORITY_INFORMATION e' VERY_LOW — ou seja, um perfil novo do motor
            // por processo nascia com a pior prioridade de pagina possivel. 5 = NORMAL.
            public int PagePriorityLevel { get; set; } = 5;
            public bool TimerBoost { get; set; } = false;
            public bool EcoQoSEnabled { get; set; } = false;
            public bool ProBalance { get; set; } = false;
            public int ProBalanceCpuThreshold { get; set; } = 5;
            // Classic = BelowNormal temporário (estilo Process Lasso) | EcoQoS = API oficial Win11 | HardCap = Job Object hard cap
            public string ProBalanceMode { get; set; } = "Classic";
            public bool CpuLimitEnabled { get; set; } = false;
            public int CpuLimitPercent { get; set; } = 50;
            public bool NetworkBoost { get; set; } = false;
            public int ThreadMemoryPriority { get; set; } = 5;
            public bool ThreadEfficiencyMode { get; set; } = false;
            public bool GameClassInfo { get; set; } = true;
            public bool Win32PrioritySeparation { get; set; } = true;
            public bool DownloadBoostEnabled { get; set; } = false;
            public string DownloadBoostLevel { get; set; } = "Auto";
        }
    }


    public class CustomEngineConfig
    {
        public string CpuPriority { get; set; } = "High";
        // SEGURANCA (03/10/2026): o default ERA 1, que o codigo traduzia em I/O Critico (4).
        // I/O Critico coloca o processo na FRENTE de tudo na fila do disco — inclusive do
        // pagefile e do log do Windows — e o sistema inteiro passa a engasgar. Default agora 3 (High).
        public int IoPriorityLevel { get; set; } = 3;
        public int PagePriorityLevel { get; set; } = 4;
        public bool TimerBoost { get; set; } = false;
        public bool EcoQoSEnabled { get; set; } = false;
        public bool ProBalance { get; set; } = false;
        public int ProBalanceCpuThreshold { get; set; } = 5;
        public bool CpuLimitEnabled { get; set; } = false;
        public int CpuLimitPercent { get; set; } = 50;
        public bool NetworkBoost { get; set; } = false;
        public int ThreadMemoryPriority { get; set; } = 0;
        public bool ThreadEfficiencyMode { get; set; } = false;
        public bool GameClassInfo { get; set; } = true;
        public bool Win32PrioritySeparation { get; set; } = true;
        public bool DownloadBoostEnabled { get; set; } = false;
        public string DownloadBoostLevel { get; set; } = "Auto";
    }
}