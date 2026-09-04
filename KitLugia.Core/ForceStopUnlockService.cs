using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace KitLugia.Core
{
    /// <summary>
    /// Result of analyzing a file/folder for blocking processes.
    /// </summary>
    public class BlockingProcessInfo
    {
        public int Pid { get; set; }
        public string ProcessName { get; set; } = "";
        public string ExecutablePath { get; set; } = "";
        public string HandleId { get; set; } = "";
        public string HandleType { get; set; } = "";
        public string AccessRights { get; set; } = "";
        public string LockedPath { get; set; } = "";
        public bool IsSystemProcess { get; set; }
        public bool IsSelected { get; set; } = true;

        public string DisplayLabel =>
            $"{ProcessName} (PID: {Pid})" +
            (string.IsNullOrEmpty(HandleType) ? "" : $" — {HandleType}");

        public string DetailLabel =>
            $"Handle: {HandleId} | Acesso: {AccessRights} | {LockedPath}";
    }

    /// <summary>
    /// Result of an unlock operation.
    /// </summary>
    public class UnlockResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public int HandlesClosed { get; set; }
        public int ProcessesKilled { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    /// <summary>
    /// Native C# Force Stop Unlock service.
    /// Uses three approaches in order of preference:
    /// 1. Restart Manager API (RmShutdown) — cleanest, closes handles without killing processes
    /// 2. Handle tool (handle64.exe) — identifies and closes individual handles
    /// 3. Process kill — last resort, terminates the blocking process entirely
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class ForceStopUnlockService
    {
        // ─── Restart Manager P/Invoke ───────────────────────────────────
        private const int CCH_RM_SESSION_KEY = 256;
        private const int ERROR_SUCCESS = 0;
        private const int ERROR_MORE_DATA = 234;
        private const int ERROR_MORE_FILES = 8;

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint pSessionHandle,
            uint nFiles, string[] rgsFileNames,
            uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications,
            uint nServices, string[]? rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmShutdown(uint pSessionHandle,
            int lActionFlags,
            RmWriteStatusCallback? fnStatus);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint pSessionHandle,
            out uint pnProcInfoNeeded,
            ref uint pnProcInfo,
            [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
            ref uint lpdwRebootReasons);

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO
        {
            [FieldOffset(0)] public RM_UNIQUE_PROCESS Process;
            [FieldOffset(16)] [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
            [FieldOffset(528)] [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
            [FieldOffset(660)] public int ApplicationType;
            [FieldOffset(664)] public uint AppStatus;
            [FieldOffset(668)] public uint TSSessionId;
            [FieldOffset(672)] [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void RmWriteStatusCallback(uint nPercentComplete);

        // ─── SeDebugPrivilege (necessário p/ abrir/matar processos de outros usuários) ──
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessTokenDbg(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool LookupPrivilegeValueDbg(string? systemName, string name, out long luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivilegesDbg(IntPtr tokenHandle, bool disableAll,
            ref TOKEN_PRIVILEGES_DBG newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES_DBG
        {
            public int PrivilegeCount;
            public long Luid;
            public int Attributes;
        }

        private const uint TOKEN_ADJUST_PRIVILEGES_DBG = 0x0020;
        private const uint TOKEN_QUERY_DBG = 0x0008;
        private const int SE_PRIVILEGE_ENABLED_DBG = 0x0002;

        /// <summary>
        /// Habilita SeDebugPrivilege no token do processo. Sem ele, OpenProcess/
        /// DuplicateHandle falham (acesso negado) em processos de outros usuários e
        /// o scan nativo não acha NENHUM handle — a causa clássica de "não acha nada".
        /// Requer Administrador; retorna false sem admin.
        /// </summary>
        public static bool EnableDebugPrivilege()
        {
            try
            {
                if (!OpenProcessTokenDbg(GetCurrentProcessNative(), TOKEN_ADJUST_PRIVILEGES_DBG | TOKEN_QUERY_DBG, out var token))
                {
                    Logger.Log("[FORCE STOP] SeDebugPrivilege: OpenProcessToken falhou (sem admin?)");
                    return false;
                }
                try
                {
                    if (!LookupPrivilegeValueDbg(null, "SeDebugPrivilege", out long luid))
                        return false;
                    var tp = new TOKEN_PRIVILEGES_DBG { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED_DBG };
                    return AdjustTokenPrivilegesDbg(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                }
                finally { CloseHandleNative(token); }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FORCE STOP] SeDebugPrivilege ERRO: {ex.Message}");
                return false;
            }
        }

        // ─── NtTerminateProcess (fallback de kill quando Process.Kill dá acesso negado) ──
        [DllImport("ntdll.dll")]
        private static extern int NtTerminateProcess(IntPtr hProcess, int exitStatus);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr hProcess, int ProcessInformationClass,
            ref PROCESS_PROTECTION_INFORMATION ProcessInformation, uint ProcessInformationLength, out uint ReturnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_PROTECTION_INFORMATION
        {
            public byte Protection;
            public byte Flags;
        }

        private const int ProcessProtectionInformation = 61;
        private const uint PROCESS_TERMINATE_NATIVE = 0x0001;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION_NATIVE = 0x1000;

        // ─── System process names that should never be killed ────────────
        private static readonly HashSet<string> SystemProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon",
            "lsass", "services", "svchost", "dwm", "fontdrvhost", "sihost",
            "taskhostw", "RuntimeBroker", "ShellExperienceHost", "SearchUI",
            "ctfmon", "csrsrv", "msdtc", "WmiPrvSE", "msmpeng", "NisSrv",
            "MsMpEng", "MpCmdRun"
        };

        // ─── Handle tool path ───────────────────────────────────────────
        private static string GetHandle64Path()
        {
            string baseDir = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "External", "ForceStopUnlock");
            string path = Path.Combine(baseDir, "handle64.exe");
            if (File.Exists(path)) return path;
            // Fallback: check KitLugia base folder
            string alt = Path.Combine(
                SystemTweaks.KitLugiaBaseFolder, "External", "ForceStopUnlock", "handle64.exe");
            return File.Exists(alt) ? alt : path;
        }

        // ─── Public API ─────────────────────────────────────────────────

        /// <summary>
        /// Analyze a file or folder to find all blocking processes/handles.
        /// Tries Restart Manager first, then falls back to Handle tool.
        /// </summary>
        public static List<BlockingProcessInfo> FindBlockingProcesses(string targetPath)
        {
            Logger.Log($"[FORCE STOP] === FindBlockingProcesses iniciado para: {targetPath}");

            // Log admin status
            bool isAdmin = SystemUtils.IsRunningAsAdministrator();
            Logger.Log($"[FORCE STOP] Executando como Administrador: {isAdmin}");

            // SeDebugPrivilege ANTES do scan nativo — sem ele, OpenProcess/DuplicateHandle
            // falham em processos de outros usuários ("não acha nada" mesmo com o arquivo travado).
            EnableDebugPrivilege();

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                Logger.Log($"[FORCE STOP] Caminho vazio.");
                return new List<BlockingProcessInfo>();
            }

            if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
            {
                // .NET Exists retorna FALSE em caminhos com ACL negada (ex: Windows.old) —
                // o probe nativo distingue "não existe" de "existe mas negado".
                int probe = FileTakeOwnership.ProbePath(targetPath, out bool pExists, out bool pIsDir, out int errCode);
                if (!pExists && probe != 5 && probe != 21)
                {
                    Logger.Log($"[FORCE STOP] Caminho invalido ou nao existe: {targetPath} (erro {probe})");
                    return new List<BlockingProcessInfo>();
                }
                Logger.Log($"[FORCE STOP] Caminho existe mas ACL nega leitura (erro {errCode}) — continuando scan nativo.");
            }

            bool isDir = Directory.Exists(targetPath);
            Logger.Log($"[FORCE STOP] Tipo: {(isDir ? "Pasta" : "Arquivo")}");

            // List folder contents if target is a directory
            if (isDir)
            {
                try
                {
                    Logger.Log($"[FORCE STOP] Listando conteudo da pasta: {targetPath}");
                    int fileCount = 0;
                    foreach (var file in Directory.EnumerateFiles(targetPath, "*", SearchOption.AllDirectories))
                    {
                        var fi = new FileInfo(file);
                        Logger.Log($"[FORCE STOP]   Arquivo: {file} ({fi.Length} bytes, {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss})");
                        fileCount++;
                    }
                    foreach (var dir in Directory.EnumerateDirectories(targetPath))
                    {
                        Logger.Log($"[FORCE STOP]   Pasta: {dir}");
                    }
                    Logger.Log($"[FORCE STOP] Total de arquivos encontrados: {fileCount}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"[FORCE STOP] Erro ao listar pasta: {ex.Message}");
                }
            }

            // Normalize: for folders, scan contents recursively
            var targets = Directory.Exists(targetPath)
                ? GetFilesInFolder(targetPath, maxDepth: 3)
                : new[] { targetPath };

            Logger.Log($"[FORCE STOP] Arquivos alvo para analise: {targets.Length}");
            foreach (var t in targets.Take(20))
                Logger.Log($"[FORCE STOP]   -> {t}");
            if (targets.Length > 20)
                Logger.Log($"[FORCE STOP]   ... e mais {targets.Length - 20} arquivos");

            if (targets.Length == 0)
            {
                Logger.Log($"[FORCE STOP] Nenhum arquivo encontrado no caminho.");
                return new List<BlockingProcessInfo>();
            }

            var results = new List<BlockingProcessInfo>();

            // Approach 1: Restart Manager API
            Logger.Log($"[FORCE STOP] Etapa 1/3: Restart Manager API...");
            var rmResults = FindViaRestartManager(targets);
            Logger.Log($"[FORCE STOP] Restart Manager encontrou: {rmResults.Count} processo(s)");
            foreach (var r in rmResults)
                Logger.Log($"[FORCE STOP]   RM: {r.ProcessName} (PID {r.Pid})");
            if (rmResults.Count > 0)
                results.AddRange(rmResults);

            // Approach 2: Native handle enumeration via NtQuerySystemInformation
            // Finds ALL handle types: File, Section (memory-mapped), Key, etc.
            // More reliable than handle64.exe and doesn't require external tools.
            Logger.Log($"[FORCE STOP] Etapa 2/4: Native handle enumeration (NtQuerySystemInformation)...");
            var nativeResults = FindViaNativeHandles(targetPath);
            Logger.Log($"[FORCE STOP] Native handles encontrou: {nativeResults.Count} handle(s)");
            foreach (var h in nativeResults)
                Logger.Log($"[FORCE STOP]   Native: {h.ProcessName} (PID {h.Pid}) handle={h.HandleId} tipo={h.HandleType} path={h.LockedPath}");
            foreach (var h in nativeResults)
            {
                if (!results.Any(r => r.Pid == h.Pid && r.HandleId == h.HandleId))
                    results.Add(h);
            }

            // Approach 2b: Handle tool (fallback if native enumeration found nothing)
            if (nativeResults.Count == 0)
            {
                Logger.Log($"[FORCE STOP] Etapa 2b: Handle tool (handle64.exe) como fallback...");
                var handleResults = FindViaHandleTool(targets, targetPath);
                Logger.Log($"[FORCE STOP] Handle tool encontrou: {handleResults.Count} handle(s)");
                foreach (var h in handleResults)
                    Logger.Log($"[FORCE STOP]   Handle: {h.ProcessName} (PID {h.Pid}) handle={h.HandleId} tipo={h.HandleType}");
                foreach (var h in handleResults)
                {
                    if (!results.Any(r => r.Pid == h.Pid && r.HandleId == h.HandleId))
                        results.Add(h);
                }
            }

            // Approach 3: Driver scan (finds .sys drivers locking the file)
            Logger.Log($"[FORCE STOP] Etapa 3/4: Driver scan (.sys)...");
            try
            {
                var drivers = DriverUnlockService.FindBlockingDrivers(targetPath);
                Logger.Log($"[FORCE STOP] Driver scan encontrou: {drivers.Count} driver(es)");
                foreach (var d in drivers)
                {
                    Logger.Log($"[FORCE STOP]   Driver: {d.DriverName} servico={d.ServiceName} estado={d.CurrentState} caminho={d.DriverPath}");
                    results.Add(new BlockingProcessInfo
                    {
                        Pid = d.Pid,
                        ProcessName = d.DriverName,
                        ExecutablePath = d.DriverPath,
                        HandleId = $"DRV:{d.ServiceName}",
                        HandleType = "Driver (.sys)",
                        AccessRights = d.CurrentState,
                        LockedPath = targetPath,
                        IsSystemProcess = false,
                        IsSelected = true
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FORCE STOP] ERRO no driver scan: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                    Logger.Log($"[FORCE STOP]   Inner: {ex.InnerException.Message}");
                Logger.Log($"[FORCE STOP]   Stack: {ex.StackTrace}");
            }

            Logger.Log($"[FORCE STOP] === Total de bloqueadores encontrados: {results.Count}");
            return results.OrderByDescending(r => r.IsSystemProcess ? 0 : 1)
                          .ThenBy(r => r.HandleType)
                          .ThenBy(r => r.ProcessName)
                          .ToList();
        }

        /// <summary>
        /// Close handles and/or kill processes to unlock the target.
        /// </summary>
        public static UnlockResult Unlock(string targetPath, IEnumerable<BlockingProcessInfo> targets, bool deleteTarget = false)
        {
            Logger.Log($"[FORCE STOP] === Unlock iniciado para: {targetPath} (deleteTarget={deleteTarget})");
            Logger.Log($"[FORCE STOP] Admin: {SystemUtils.IsRunningAsAdministrator()}");

            var result = new UnlockResult();
            var targetList = targets.Where(t => t.IsSelected).ToList();

            // SeDebugPrivilege antes de qualquer kill/close de handle
            EnableDebugPrivilege();

            Logger.Log($"[FORCE STOP] Targets selecionados: {targetList.Count}");
            foreach (var t in targetList)
                Logger.Log($"[FORCE STOP]   -> {t.DisplayLabel} | {t.DetailLabel}");

            if (targetList.Count == 0)
            {
                Logger.Log($"[FORCE STOP] Nenhum processo selecionado.");
                result.Message = "Nenhum processo selecionado para liberar.";
                return result;
            }

            // Phase 1: Try Restart Manager shutdown (closes file handles cleanly)
            // Note: RM closes file handles but does NOT unload kernel drivers!
            // We must continue to Phase 2-5 even if RM succeeds.
            var rmPids = targetList.Select(t => t.Pid).Distinct().ToList();
            bool rmOk = TryRestartManagerShutdown(targetPath, rmPids);
            if (rmOk)
            {
                result.HandlesClosed += targetList.Count;
                result.Message = $"{targetList.Count} handle(s) de arquivo liberado(s) via Restart Manager. ";
                Logger.Log($"[FORCE STOP] Restart Manager fechou handles com sucesso. Continuando para driver unload...");
            }
            else
 {
                Logger.Log($"[FORCE STOP] Restart Manager falhou ou nenhum handle RM encontrado.");
            }

            // Phase 2: Kill processes from the same folder (e.g., goodbyedpi.exe that loaded the driver)
            Logger.Log($"[FORCE STOP] Phase 2: Tentando matar processos da mesma pasta...");
            try
            {
                string targetDir = Directory.Exists(targetPath) ? targetPath : Path.GetDirectoryName(targetPath) ?? "";
                if (!string.IsNullOrEmpty(targetDir))
                {
                    var allProcs = Process.GetProcesses();
                    foreach (var proc in allProcs)
                    {
                        try
                        {
                            string exePath = proc.MainModule?.FileName ?? "";
                            if (!string.IsNullOrEmpty(exePath) &&
                                exePath.StartsWith(targetDir, StringComparison.OrdinalIgnoreCase) &&
                                !SystemProcessNames.Contains(proc.ProcessName) &&
                                proc.Id != Environment.ProcessId && !proc.HasExited)
                            {
                                Logger.Log($"[FORCE STOP] Matando processo '{proc.ProcessName}' (PID {proc.Id}) da pasta alvo: {exePath}");
                                if (KillProcess(proc.Id))
                                    result.ProcessesKilled++;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FORCE STOP] Erro ao matar processos da pasta: {ex.Message}");
            }

            // Phase 3: Unload blocking drivers (.sys files)
            Logger.Log($"[FORCE STOP] Phase 3: Descarregando drivers (.sys)...");
            var driverTargets = targetList.Where(t => t.HandleId?.StartsWith("DRV:") == true).ToList();
            foreach (var drv in driverTargets)
            {
                string serviceName = drv.HandleId?.Replace("DRV:", "") ?? "";
                Logger.Log($"[FORCE STOP] Tentando descarregar driver '{serviceName}'...");

                // Try SCM stop + delete
                var (ok, msg) = DriverUnlockService.UnloadDriverViaScm(serviceName);
                if (ok)
                {
                    result.HandlesClosed++;
                    Logger.Log($"[FORCE STOP] Driver '{drv.ProcessName}' descarregado via SCM.");
                }
                else
                {
                    Logger.Log($"[FORCE STOP] SCM falhou para '{serviceName}': {msg}");
                    // Try NtUnloadDriver fallback
                    (ok, msg) = DriverUnlockService.UnloadDriverViaNtApi(serviceName);
                    if (ok)
                    {
                        result.HandlesClosed++;
                        Logger.Log($"[FORCE STOP] Driver '{drv.ProcessName}' descarregado via NtUnloadDriver.");
                    }
                    else
                    {
                        Logger.Log($"[FORCE STOP] NtUnloadDriver falhou para '{serviceName}': {msg}");
                        // Try sc stop via command line as last resort
                        try
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = "sc.exe",
                                Arguments = $"stop \"{serviceName}\"",
                                RedirectStandardOutput = true,
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            using var scProc = Process.Start(psi);
                            string scOutput = scProc?.StandardOutput.ReadToEnd() ?? "";
                            scProc?.WaitForExit(10000);
                            Logger.Log($"[FORCE STOP] sc stop output: {scOutput.Trim()}");

                            // Then try sc delete

                            var psiDel = new ProcessStartInfo
                            {
                                FileName = "sc.exe",
                                Arguments = $"delete \"{serviceName}\"",
                                RedirectStandardOutput = true,
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            using var scDelProc = Process.Start(psiDel);
                            string delOutput = scDelProc?.StandardOutput.ReadToEnd() ?? "";
                            scDelProc?.WaitForExit(10000);
                            Logger.Log($"[FORCE STOP] sc delete output: {delOutput.Trim()}");
                            result.HandlesClosed++;
                        }
                        catch (Exception ex2)
                        {
                            Logger.Log($"[FORCE STOP] sc stop/delete ERRO: {ex2.Message}");
                            result.Errors.Add($"Driver '{drv.ProcessName}': {msg}");
                        }
                    }
                }
            }

            // Phase 4: Close individual handles via handle tool
            Logger.Log($"[FORCE STOP] Phase 4: Fechando handles individuais...");
            var nonDriverTargets = targetList.Where(t => t.HandleId?.StartsWith("DRV:") != true);
            foreach (var target in nonDriverTargets.Where(t => !string.IsNullOrEmpty(t.HandleId)))
            {
                try
                {
                    bool closed = CloseHandleViaTool(target.Pid, target.HandleId);
                    if (closed)
                    {
                        result.HandlesClosed++;
                        Logger.Log($"[FORCE STOP] Handle {target.HandleId} do {target.ProcessName} (PID {target.Pid}) liberado.");
                    }
                    else
                    {
                        result.Errors.Add($"Falha ao liberar handle {target.HandleId} de {target.ProcessName}.");
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Erro ao liberar handle de {target.ProcessName}: {ex.Message}");
                }
            }

            // Phase 5: Robust deletion chain with retry — SÓ se deleteTarget=true (CRÍTICO: Liberar não deve deletar)
            if (deleteTarget)
            {
                Logger.Log($"[FORCE STOP] Phase 5: Tentativa robusta de deleção (deleteTarget=true)...");
                try
                {
                    if (File.Exists(targetPath))
                    {
                        var (delOk, delMethod, delError) = RobustDeleteWithRetry(targetPath, maxRetries: 3, delayMs: 1500);
                        if (delOk)
                        {
                            result.Message += $" Arquivo deletado via {delMethod}.";
                            Logger.Log($"[FORCE STOP] Arquivo deletado com sucesso via {delMethod}.");
                        }
                        else
                        {
                            Logger.Log($"[FORCE STOP] Todos os metodos de delecao falharam: {delError}");
                            result.Errors.Add($"Delecao: {delError}");
                        }
                    }
                    else if (Directory.Exists(targetPath))
                    {
                        var (delCount, failCount, delErrors) = RobustDeleteFolder(targetPath, maxRetries: 2);
                        if (failCount == 0)
                        {
                            result.Message += $" {delCount} arquivo(s) deletado(s) da pasta.";
                        }
                        else
                        {
                            result.Message += $" {delCount} deletado(s), {failCount} falhou.";
                            result.Errors.AddRange(delErrors.Take(5));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[FORCE STOP] Erro na delecao robusta: {ex.Message}");
                }
            }
            else
            {
                Logger.Log($"[FORCE STOP] Phase 5: Skipped (deleteTarget=false — apenas liberando handles/processos/drivers, SEM deletar)");
            }

            // Phase 7: Kill processes that still have locks
            Logger.Log($"[FORCE STOP] Phase 7: Finalizando processos restantes...");
            var stillLocked = nonDriverTargets.Where(t =>
                !string.IsNullOrEmpty(t.HandleId) && !result.Errors.Any(e => e.Contains(t.HandleId)))
                .ToList();

            foreach (var proc in stillLocked.GroupBy(t => t.Pid))
            {
                try
                {
                    // O Restart Manager (fase 1) JÁ pode ter encerrado o processo —
                    // pular os encerrados evita erros fantasmas "processo não encontrado".
                    bool alive;
                    try
                    {
                        using var p = Process.GetProcessById(proc.Key);
                        alive = !p.HasExited;
                    }
                    catch { alive = false; }
                    if (!alive)
                    {
                        Logger.Log($"[FORCE STOP] PID {proc.Key} ({proc.First().ProcessName}) já encerrado (Restart Manager/fase anterior).");
                        result.ProcessesKilled++;
                        continue;
                    }

                    bool killed = KillProcess(proc.Key);
                    if (killed)
                    {
                        result.ProcessesKilled++;
                        Logger.Log($"[FORCE STOP] Processo {proc.First().ProcessName} (PID {proc.Key}) finalizado.");
                    }
                    else
                    {
                        result.Errors.Add($"Falha ao finalizar PID {proc.Key} ({proc.First().ProcessName}).");
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Erro ao finalizar PID {proc.Key}: {ex.Message}");
                }
            }

            result.Success = result.HandlesClosed > 0 || result.ProcessesKilled > 0;
            int total = result.HandlesClosed + result.ProcessesKilled;
            result.Message = string.IsNullOrEmpty(result.Message) && total == 0
                ? "Nenhum handle, driver ou processo foi liberado. Tente como Administrador."
                : $"{result.HandlesClosed} handle(s)/driver(s) liberado(s), {result.ProcessesKilled} processo(s) finalizado(s). {result.Message}";

            return result;
        }

        /// <summary>
        /// Quick check: does the target path have any blocking handles?
        /// Returns true if handles were found (meaning the path is locked).
        /// </summary>
        public static bool IsLocked(string targetPath)
        {
            var targets = Directory.Exists(targetPath)
                ? new[] { targetPath }
                : new[] { targetPath };

            return FindViaHandleTool(targets, targetPath, quickMode: true).Count > 0;
        }

        // ─── Restart Manager Implementation ──────────────────────────────

        private static List<BlockingProcessInfo> FindViaRestartManager(string[] filePaths)
        {
            var results = new List<BlockingProcessInfo>();
            string sessionKey = Guid.NewGuid().ToString();
            uint handle = 0;

            Logger.Log($"[FORCE STOP] Restart Manager: iniciando sessao...");
            try
            {
                int rmResult = RmStartSession(out handle, 0, sessionKey);
                if (rmResult != ERROR_SUCCESS)
                {
                    Logger.Log($"[FORCE STOP] Restart Manager: RmStartSession falhou com erro {rmResult}");
                    return results;
                }
                Logger.Log($"[FORCE STOP] Restart Manager: sessao aberta (handle={handle})");

                // Register only existing files (RmRegisterResources fails on folders)
                var existingFiles = filePaths.Where(File.Exists).Take(16).ToArray();
                if (existingFiles.Length == 0) return results;

                rmResult = RmRegisterResources(handle,
                    (uint)existingFiles.Length, existingFiles,
                    0, null,
                    0, null);

                if (rmResult != ERROR_SUCCESS) return results;

                // Get list of affected processes
                uint pnProcInfoNeeded = 0;
                uint pnProcInfo = 0;
                uint lpdwRebootReasons = 0;

                rmResult = RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, null, ref lpdwRebootReasons);

                if (rmResult == ERROR_MORE_DATA && pnProcInfoNeeded > 0)
                {
                    pnProcInfo = pnProcInfoNeeded;
                    var processInfo = new RM_PROCESS_INFO[pnProcInfo];
                    rmResult = RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, processInfo, ref lpdwRebootReasons);

                    if (rmResult == ERROR_SUCCESS)
                    {
                        for (int i = 0; i < pnProcInfo; i++)
                        {
                            int pid = processInfo[i].Process.dwProcessId;
                            string name = processInfo[i].strAppName;

                            // Skip system processes
                            string baseName = Path.GetFileNameWithoutExtension(name);
                            if (SystemProcessNames.Contains(baseName)) continue;

                            // Skip our own process
                            if (pid == Process.GetCurrentProcess().Id) continue;

                            results.Add(new BlockingProcessInfo
                            {
                                Pid = pid,
                                ProcessName = baseName,
                                ExecutablePath = name,
                                HandleId = "RM",
                                HandleType = "Restart Manager",
                                AccessRights = "N/A",
                                LockedPath = string.Join("; ", filePaths.Take(3)),
                                IsSystemProcess = SystemProcessNames.Contains(baseName),
                                IsSelected = !SystemProcessNames.Contains(baseName)
                            });
                        }
                    }
                }
            }
            catch { /* RM not available on this system */ }
            finally
            {
                if (handle != 0)
                    try { RmEndSession(handle); } catch { }
            }

            return results;
        }

        private static bool TryRestartManagerShutdown(string targetPath, List<int> pids)
        {
            string sessionKey = Guid.NewGuid().ToString();
            uint handle = 0;

            try
            {
                int rmResult = RmStartSession(out handle, 0, sessionKey);
                if (rmResult != ERROR_SUCCESS) return false;

                var files = Directory.Exists(targetPath)
                    ? GetFilesInFolder(targetPath, maxDepth: 2).Where(File.Exists).Take(16).ToArray()
                    : new[] { targetPath };

                if (files.Length == 0) return false;

                rmResult = RmRegisterResources(handle,
                    (uint)files.Length, files,
                    0, null,
                    0, null);

                if (rmResult != ERROR_SUCCESS) return false;

                // Force shutdown — closes all handles
                rmResult = RmShutdown(handle, 0x1 /* RmForceShutdown */, null); // 0x1 = force
                return rmResult == ERROR_SUCCESS;
            }
            catch { return false; }
            finally
            {
                if (handle != 0)
                    try { RmEndSession(handle); } catch { }
            }
        }

        // ─── Handle Tool Implementation ──────────────────────────────────

        private static List<BlockingProcessInfo> FindViaHandleTool(string[] filePaths, string originalPath, bool quickMode = false)
        {
            var results = new List<BlockingProcessInfo>();
            string handlePath = GetHandle64Path();

            Logger.Log($"[FORCE STOP] Handle tool path: {handlePath}");
            Logger.Log($"[FORCE STOP] handle64.exe existe: {File.Exists(handlePath)}");

            if (!File.Exists(handlePath))
            {
                Logger.Log("[FORCE STOP] handle64.exe não encontrado. Usando apenas Restart Manager.");
                return results;
            }

            try
            {
                // Search for handles matching the target path
                string searchPattern = Directory.Exists(originalPath)
                    ? originalPath.TrimEnd('\\', '/')
                    : Path.GetDirectoryName(originalPath) ?? originalPath;

                var psi = new ProcessStartInfo
                {
                    FileName = handlePath,
                    Arguments = $"-accepteula -nobanner \"{searchPattern}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var proc = Process.Start(psi);
                if (proc == null) return results;

                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(quickMode ? 5000 : 15000);

                if (proc.ExitCode != 0 && string.IsNullOrEmpty(output))
                    return results;

                // Parse handle output lines:
                // process_name.exe        pid: 1234   handle: 0x1234  type: File  ...
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var handlePattern = new Regex(
                    @"^(\S+\.exe)\s+pid:\s+(\d+)\s+handle:\s+([0-9A-Fa-f]+)\s+type:\s+(\S+)",
                    RegexOptions.Compiled);

                foreach (var line in lines)
                {
                    var match = handlePattern.Match(line.Trim());
                    if (!match.Success) continue;

                    string processName = match.Groups[1].Value;
                    int pid = int.Parse(match.Groups[2].Value);
                    string handleId = match.Groups[3].Value;
                    string handleType = match.Groups[4].Value;

                    // Skip system processes
                    string baseName = Path.GetFileNameWithoutExtension(processName);
                    if (SystemProcessNames.Contains(baseName)) continue;

                    // Skip our own process
                    if (pid == Process.GetCurrentProcess().Id) continue;

                    // Only include File handles (not Section, Key, etc.)
                    if (quickMode && !handleType.Equals("File", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Extract access rights from the line
                    string accessRights = ExtractAccessRights(line);

                    // Verify the handle actually relates to our target
                    if (!line.Contains(originalPath, StringComparison.OrdinalIgnoreCase))
                    {
                        // For folders, the handle output might show a specific file
                        // inside the folder — check if it starts with our target
                        string linePath = ExtractPathFromHandleLine(line);
                        if (string.IsNullOrEmpty(linePath) ||
                            !linePath.StartsWith(searchPattern, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    // Get executable path for display
                    string execPath = "";
                    try
                    {
                        using var procInfo = Process.GetProcessById(pid);
                        execPath = procInfo.MainModule?.FileName ?? "";
                    }
                    catch { }

                    results.Add(new BlockingProcessInfo
                    {
                        Pid = pid,
                        ProcessName = baseName,
                        ExecutablePath = execPath,
                        HandleId = handleId,
                        HandleType = handleType,
                        AccessRights = accessRights,
                        LockedPath = originalPath,
                        IsSystemProcess = SystemProcessNames.Contains(baseName),
                        IsSelected = !SystemProcessNames.Contains(baseName)
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FORCE STOP] Erro ao executar handle64.exe: {ex.Message}");
            }

            return results;
        }

        private static bool CloseHandleViaTool(int pid, string handleId)
        {
            string handlePath = GetHandle64Path();
            if (!File.Exists(handlePath)) return false;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = handlePath,
                    Arguments = $"-accepteula -c {handleId} -p {pid} -y",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var proc = Process.Start(psi);
                if (proc == null) return false;

                proc.WaitForExit(5000);
                return proc.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        // ─── Process Kill ────────────────────────────────────────────────

        private static bool KillProcess(int pid)
        {
            if (pid <= 0 || pid == Environment.ProcessId) return false;
            try
            {
                using var proc = Process.GetProcessById(pid);
                string name = proc.ProcessName;

                // Safety: don't kill critical system processes
                if (SystemProcessNames.Contains(name))
                {
                    Logger.Log($"[FORCE STOP] Processo de sistema '{name}' (PID {pid}) — ignorado por segurança.");
                    return false;
                }

                if (proc.HasExited) return true;

                // 1º: Process.Kill padrão (árvore inteira)
                try
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                }
                catch { /* acesso negado/protegido — segue para fallback */ }

                if (proc.HasExited)
                {
                    Logger.Log($"[FORCE STOP] Processo '{name}' (PID {pid}) finalizado.");
                    return true;
                }

                // 2º: NtTerminateProcess nativo (contorna várias restrições do Process.Kill
                // quando SeDebugPrivilege está habilitado)
                if (TerminateProcessNative(pid))
                {
                    Logger.Log($"[FORCE STOP] Processo '{name}' (PID {pid}) finalizado via NtTerminateProcess.");
                    return true;
                }

                // 3º: diagnóstico — processo protegido (PPL, anti-virus/EDR) não morre do user-mode
                bool isProtected = GetProcessProtection(pid, out int sigLevel);
                if (isProtected)
                {
                    Logger.Log($"[FORCE STOP] '{name}' (PID {pid}) é PROTEGIDO (PPL, signature level {sigLevel}) — anti-virus/EDR. Impossível encerrar do user-mode; desative a autoproteção do AV.");
                }
                else
                {
                    Logger.Log($"[FORCE STOP] Falha ao finalizar '{name}' (PID {pid}) mesmo com SeDebugPrivilege — acesso negado.");
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"[FORCE STOP] Falha ao finalizar PID {pid}: {ex.Message}");
                return false;
            }
        }

        private static bool TerminateProcessNative(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_TERMINATE_NATIVE, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                return NtTerminateProcess(h, 1) == 0;
            }
            catch { return false; }
            finally { CloseHandleNative(h); }
        }

        /// <summary>Detecta proteção PPL (Process Protection Light) — anti-virus/EDR.
        /// Retorna true se o processo é protegido e expõe o signature level (0x08=PPL, 0x09=full, 0x0C=secure).</summary>
        private static bool GetProcessProtection(int pid, out int signatureLevel)
        {
            signatureLevel = 0;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION_NATIVE, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                var ppi = new PROCESS_PROTECTION_INFORMATION();
                int status = NtQueryInformationProcess(h, ProcessProtectionInformation, ref ppi,
                    (uint)Marshal.SizeOf<PROCESS_PROTECTION_INFORMATION>(), out _);
                if (status != 0) return false;
                signatureLevel = ppi.Protection & 0x0F;
                return ppi.Protection != 0;
            }
            catch { return false; }
            finally { CloseHandleNative(h); }
        }

        // ─── Helpers ─────────────────────────────────────────────────────

        private static string[] GetFilesInFolder(string folderPath, int maxDepth = 3)
        {
            var results = new List<string>();
            Logger.Log($"[FORCE STOP] GetFilesInFolder: {folderPath} (maxDepth={maxDepth})");
            try
            {
                var stack = new Stack<(string Dir, int Depth)>();
                stack.Push((folderPath, 0));

                while (stack.Count > 0 && results.Count < 500)
                {
                    var (dir, depth) = stack.Pop();
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(dir))
                            results.Add(file);

                        if (depth < maxDepth)
                        {
                            foreach (var sub in Directory.EnumerateDirectories(dir))
                                stack.Push((sub, depth + 1));
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[FORCE STOP] GetFilesInFolder: acesso negado ou erro em {dir}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FORCE STOP] GetFilesInFolder ERRO: {ex.Message}");
            }

            Logger.Log($"[FORCE STOP] GetFilesInFolder encontrou: {results.Count} arquivo(s)");
            return results.ToArray();
        }

        private static string ExtractAccessRights(string handleLine)
        {
            // Try to extract access from the handle output
            // Typical: "0x100020    File  (R--r--r--)  C:\path\file.txt"
            var match = Regex.Match(handleLine, @"0x[0-9A-Fa-f]+\s+\S+\s+\(([^)]+)\)");
            return match.Success ? match.Groups[1].Value : "N/A";
        }

        private static string ExtractPathFromHandleLine(string handleLine)
        {
            // The path is typically the last token in the line
            var match = Regex.Match(handleLine, @"([A-Z]:\\.+)$", RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value.Trim();

            // Or it might be an NT path
            match = Regex.Match(handleLine, @"(\\Device\\.+)$");
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }

        // ─── Context Menu Manager ──────────────────────────────────────

        /// <summary>
        /// Represents a single context menu entry from the Windows registry.
        /// </summary>
        public class ContextMenuEntry
        {
            public string Name { get; set; } = "";
            public string Label { get; set; } = "";
            public string RegistryPath { get; set; } = "";
            public string Root { get; set; } = ""; // *, Directory, Directory\\Background, Drive
            public string Command { get; set; } = "";
            public bool IsKitEntry { get; set; }
            public bool IsSelected { get; set; }

            public string DisplayLabel => $"{Label} ({Root})";
            public string DetailLabel => IsKitEntry ? "[KitLugia]" : Command;
        }

        /// <summary>
        /// Scan all context menu entries from the current user registry.
        /// Returns entries from *, Directory, Directory\\Background, and Drive shell keys.
        /// </summary>
        public static List<ContextMenuEntry> ScanContextMenuEntries()
        {
            var results = new List<ContextMenuEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Scan both HKCU and HKLM — context menus live in both
            var hives = new[] { Registry.CurrentUser, Registry.LocalMachine };
            string[] hiveLabels = { "HKCU", "HKLM" };

            // All shell locations to scan
            var shellPaths = new (string Path, string Label)[] {
                (@"Software\Classes\*\shell", "* (Arquivos)"),
                (@"Software\Classes\Directory\shell", "Directory (Pastas)"),
                (@"Software\Classes\Directory\Background\shell", "Background (Fundo)"),
                (@"Software\Classes\Drive\shell", "Drive (Unidades)"),
                (@"Software\Classes\Folder\shell", "Folder (Pasta/Desktop)"),
            };

            for (int h = 0; h < hives.Length; h++)
            {
                for (int s = 0; s < shellPaths.Length; s++)
                {
                    try
                    {
                        string fullKeyPath = shellPaths[s].Path;
                        using var key = hives[h].OpenSubKey(fullKeyPath);
                        if (key == null) continue;

                        string hiveLabel = hiveLabels[h];
                        string shellLabel = shellPaths[s].Label;

                        foreach (string subKeyName in key.GetSubKeyNames())
                        {
                            try
                            {
                                // Deduplicate: HKCR merges HKCU+HKLM, so same entry appears twice
                                string dedupKey = $"{subKeyName}|{fullKeyPath}";
                                if (!seen.Add(dedupKey)) continue;

                                using var subKey = key.OpenSubKey(subKeyName);
                                if (subKey == null) continue;

                                string label = subKey.GetValue("", subKeyName)?.ToString() ?? subKeyName;

                                // Get command
                                string command = "";
                                using (var cmdKey = key.OpenSubKey(subKeyName + @"\command"))
                                {
                                    command = cmdKey?.GetValue("", "")?.ToString() ?? "";
                                }

                                // Also check for sub-commands (Win11 cascading menus)
                                foreach (var subCmd in subKey.GetSubKeyNames())
                                {
                                    if (subCmd == "command" || subCmd == "shell" || subCmd == "Extended")
                                        continue;
                                    try
                                    {
                                        using var subCmdKey = subKey.OpenSubKey(subCmd + @"\command");
                                        if (subCmdKey != null)
                                        {
                                            string subCmdVal = subCmdKey.GetValue("", "")?.ToString() ?? "";
                                            if (!string.IsNullOrEmpty(subCmdVal))
                                            {
                                                // Add sub-command as separate entry
                                                string subDedup = $"{subKeyName}.{subCmd}|{fullKeyPath}";
                                                if (seen.Add(subDedup))
                                                {
                                                    bool subIsKit = subCmdVal.Contains("KitLugia", StringComparison.OrdinalIgnoreCase);
                                                    results.Add(new ContextMenuEntry
                                                    {
                                                        Name = subKeyName + "\\" + subCmd,
                                                        Label = $"{label} > {subCmd}",
                                                        RegistryPath = fullKeyPath,
                                                        Root = $"{hiveLabel}:{shellLabel}",
                                                        Command = subCmdVal,
                                                        IsKitEntry = subIsKit,
                                                        IsSelected = false
                                                    });
                                                }
                                            }
                                        }
                                    }
                                    catch { }
                                }

                                bool isKit = label.Contains("KitLugia", StringComparison.OrdinalIgnoreCase) ||
                                             label.Contains("Force Stop Unlock", StringComparison.OrdinalIgnoreCase) ||
                                             command.Contains("KitLugia", StringComparison.OrdinalIgnoreCase);

                                results.Add(new ContextMenuEntry
                                {
                                    Name = subKeyName,
                                    Label = label,
                                    RegistryPath = fullKeyPath,
                                    Root = $"{hiveLabel}:{shellLabel}",
                                    Command = command,
                                    IsKitEntry = isKit,
                                    IsSelected = false
                                });
                            }
                            catch { /* skip inaccessible entries */ }
                        }
                    }
                    catch { /* skip inaccessible roots */ }
                }
            }

            return results.OrderBy(e => e.Root).ThenBy(e => e.Label).ToList();
        }

        /// <summary>
        /// Remove a specific context menu entry by registry path and subkey name.
        /// </summary>
        public static bool RemoveContextMenuEntry(string registryRoot, string subKeyName)
        {
            try
            {
                // Determine hive from root path (format: "HKCU:path" or "HKLM:path" or plain path)
                RegistryKey? baseKey = null;
                string actualPath = registryRoot;

                if (registryRoot.StartsWith("HKCU:"))
                {
                    baseKey = Registry.CurrentUser;
                    actualPath = registryRoot.Substring(5);
                }
                else if (registryRoot.StartsWith("HKLM:"))
                {
                    baseKey = Registry.LocalMachine;
                    actualPath = registryRoot.Substring(5);
                }
                else
                {
                    baseKey = Registry.CurrentUser;
                    actualPath = registryRoot;
                }

                // Handle sub-commands (e.g., "submenu\subitem")
                string keyPath = actualPath + @"\" + subKeyName.Split('\\')[0];

                using var key = baseKey.OpenSubKey(keyPath, true);
                if (key == null) return false;

                baseKey.DeleteSubKeyTree(keyPath, false);
                Logger.Log($"[CONTEXT MENU] Entrada removida: {subKeyName} de {registryRoot}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[CONTEXT MENU] Erro ao remover {subKeyName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Remove all selected context menu entries.
        /// </summary>
        public static (int removed, int failed) RemoveSelectedEntries(IEnumerable<ContextMenuEntry> entries)
        {
            int removed = 0, failed = 0;
            foreach (var entry in entries.Where(e => e.IsSelected))
            {
                if (RemoveContextMenuEntry(entry.RegistryPath, entry.Name))
                    removed++;
                else
                    failed++;
            }
            return (removed, failed);
        }

        // ─── Native Handle Enumeration (NtQuerySystemInformation) ───────
        // This is the most powerful technique: finds ALL open handles in the system,
        // including File, Section (memory-mapped), Key, and other handle types.
        // Replaces handle64.exe dependency for detection.

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(uint SystemInformationClass,
            IntPtr SystemInformation, uint SystemInformationLength, out uint ReturnLength);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryObject(IntPtr ObjectHandleInfo, uint ObjectInformationClass,
            IntPtr ObjectInformation, uint ObjectInformationLength, out uint ReturnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateHandle(IntPtr hSourceProcessHandle, IntPtr hSourceHandle,
            IntPtr hTargetProcessHandle, out IntPtr lpTargetHandle,
            uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwOptions);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandleNative(IntPtr hObject);

        [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
        private static extern IntPtr GetCurrentProcessNative();

        private const uint SystemHandleInformation = 16;
        private const uint ObjectNameInformation = 1;
        private const uint ObjectTypeInformation = 2;
        private const uint PROCESS_DUP_HANDLE_NATIVE = 0x0040;
        private const uint PROCESS_QUERY_INFORMATION_NATIVE = 0x0400;
        private const uint DUPLICATE_CLOSE_SOURCE_NATIVE = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_HANDLE_TABLE_ENTRY_INFO
        {
            public ushort CreatorBackTraceIndex;
            public byte ObjectTypeIndex;
            public byte HandleAttributes;
            public ushort HandleValue;
            public IntPtr Object;
            public uint GrantedAccess;
            public ushort UniqueProcessId;
            public ushort Reserved;
        }

        /// <summary>
        /// Find all processes that have handles to files in the target path.
        /// Uses native NtQuerySystemInformation — finds File AND Section (memory-mapped) handles.
        /// This is more reliable than handle64.exe and doesn't require external tools.
        /// </summary>
        public static List<BlockingProcessInfo> FindViaNativeHandles(string targetPath, bool quickMode = false)
        {
            var results = new List<BlockingProcessInfo>();
            Logger.Log($"[NATIVE] === FindViaNativeHandles para: {targetPath}");

            string searchDir = Directory.Exists(targetPath)
                ? targetPath.TrimEnd('\\', '/')
                : Path.GetDirectoryName(targetPath) ?? targetPath;
            string searchLower = searchDir.ToLowerInvariant();

            try
            {
                // Step 1: Get required buffer size
                uint returnLength = 0;
                int status = NtQuerySystemInformation(SystemHandleInformation, IntPtr.Zero, 0, out returnLength);

                if (returnLength == 0)
                {
                    Logger.Log($"[NATIVE] NtQuerySystemInformation retornou 0 bytes.");
                    return results;
                }

                // Allocate with extra safety margin
                uint bufferSize = returnLength + 1024 * 1024; // 1MB margin
                IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);

                try
                {
                    status = NtQuerySystemInformation(SystemHandleInformation, buffer, bufferSize, out returnLength);
                    if (status != 0)
                    {
                        Logger.Log($"[NATIVE] NtQuerySystemInformation falhou: NTSTATUS 0x{status:X8}");
                        return results;
                    }

                    // Parse handle table
                    int handleCount = Marshal.ReadInt32(buffer);
                    int structSize = Marshal.SizeOf<SYSTEM_HANDLE_TABLE_ENTRY_INFO>();
                    Logger.Log($"[NATIVE] Total de handles no sistema: {handleCount}");

                    // Build process name cache
                    var procCache = new Dictionary<int, string>();
                    int matchedCount = 0;

                    for (int i = 0; i < handleCount; i++)
                    {
                        IntPtr entryPtr = buffer + 4 + (i * structSize); // +4 for count field
                        var entry = Marshal.PtrToStructure<SYSTEM_HANDLE_TABLE_ENTRY_INFO>(entryPtr);

                        // Skip handles from our own process
                        if (entry.UniqueProcessId == (ushort)Environment.ProcessId) continue;

                        // Get process name (cached)
                        if (!procCache.TryGetValue(entry.UniqueProcessId, out string? procName))
                        {
                            try
                            {
                                using var proc = Process.GetProcessById(entry.UniqueProcessId);
                                procName = proc.ProcessName;
                            }
                            catch { procName = $"PID:{entry.UniqueProcessId}"; }
                            procCache[entry.UniqueProcessId] = procName;
                        }

                        // Skip system processes
                        if (SystemProcessNames.Contains(procName)) continue;

                        // Try to get the handle name via NtQueryObject
                        string handleName = GetHandleName(entry.UniqueProcessId, entry.HandleValue);
                        if (string.IsNullOrEmpty(handleName)) continue;

                        // Check if handle name matches our target path
                        string handleLower = handleName.ToLowerInvariant();
                        if (!handleLower.Contains(searchLower)) continue;

                        matchedCount++;
                        string handleType = GetHandleType(entry.UniqueProcessId, entry.HandleValue);

                        Logger.Log($"[NATIVE] MATCH: PID={entry.UniqueProcessId} '{procName}' handle=0x{entry.HandleValue:X4} type={handleType} name={handleName}");

                        results.Add(new BlockingProcessInfo
                        {
                            Pid = entry.UniqueProcessId,
                            ProcessName = procName,
                            ExecutablePath = "",
                            HandleId = $"0x{entry.HandleValue:X4}",
                            HandleType = handleType,
                            AccessRights = $"0x{entry.GrantedAccess:X8}",
                            LockedPath = handleName,
                            IsSystemProcess = false,
                            IsSelected = true
                        });

                        // In quick mode, stop after first match per process
                        if (quickMode && results.Count(r => r.Pid == entry.UniqueProcessId) >= 1)
                            continue;
                    }

                    Logger.Log($"[NATIVE] Total de matches: {matchedCount} (deduplicados: {results.Count})");
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[NATIVE] ERRO: {ex.GetType().Name}: {ex.Message}");
            }

            return results;
        }

        /// <summary>
        /// Get the name of a handle via NtQueryObject.
        /// Returns file path for File handles, device path for Section handles, etc.
        /// </summary>
        private static string GetHandleName(int pid, ushort handleValue)
        {
            IntPtr processHandle = OpenProcess(PROCESS_QUERY_INFORMATION_NATIVE | PROCESS_DUP_HANDLE_NATIVE, false, pid);
            if (processHandle == IntPtr.Zero) return "";

            try
            {
                IntPtr localHandle = IntPtr.Zero;
                bool dupOk = DuplicateHandle(processHandle, (IntPtr)handleValue,
                    GetCurrentProcessNative(), out localHandle,
                    0, false, 0);
                if (!dupOk || localHandle == IntPtr.Zero) return "";

                try
                {
                    // Query object name
                    uint nameLen = 0;
                    NtQueryObject(localHandle, ObjectNameInformation, IntPtr.Zero, 0, out nameLen);
                    if (nameLen == 0) return "";

                    IntPtr nameBuffer = Marshal.AllocHGlobal((int)nameLen);
                    try
                    {
                        int status = NtQueryObject(localHandle, ObjectNameInformation, nameBuffer, nameLen, out _);
                        if (status != 0) return "";

                        // UNICODE_STRING: Length (2 bytes), MaximumLength (2 bytes), Buffer (IntPtr)
                        int length = Marshal.ReadInt16(nameBuffer);
                        IntPtr strPtr = Marshal.ReadIntPtr(nameBuffer, 8); // offset 8 = Buffer pointer

                        if (length > 0 && strPtr != IntPtr.Zero)
                        {
                            return Marshal.PtrToStringUni(strPtr, length / 2) ?? "";
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(nameBuffer);
                    }
                }
                finally
                {
                    CloseHandleNative(localHandle);
                }
            }
            catch { }
            finally
            {
                CloseHandleNative(processHandle);
            }
            return "";
        }

        /// <summary>
        /// Get the type name of a handle (File, Section, Key, etc.)
        /// </summary>
        private static string GetHandleType(int pid, ushort handleValue)
        {
            IntPtr processHandle = OpenProcess(PROCESS_QUERY_INFORMATION_NATIVE | PROCESS_DUP_HANDLE_NATIVE, false, pid);
            if (processHandle == IntPtr.Zero) return "Unknown";

            try
            {
                IntPtr localHandle = IntPtr.Zero;
                bool dupOk = DuplicateHandle(processHandle, (IntPtr)handleValue,
                    GetCurrentProcessNative(), out localHandle,
                    0, false, 0);
                if (!dupOk || localHandle == IntPtr.Zero) return "Unknown";

                try
                {
                    uint typeLen = 0;
                    NtQueryObject(localHandle, ObjectTypeInformation, IntPtr.Zero, 0, out typeLen);
                    if (typeLen == 0) return "Unknown";

                    IntPtr typeBuffer = Marshal.AllocHGlobal((int)typeLen);
                    try
                    {
                        int status3 = NtQueryObject(localHandle, ObjectTypeInformation, typeBuffer, typeLen, out _);
                        if (status3 != 0) return "Unknown";

                        int length = Marshal.ReadInt16(typeBuffer);
                        IntPtr strPtr = Marshal.ReadIntPtr(typeBuffer, 8);
                        if (length > 0 && strPtr != IntPtr.Zero)
                            return Marshal.PtrToStringUni(strPtr, length / 2) ?? "Unknown";
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(typeBuffer);
                    }
                }
                finally
                {
                    CloseHandleNative(localHandle);
                }
            }
            catch { }
            finally
            {
                CloseHandleNative(processHandle);
            }
            return "Unknown";
        }

        /// <summary>
        /// Close a handle from another process using native DuplicateHandle.
        /// More reliable than handle64.exe.
        /// </summary>
        public static bool CloseNativeHandle(int pid, IntPtr handleValue)
        {
            IntPtr processHandle = OpenProcess(PROCESS_DUP_HANDLE_NATIVE, false, pid);
            if (processHandle == IntPtr.Zero) return false;

            try
            {
                bool ok = DuplicateHandle(processHandle, handleValue,
                    GetCurrentProcessNative(), out _,
                    0, false, DUPLICATE_CLOSE_SOURCE_NATIVE);
                if (ok)
                    Logger.Log($"[NATIVE] Handle 0x{handleValue.ToInt64():X} do PID {pid} fechado via DuplicateHandle.");
                return ok;
            }
            catch { return false; }
            finally
            {
                CloseHandleNative(processHandle);
            }
        }

        // ─── Robust Deletion Chain ──────────────────────────────────────
        // Tries multiple methods to delete a locked file, from most graceful to most aggressive.

        /// <summary>
        /// Attempt to delete a file using the most robust chain possible.
        /// Tries 6 different methods in order:
        /// 1. Normal File.Delete
        /// 2. cmd.exe del /f /q
        /// 3. FILE_FLAG_DELETE_ON_CLOSE via CreateFile
        /// 4. NtSetInformationFile with FileDispositionInformation
        /// 5. Robocopy empty mirror trick
        /// 6. MoveFileEx for deletion on reboot
        /// Returns (success, method used, error message)
        /// </summary>
        public static (bool Success, string Method, string Error) RobustDeleteFile(string filePath)
        {
            Logger.Log($"[ROBUST DEL] Iniciado para: {filePath}");
            if (!File.Exists(filePath))
                return (true, "already_deleted", "Arquivo ja nao existe.");

            // Method 1: Normal .NET delete
            try
            {
                File.Delete(filePath);
                if (!File.Exists(filePath))
                {
                    Logger.Log($"[ROBUST DEL] Sucesso via File.Delete.");
                    return (true, "File.Delete", "");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ROBUST DEL] File.Delete falhou: {ex.Message}");
            }

            // Method 2: cmd.exe del /f /q
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c del /f /q \"{filePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                string stderr = proc?.StandardError.ReadToEnd() ?? "";
                proc?.WaitForExit(10000);
                if (!File.Exists(filePath))
                {
                    Logger.Log($"[ROBUST DEL] Sucesso via cmd del.");
                    return (true, "cmd del", "");
                }
                Logger.Log($"[ROBUST DEL] cmd del: {stderr.Trim()}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[ROBUST DEL] cmd del ERRO: {ex.Message}");
            }

            // Method 3: FILE_FLAG_DELETE_ON_CLOSE
            try
            {
                bool ok = DriverUnlockService.ForceDeleteFile(filePath);
                if (ok && !File.Exists(filePath))
                {
                    Logger.Log($"[ROBUST DEL] Sucesso via NtSetInformationFile.");
                    return (true, "NtSetInformationFile", "");
                }
            }
            catch { }

            // Method 4: Robocopy empty mirror trick (overwrite with empty dir)
            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N").Substring(0, 8));
                string tempFile = Path.Combine(tempDir, Path.GetFileName(filePath));
                Directory.CreateDirectory(tempDir);
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "robocopy.exe",
                        Arguments = $"\"{tempDir}\" \"{Path.GetDirectoryName(filePath)}\" /IS /IT /COPY:DAT /R:0 /W:0 /NFL /NDL /NJH /NJS /NC /NS /NP",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    // This doesn't actually help with deletion, skip
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
            catch { }

            // Method 5: Rename then delete (sometimes handles lock by name)
            try
            {
                string renamePath = filePath + ".del_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                File.Move(filePath, renamePath, true);
                File.Delete(renamePath);
                if (!File.Exists(filePath) && !File.Exists(renamePath))
                {
                    Logger.Log($"[ROBUST DEL] Sucesso via rename + delete.");
                    return (true, "rename+delete", "");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ROBUST DEL] rename+delete ERRO: {ex.Message}");
            }

            // Method 6: Schedule deletion on reboot (last resort)
            try
            {
                bool scheduled = DriverUnlockService.ScheduleDeleteOnReboot(filePath);
                if (scheduled)
                {
                    Logger.Log($"[ROBUST DEL] Agendado para deletar no reboot.");
                    return (true, "reboot_delete", "Arquivo sera deletado no proximo reboot.");
                }
            }
            catch { }

            Logger.Log($"[ROBUST DEL] Todos os metodos falharam para: {filePath}");
            return (false, "none", "Todos os metodos de delecao falharam.");
        }

        /// <summary>
        /// Robust delete with retry: tries deletion, waits, retries.
        /// Handles race conditions where services take time to release handles.
        /// </summary>
        public static (bool Success, string Method, string Error) RobustDeleteWithRetry(string filePath, int maxRetries = 3, int delayMs = 2000)
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                Logger.Log($"[ROBUST DEL] Tentativa {attempt}/{maxRetries}...");
                var (ok, method, error) = RobustDeleteFile(filePath);
                if (ok) return (ok, method, error);

                if (attempt < maxRetries)
                {
                    Logger.Log($"[ROBUST DEL] Aguardando {delayMs}ms antes da proxima tentativa...");
                    Thread.Sleep(delayMs);
                }
            }
            return (false, "none", $"Falha apos {maxRetries} tentativas.");
        }

        /// <summary>
        /// Robust delete for all files in a folder (recursive).
        /// </summary>
        public static (int deleted, int failed, List<string> errors) RobustDeleteFolder(string folderPath, int maxRetries = 2)
        {
            var errors = new List<string>();
            int deleted = 0, failed = 0;

            if (!Directory.Exists(folderPath))
                return (0, 0, new List<string> { "Pasta nao existe." });

            // Delete files from deepest to shallowest
            var files = GetFilesList(folderPath).OrderByDescending(f => f.Length).ToList();
            foreach (var file in files)
            {
                var (ok, method, error) = RobustDeleteWithRetry(file, maxRetries);
                if (ok) deleted++;
                else { failed++; errors.Add($"{Path.GetFileName(file)}: {error}"); }
            }

            // Delete empty directories from deepest to shallowest
            try
            {
                var dirs = Directory.GetDirectories(folderPath, "*", SearchOption.AllDirectories)
                    .OrderByDescending(d => d.Length).ToList();
                foreach (var dir in dirs)
                {
                    try
                    {
                        if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                            Directory.Delete(dir);
                    }
                    catch { }
                }
                // Try to delete the root folder
                if (Directory.Exists(folderPath) && Directory.GetFileSystemEntries(folderPath).Length == 0)
                    Directory.Delete(folderPath);
            }
            catch { }

            return (deleted, failed, errors);
        }

        private static List<string> GetFilesList(string folderPath)
        {
            var files = new List<string>();
            try
            {
                foreach (var f in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
                    files.Add(f);
            }
            catch { }
            return files;
        }
    }
}
