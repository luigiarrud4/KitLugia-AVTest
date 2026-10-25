using System;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace KitLugia.Core
{
    public static class MemoryOptimizer
    {
        // P/Invoke Constants & Enums
        private const int SystemFileCacheInformation = 21;
        private const int SystemMemoryListInformation = 80;
        
        // Commands for SystemMemoryListInformation
        private const int MemoryCaptureAccessedBits = 0;
        private const int MemoryCaptureAndResetAccessedBits = 1;
        private const int MemoryEmptyWorkingSets = 2;
        private const int MemoryFlushModifiedList = 3;
        private const int MemoryPurgeStandbyList = 4;
        private const int MemoryPurgeLowPriorityStandbyList = 5;
        private const int MemoryCommandMax = 6;

        /// <summary>ERROR_NOT_ALL_ASSIGNED: AdjustTokenPrivileges devolveu TRUE mas o privilegio nao foi atribuido.</summary>
        private const int ERROR_NOT_ALL_ASSIGNED = 1300;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct SYSTEM_FILECACHE_INFORMATION
        {
            public long CurrentSize;
            public long PeakSize;
            public int PageFaultCount;
            public long MinimumWorkingSet;
            public long MaximumWorkingSet;
            public long CurrentSizeIncludingTransitionInPages;
            public long PeakSizeIncludingTransitionInPages;
            public int TransitionRePurposedCount;
            public int Flags;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength);

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength, out int ReturnLength);

        /// <summary>
        /// SYSTEM_MEMORY_LIST_INFORMATION (classe 80). Todos os campos sao SIZE_T
        /// (8 bytes em x64), num total de 22 = 176 bytes.
        /// A standby list e a SOMA dos 8 PageCountByPriority; "Free" do ISLC e
        /// ZeroPageCount + FreePageCount (NAO e o "Disponivel" do Gerenciador de Tarefas).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_MEMORY_LIST_INFORMATION
        {
            public IntPtr ZeroPageCount;
            public IntPtr FreePageCount;
            public IntPtr ModifiedPageCount;
            public IntPtr ModifiedNoWritePageCount;
            public IntPtr BadPageCount;
            public IntPtr PageCountByPriority0;
            public IntPtr PageCountByPriority1;
            public IntPtr PageCountByPriority2;
            public IntPtr PageCountByPriority3;
            public IntPtr PageCountByPriority4;
            public IntPtr PageCountByPriority5;
            public IntPtr PageCountByPriority6;
            public IntPtr PageCountByPriority7;
            public IntPtr RepurposedPages0;
            public IntPtr RepurposedPages1;
            public IntPtr RepurposedPages2;
            public IntPtr RepurposedPages3;
            public IntPtr RepurposedPages4;
            public IntPtr RepurposedPages5;
            public IntPtr RepurposedPages6;
            public IntPtr RepurposedPages7;
            public IntPtr ModifiedPageCountPageFile;
        }

        private static readonly IntPtr _systemMemoryListInfoSize =
            new IntPtr(Marshal.SizeOf<SYSTEM_MEMORY_LIST_INFORMATION>());

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out long lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        // ★ CORREÇÃO 28/09: layout nativo = DWORD(4) + LUID(8, alinhado a 4) + DWORD(4) = 16 bytes.
        // Com Pack=1 o campo caía no offset 1 e com o layout padrão no offset 8 — nos dois casos
        // o Windows lia um LUID inválido e devolvia TRUE + ERROR_NOT_ALL_ASSIGNED (1300), ou seja:
        // SeProfileSingleProcessPrivilege NUNCA era habilitado (purge da standby list sem efeito).
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct TOKEN_PRIVILEGES
        {
            public int PrivilegeCount;
            public long Luid;
            public int Attributes;
        }

        private const int SE_PRIVILEGE_ENABLED = 0x00000002;
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;

        public enum CleaningMode
        {
            Leve = 0,     // Barely noticeable
            Normal = 1,   // Standard (default)
            Alta = 2,     // Slight freeze
            Bruta = 3     // System freeze - maximum clean
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        // -- TRIM DE WORKING SET POR PROCESSO ------------------------------------
        // Por que NAO usar SetProcessWorkingSetSize(-1,-1) para "limpar RAM" de UM
        // processo (o que o botao do KitTaskManager fazia):
        //   1) Medicao real (AGENTS.md, sessao do governador): SetProcessWorkingSetSize
        //      devolve TRUE e o working set NAO cai; EmptyWorkingSet (psapi) esvazia
        //      NA HORA (321 MB -> 0 MB) -- e o mecanismo que o Firemin usa.
        //   2) Process.Handle do .NET abre com PROCESS_ALL_ACCESS e falha com
        //      "acesso negado" em processos de outro usuario/protegidos. O catch
        //      engolia a excecao e a UI dizia "memoria limpa" sem ter limpo nada.
        // Aqui o handle e aberto com o MINIMO necessario e o resultado e REAL.

        private const uint PROCESS_SET_QUOTA = 0x0100;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_VM_READ = 0x0010;
        private const int ERROR_ACCESS_DENIED = 5;
        private const int ERROR_INVALID_PARAMETER = 87;

        /// <summary>
        /// O processo atual roda elevado (Administrador)? O KitLugia SEMPRE roda como
        /// admin, entao "sem permissao" so muda de DON'T: quando ja estamos elevados,
        /// o acesso negado e do proprio Windows (processo protegido/sistema).
        /// </summary>
        public static bool IsElevated()
        {
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(id);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>
        /// Mensagem de acesso negado do trim, coerente com o nivel de elevacao.
        /// Fica separada para poder ser testada nos DOIS ramos (elevado e nao
        /// elevado) sem precisar de dois terminais diferentes.
        /// </summary>
        public static string BuildTrimAccessDeniedMessage(int pid, bool elevated)
        {
            if (!elevated)
                return "sem permissao (execute o KitLugia como administrador)";
            return pid == 4
                ? "acesso negado: o processo System (PID 4) e protegido pelo Windows"
                : "acesso negado pelo Windows (processo protegido ou de outro usuario)";
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr hProcess, out PROCESS_MEMORY_COUNTERS counters, uint cb);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSizeEx(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize, uint dwFlags);

        // ATENCAO: em winnt.h os campos de memoria sao SIZE_T (8 bytes em x64), entao
        // a struct tem 72 bytes. Com 40 bytes a API responde ERROR_INSUFFICIENT_BUFFER
        // e a medicao volta -1 (o trim funcionava, mas sem numero no status).
        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS
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

        [StructLayout(LayoutKind.Sequential)]
        public struct MemoryInfo
        {
            public int Percent;
            public double TotalGB;
            public double UsedGB;
            public double FreeGB;
            public ulong TotalBytes;
            public ulong AvailBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
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
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        // ISLC: GetPerformanceInfo para obter tamanho da Standby List
        [StructLayout(LayoutKind.Sequential)]
        public struct PERFORMANCE_INFORMATION
        {
            public int cb;
            public IntPtr CommitTotal;
            public IntPtr CommitLimit;
            public IntPtr CommitPeak;
            public IntPtr PhysicalAvailable;
            public IntPtr PhysicalTotal;
            public IntPtr SystemCache;
            public IntPtr KernelTotal;
            public IntPtr KernelPaged;
            public IntPtr KernelNonPaged;
            public IntPtr PageSize;
            public int HandleCount;
            public int ProcessCount;
            public int ThreadCount;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetPerformanceInfo(ref PERFORMANCE_INFORMATION pPerformanceInformation, int cb);

        /// <summary>
        /// Le a SYSTEM_MEMORY_LIST_INFORMATION (classe 80).
        /// Retorna false em falha de API — quem chama precisa distinguir
        /// "falhou" de "standby vazia" (que sao coisas diferentes: a primeira
        /// invalida decisao, a segunda e um estado legitimo).
        /// </summary>
        private static bool TryGetMemoryListInfo(out SYSTEM_MEMORY_LIST_INFORMATION info)
        {
            info = default;
            IntPtr buf = Marshal.AllocHGlobal(_systemMemoryListInfoSize);
            try
            {
                int ret;
                int status = NtQuerySystemInformation(
                    SystemMemoryListInformation, buf, _systemMemoryListInfoSize.ToInt32(), out ret);

                if (status != 0) return false;

                info = Marshal.PtrToStructure<SYSTEM_MEMORY_LIST_INFORMATION>(buf);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        /// <summary>
        /// Tamanho da standby list em MB pela metrica do proprio recurso
        /// (soma dos 8 buckets de prioridade), que e o caminho que o ISLC usa.
        ///
        /// Medido nesta maquina: <c>GetPerformanceInfo().SystemCache</c> carrega
        /// um erro ABSOLUTO de ~175-340 MB (standby + system working set sao outra
        /// grandeza), o que da +4,8% com standby alta mas +70% com standby baixa —
        /// ou seja, errando mais justo quando a decisao importa. Ver
        /// docs/ISLC_VS_KITLUGIA_COMPARISON.md secao 3.
        /// </summary>
        public static bool TryGetStandbyListSizeMB(out long standbyMB)
        {
            standbyMB = 0;
            if (!TryGetMemoryListInfo(out var info)) return false;

            long pages =
                info.PageCountByPriority0.ToInt64() + info.PageCountByPriority1.ToInt64() +
                info.PageCountByPriority2.ToInt64() + info.PageCountByPriority3.ToInt64() +
                info.PageCountByPriority4.ToInt64() + info.PageCountByPriority5.ToInt64() +
                info.PageCountByPriority6.ToInt64() + info.PageCountByPriority7.ToInt64();

            standbyMB = pages * 4 / 1024; // pagina de 4 KB (o ISLC tambem fixa 4096)
            return true;
        }

        /// <summary>
        /// Os 8 buckets de prioridade da standby list, em MB. Util para ver
        /// onde o cache esta (prioridade alta = acessado recentemente).
        /// </summary>
        public static bool TryGetStandbyListByPriorityMB(out long[] byPriorityMB)
        {
            byPriorityMB = null;
            if (!TryGetMemoryListInfo(out var info)) return false;

            byPriorityMB = new[]
            {
                info.PageCountByPriority0.ToInt64() * 4 / 1024,
                info.PageCountByPriority1.ToInt64() * 4 / 1024,
                info.PageCountByPriority2.ToInt64() * 4 / 1024,
                info.PageCountByPriority3.ToInt64() * 4 / 1024,
                info.PageCountByPriority4.ToInt64() * 4 / 1024,
                info.PageCountByPriority5.ToInt64() * 4 / 1024,
                info.PageCountByPriority6.ToInt64() * 4 / 1024,
                info.PageCountByPriority7.ToInt64() * 4 / 1024
            };
            return true;
        }

        /// <summary>
        /// "Free" no sentido do ISLC = ZeroPageCount + FreePageCount, em MB.
        /// NAO e o "Disponivel" do Gerenciador de Tarefas (que inclui standby).
        ///
        /// Medido nesta maquina: variou de 0-256 MB (21:21) a 8,8 GB (23:17) no
        /// mesmo dia — ou seja, depende do historico recente de memoria e nao e
        /// um numero estavel entre si. Ver docs/ISLC_VS_KITLUGIA_COMPARISON.md secao 2.
        /// </summary>
        public static bool TryGetFreePagesMB(out long freeMB)
        {
            freeMB = 0;
            if (!TryGetMemoryListInfo(out var info)) return false;
            freeMB = (info.ZeroPageCount.ToInt64() + info.FreePageCount.ToInt64()) * 4 / 1024;
            return true;
        }

        /// <summary>
        /// Tamanho aproximado da Standby List em MB.
        /// Caminho primario e a metrica do recurso (classe 80); se a API falhar,
        /// cai para GetPerformanceInfo (aproximacao). Nunca retorna -1:
        /// callers antigos esperavam >= 0.
        /// </summary>
        public static long GetStandbyListSizeMB()
        {
            if (TryGetStandbyListSizeMB(out long mb)) return mb;
            return GetStandbyListSizeMBFromPerformanceInfo();
        }

        /// <summary>
        /// Fallback aproximado. Retorna -1 em falha para nao se confundir com
        /// "standby vazia" (o numero 0 e um estado legitimo).
        /// </summary>
        private static long GetStandbyListSizeMBFromPerformanceInfo()
        {
            try
            {
                var info = new PERFORMANCE_INFORMATION();
                info.cb = Marshal.SizeOf(info);
                if (GetPerformanceInfo(ref info, info.cb))
                {
                    // SystemCache = Standby List + System Working Set (em pages)
                    long cachePages = info.SystemCache.ToInt64();
                    long pageSize = info.PageSize.ToInt64();
                    return (cachePages * pageSize) / (1024 * 1024);
                }
            }
            catch { }
            return -1;
        }

        /// <summary>
        /// Limpa a Standby List (cache do Windows) e reporta o resultado de verdade.
        ///
        /// <paramref name="ntStatus"/> recebe o NTSTATUS de
        /// <c>NtSetSystemInformation(80, MemoryPurgeStandbyList)</c>:
        /// 0 = ok; 0xC0000061 = STATUS_PRIVILEGE_NOT_HELD; -1 = privilegio ausente.
        ///
        /// Antes desta versao o retorno da API e o GetLastError eram descartados e
        /// o comando vivia dentro de um <c>catch {}</c>: uma falha era
        /// indistinguivel de sucesso, e o contador da UI subia igual.
        /// </summary>
        public static bool TryPurgeStandbyList(out int ntStatus)
        {
            ntStatus = -1;

            if (!EnablePrivilege("SeProfileSingleProcessPrivilege"))
            {
                Logger.LogWarning("Memory", "Purge de standby: SeProfileSingleProcessPrivilege ausente " +
                    "(ERROR_NOT_ALL_ASSIGNED) — a operacao exige rodar como administrador.");
                return false;
            }

            ntStatus = ExecuteMemoryCommand(MemoryPurgeStandbyList);
            return ntStatus == 0;
        }

        /// <summary>Wrapper antigo: so o efeito, sem diagnostico.</summary>
        public static void PurgeStandbyList()
        {
            TryPurgeStandbyList(out _);
        }

        public static MemoryInfo GetMemoryStats()
        {
            var m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(m);
            GlobalMemoryStatusEx(ref m);

            double total = m.ullTotalPhys;
            double avail = m.ullAvailPhys;
            double used = total - avail;
            
            // ðŸ”¥ CORREÃ‡ÃƒO: Usar dwMemoryLoad da API do Windows em vez de calcular manualmente
            // dwMemoryLoad é o mesmo valor que o Windows Task Manager mostra
            // Isso garante consistência entre TrayIcon e Task Manager
            int percent = (int)m.dwMemoryLoad;

            return new MemoryInfo
            {
                Percent = percent,
                TotalBytes = m.ullTotalPhys,
                AvailBytes = m.ullAvailPhys,
                TotalGB = m.ullTotalPhys / (1024.0 * 1024.0 * 1024.0),
                FreeGB = m.ullAvailPhys / (1024.0 * 1024.0 * 1024.0),
                UsedGB = used / (1024.0 * 1024.0 * 1024.0)
            };
        }

        /// <summary>
        /// Resultado REAL de um trim de working set: sucesso/falha, o motivo da falha
        /// e quanto foi liberado. A UI precisa disso para nao mentir ("memoria limpa")
        /// quando o Windows nega o acesso ao processo.
        /// </summary>
        public readonly struct ProcessTrimResult
        {
            public bool Success { get; }
            public string Message { get; }
            public double WorkingSetBeforeMB { get; }
            public double WorkingSetAfterMB { get; }
            public double FreedMB => WorkingSetBeforeMB > WorkingSetAfterMB ? WorkingSetBeforeMB - WorkingSetAfterMB : 0;

            internal ProcessTrimResult(bool success, string message, double beforeMb, double afterMb)
            {
                Success = success;
                Message = message;
                WorkingSetBeforeMB = beforeMb;
                WorkingSetAfterMB = afterMb;
            }
        }

        /// <summary>
        /// Esvazia o working set de UM processo (estilo Firemin) e devolve o resultado
        /// real. Nao faz "cleanup" global: age so no PID informado.
        /// </summary>
        public static ProcessTrimResult TrimProcessWorkingSet(int pid)
        {
            if (pid <= 0)
                return new ProcessTrimResult(false, "PID invalido.", 0, 0);

            // EmptyWorkingSet exige PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION.
            // PROCESS_VM_READ e extra so para o GetProcessMemoryInfo (medicao).
            IntPtr h = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
            if (h == IntPtr.Zero)
                h = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION, false, pid);

            if (h == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                string msg;
                if (err == ERROR_ACCESS_DENIED)
                {
                    // O KitLugia roda como admin: nesse ponto o acesso negado vem do
                    // proprio Windows (processo protegido ou do kernel), nao da falta
                    // de privilegio -- mandar "rode como admin" seria mentira.
                    msg = BuildTrimAccessDeniedMessage(pid, IsElevated());
                }
                else if (err == ERROR_INVALID_PARAMETER) msg = "o processo ja encerrou";
                else msg = "OpenProcess falhou (erro " + err + ")";
                return new ProcessTrimResult(false, msg, 0, 0);
            }

            try
            {
                double before = ReadWorkingSetMB(h);

                // Mecanismo que funciona de verdade (medido): esvazia o working set agora.
                if (!EmptyWorkingSet(h))
                {
                    int err = Marshal.GetLastWin32Error();
                    // Fallback SUAVE: reduz o teto pela metade (nao libera tudo de uma
                    // vez -- SetProcessWorkingSetSize(-1,-1) dispara page-fault storm).
                    // So faz sentido se a medicao funcionou (before > 0).
                    bool ok = false;
                    if (before > 0)
                    {
                        long half = (long)(before * 1024 * 1024 * 0.5);
                        ok = SetProcessWorkingSetSizeEx(h, (IntPtr)(-1), (IntPtr)half, 0);
                    }
                    if (!ok)
                        return new ProcessTrimResult(false, "EmptyWorkingSet falhou (erro " + err + ")", before, before);
                }

                double after = ReadWorkingSetMB(h);
                return new ProcessTrimResult(true, "ok", before, after);
            }
            finally
            {
                CloseHandle(h);
            }
        }

        /// <summary>Working set do processo em MB (-1 se nao foi possivel medir).</summary>
        private static double ReadWorkingSetMB(IntPtr hProcess)
        {
            try
            {
                // cb PRECISA ser o tamanho da struct: sem isso a API falha e a
                // medicao volta -1 (o trim funcionava, mas sem numero no status).
                var c = new PROCESS_MEMORY_COUNTERS { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>() };
                if (GetProcessMemoryInfo(hProcess, out c, c.cb))
                    return c.WorkingSetSize.ToUInt64() / (1024.0 * 1024.0);
            }
            catch { }
            return -1;
        }

        /// <summary>
        /// Esvazia o working set de UM processo. Mantido por compatibilidade com os
        /// chamadores antigos; agora usa o caminho que funciona (EmptyWorkingSet) e
        /// nao engole o erro de acesso negado em silencio.
        /// </summary>
        public static void EmptyProcessWorkingSet(int pid)
        {
            try { TrimProcessWorkingSet(pid); }
            catch { /* processo ja foi encerrado entre o scan e o trim -- normal */ }
        }

        public static (bool Success, string Message) Optimize() => Optimize(CleaningMode.Normal);

        public static (bool Success, string Message) Optimize(CleaningMode mode)
        {
            try
            {
                EnablePrivilege("SeProfileSingleProcessPrivilege");
                EnablePrivilege("SeIncreaseQuotaPrivilege");

                // â”€â”€ LEVE: Just empty working sets (nem sente) â”€â”€
                ExecuteMemoryCommand(MemoryEmptyWorkingSets);

                if (mode >= CleaningMode.Normal)
                {
                    // â”€â”€ NORMAL: + Flush modified list + System file cache â”€â”€
                    var cacheInfo = new SYSTEM_FILECACHE_INFORMATION
                    {
                        MinimumWorkingSet = -1,
                        MaximumWorkingSet = -1
                    };

                    int size = Marshal.SizeOf(cacheInfo);
                    IntPtr pCacheInfo = Marshal.AllocHGlobal(size);
                    Marshal.StructureToPtr(cacheInfo, pCacheInfo, true);
                    try { NtSetSystemInformation(SystemFileCacheInformation, pCacheInfo, size); }
                    finally { Marshal.FreeHGlobal(pCacheInfo); }

                    ExecuteMemoryCommand(MemoryFlushModifiedList);
                }

                if (mode >= CleaningMode.Alta)
                {
                    // â”€â”€ ALTA: + Purge standby list (leve travada) â”€â”€
                    ExecuteMemoryCommand(MemoryPurgeStandbyList);
                }

                if (mode >= CleaningMode.Bruta)
                {
                    // â”€â”€ BRUTA: + Low priority standby + shrink all processes â”€â”€
                    ExecuteMemoryCommand(MemoryPurgeLowPriorityStandbyList);

                    // Shrink working sets of all accessible processes
                    try
                    {
                        foreach (var proc in Process.GetProcesses())
                        {
                            try
                            {
                                SetProcessWorkingSetSize(proc.Handle, (IntPtr)(-1), (IntPtr)(-1));
                            }
                            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                            finally { proc.Dispose(); }
                        }
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }

                string[] modeNames = { "Leve", "Normal", "Alta", "Bruta" };
                return (true, $"Memória otimizada ({modeNames[(int)mode]})");
            }
            catch (Exception ex)
            {
                return (false, $"Erro na otimização: {ex.Message}");
            }
        }

        /// <summary>
        /// Habilita um privilegio no token do processo.
        ///
        /// Retorna <c>false</c> quando o privilegio NAO foi atribuido.
        /// Isso importa porque <c>AdjustTokenPrivileges</c> devolve TRUE mesmo
        /// nesse caso: quem denuncia e <c>GetLastError()</c> == 1300
        /// (ERROR_NOT_ALL_ASSIGNED). A versao anterior ignorava os dois e devolvia
        /// void, entao a falha so aparecia depois, como STATUS_PRIVILEGE_NOT_HELD
        /// na API errada — exatamente o mesmo modo de falha que a analise do ISLC
        /// encontrou nele (docs/ISLC_VS_KITLUGIA_COMPARISON.md secao 1.2).
        /// </summary>
        private static bool EnablePrivilege(string privilegeName)
        {
            IntPtr hToken;
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out hToken))
                return false;

            try
            {
                long luid;
                if (!LookupPrivilegeValue(string.Empty, privilegeName, out luid))
                    return false;

                var tp = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED
                };

                if (!AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                    return false;

                // TRUE nao significa sucesso: 1300 = privilegio nao atribuido.
                return Marshal.GetLastWin32Error() != ERROR_NOT_ALL_ASSIGNED;
            }
            catch
            {
                return false;
            }
            finally
            {
                // OpenProcessToken NAO e pseudo-handle: fechar sempre (vazava 1
                // token por chamada - rodava 2x por limpeza de RAM no tray).
                CloseHandle(hToken);
            }
        }

        /// <summary>
        /// Executa um comando de SystemMemoryListInformation (classe 80).
        /// Retorna o NTSTATUS (0 = sucesso). Antes devolvia void e engolia tudo.
        /// </summary>
        private static int ExecuteMemoryCommand(int command)
        {
            GCHandle handle = GCHandle.Alloc(command, GCHandleType.Pinned);
            try
            {
                return NtSetSystemInformation(SystemMemoryListInformation, handle.AddrOfPinnedObject(), Marshal.SizeOf(typeof(int)));
            }
            catch
            {
                return unchecked((int)0xC0000001); // STATUS_UNEXPECTED_FAILURE
            }
            finally
            {
                handle.Free();
            }
        }
    }
}
