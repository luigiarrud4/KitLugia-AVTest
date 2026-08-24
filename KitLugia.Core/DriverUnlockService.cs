using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace KitLugia.Core
{
    /// <summary>
    /// Represents a driver that is locking a file.
    /// </summary>
    public class BlockingDriverInfo
    {
        public string DriverName { get; set; } = "";
        public string ServiceName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string DriverPath { get; set; } = "";
        public string RegistryPath { get; set; } = "";
        public int Pid { get; set; }
        public string CurrentState { get; set; } = ""; // Running, Stopped, etc.
        public bool IsUnloaded { get; set; }
        public bool IsSelected { get; set; } = true;
        public string Error { get; set; } = "";

        public string DisplayLabel => $"{DriverName} ({CurrentState})";
        public string DetailLabel => $"{DisplayName} | {DriverPath}";
    }

    /// <summary>
    /// Advanced driver and handle unlock service using:
    /// 1. Service Control Manager (sc stop / sc delete)
    /// 2. NtUnloadDriver (NT API direct unload)
    /// 3. Handle duplication + NtClose (close handles from other processes)
    /// 4. NtSetInformationFile (force-delete open files)
    /// 5. PendingFileRenameOperations (schedule deletion on reboot)
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class DriverUnlockService
    {
        // ─── P/Invoke: Service Control Manager ─────────────────────────
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSCManager(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ControlService(IntPtr hService, uint dwControl, ref SERVICE_STATUS lpServiceStatus);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteService(IntPtr hService);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumServicesStatusEx(
            IntPtr hSCManager, uint infoLevel, uint dwServiceType, uint dwServiceState,
            IntPtr lpServices, uint cbBufBytes, out uint pcbBytesNeeded,
            IntPtr lpServicesReturnedHandle, IntPtr lpResumeHandle, string? lpGroupName);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryServiceStatusEx(IntPtr hService, uint infoLevel,
            byte[] lpServiceStatus, uint cbBufBytes, out uint pcbBytesNeeded);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SERVICE_STATUS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ENUM_SERVICE_STATUS_PROCESS
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string lpServiceName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpDisplayName;
            public SERVICE_STATUS_PROCESS ServiceStatusProcess;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS_PROCESS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
            public uint dwProcessId;
            public uint dwServiceFlags;
        }

        private const uint SC_MANAGER_CONNECT = 0x0001;
        private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
        private const uint SERVICE_STOP = 0x0020;
        private const uint SERVICE_QUERY_STATUS = 0x0004;
        private const uint SERVICE_ALL_ACCESS = 0xF01FF;
        private const uint SERVICE_CONTROL_STOP = 0x0001;
        private const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
        private const uint SERVICE_WIN32 = 0x00000030;
        private const uint SERVICE_STATE_ALL = 0x00000003;
        private const uint SC_ENUM_PROCESS_INFO = 0;
        private const uint SERVICE_RUNNING = 0x00000004;
        private const uint SERVICE_STOPPED = 0x00000001;

        // ─── P/Invoke: NtUnloadDriver ──────────────────────────────────
        [DllImport("ntdll.dll")]
        private static extern int NtUnloadDriver(ref UNICODE_STRING DriverServiceName);

        [StructLayout(LayoutKind.Sequential)]
        private struct UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [DllImport("ntdll.dll")]
        private static extern int RtlInitUnicodeString(out UNICODE_STRING DestinationString, [MarshalAs(UnmanagedType.LPWStr)] string SourceString);

        // ─── P/Invoke: Handle operations ───────────────────────────────
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateHandle(IntPtr hSourceProcessHandle, IntPtr hSourceHandle,
            IntPtr hTargetProcessHandle, out IntPtr lpTargetHandle,
            uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwOptions);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private const uint PROCESS_DUP_HANDLE = 0x0040;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint DUPLICATE_CLOSE_SOURCE = 0x00000001;
        private const uint DUPLICATE_SAME_ACCESS = 0x00000002;

        // ─── P/Invoke: NtQuerySystemInformation (handle enumeration) ──
        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(uint SystemInformationClass,
            IntPtr SystemInformation, uint SystemInformationLength, out uint ReturnLength);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryObject(IntPtr ObjectHandleInfo, uint ObjectInformationClass,
            IntPtr ObjectInformation, uint ObjectInformationLength, out uint ReturnLength);

        private const uint SystemHandleInformation = 16;
        private const uint ObjectNameInformation = 1;

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

        // ─── P/Invoke: MoveFileEx (delete on reboot) ──────────────────
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileExW(string lpExistingFileName, string? lpNewFileName, uint dwFlags);

        private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

        // ─── P/Invoke: NtSetInformationFile (force delete) ────────────
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess,
            uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("ntdll.dll")]
        private static extern int NtSetInformationFile(IntPtr FileHandle, IntPtr IoStatusBlock,
            IntPtr FileInformation, uint Length, uint FileInformationClass);

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_STATUS_BLOCK
        {
            public IntPtr Status;
            public uint Information;
        }

        private const int FILE_DISPOSITION_INFO_DELETE = 0x00000001;
        private const uint FILE_DISPOSITION_INFO_FORCE_IMAGE_SECTION = 0x00000002;
        private const uint FileDispositionInformation = 13;
        private const uint FileDispositionInformationEx = 64;

        private const uint FILE_GENERIC_READ = 0x00120089;
        private const uint FILE_GENERIC_WRITE = 0x00120116;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint FILE_SHARE_DELETE = 0x00000004;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_DELETE_ON_CLOSE = 0x04000000;

        // ─── System process names to never touch ───────────────────────
        private static readonly HashSet<string> CriticalDrivers = new(StringComparer.OrdinalIgnoreCase)
        {
            "ntoskrnl", "ntoskrnl.exe", "hal", "hal.dll", "bootvid", "bootvid.dll",
            "ci", "ci.dll", "mssecflt", "mssecflt.sys", "msrpc.sys",
            "disk", "disk.sys", "classpnp", "classpnp.sys", "partmgr", "partmgr.sys",
            "volsnap", "volsnap.sys", "volmgr", "volmgr.sys", "atapi", "atapi.sys",
            "storport", "storport.sys", "storpor", "storpor.sys", "mpio", "mpio.sys",
            "tcpip", "tcpip.sys", "ndis", "ndis.sys", "mrxsmb", "mrxsmb.sys",
            "srv", "srv.sys", "mssmbios", "mssmbios.sys", "msisadrv", "msisadrv.sys",
            "pwpcio", "pwpcio.sys", "nvme", "nvme.sys", "spaceport", "spaceport.sys"
        };

        // ─── Public API ─────────────────────────────────────────────────

        /// <summary>
        /// Find drivers that are locking a specific file path.
        /// Checks running services with matching driver paths.
        /// </summary>
        public static List<BlockingDriverInfo> FindBlockingDrivers(string filePath)
        {
            Logger.Log($"[DRIVER] === FindBlockingDrivers iniciado para: {filePath}");
            bool isAdmin = SystemUtils.IsRunningAsAdministrator();
            Logger.Log($"[DRIVER] Executando como Administrador: {isAdmin}");

            var results = new List<BlockingDriverInfo>();

            var sysFilesInFolder = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string normalizedTarget = Path.GetFullPath(filePath).ToLowerInvariant();

                // If target is a folder, find all .sys files inside it
                if (Directory.Exists(filePath))
                {
                    Logger.Log($"[DRIVER] Pasta detectada, buscando arquivos .sys...");
                    try
                    {
                        foreach (var sysFile in Directory.EnumerateFiles(filePath, "*.sys", SearchOption.AllDirectories))
                        {
                            string sysName = Path.GetFileName(sysFile).ToLowerInvariant();
                            sysFilesInFolder.Add(sysName);
                            Logger.Log($"[DRIVER]   .sys encontrado: {sysFile}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[DRIVER]   Erro ao enumerar .sys: {ex.Message}");
                    }
                    Logger.Log($"[DRIVER] Total de .sys na pasta: {sysFilesInFolder.Count}");

                    // Also list ALL files (not just .sys) for debugging
                    try
                    {
                        Logger.Log($"[DRIVER] Todos os arquivos na pasta:");
                        foreach (var file in Directory.EnumerateFiles(filePath, "*", SearchOption.AllDirectories))
                        {
                            Logger.Log($"[DRIVER]   {file}");
                        }
                    }
                    catch { }
                }
                else
                {
                    Logger.Log($"[DRIVER] Arquivo unico detectado.");
                    // Single file — check if it's a .sys
                    string ext = Path.GetExtension(filePath);
                    Logger.Log($"[DRIVER] Extensao: {ext}");
                    if (ext.Equals(".sys", StringComparison.OrdinalIgnoreCase))
                    {
                        sysFilesInFolder.Add(Path.GetFileName(filePath).ToLowerInvariant());
                        Logger.Log($"[DRIVER] Arquivo .sys adicionado para busca: {Path.GetFileName(filePath)}");
                    }
                }

                // Enumerate all driver services (both running and stopped)
                Logger.Log($"[DRIVER] Abrindo Service Control Manager...");
                IntPtr scm = OpenSCManager(null, null, SC_MANAGER_ENUMERATE_SERVICE);
                if (scm == IntPtr.Zero)
                {
                    int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    Logger.Log($"[DRIVER] FALHA ao abrir SCM: erro {err}. {(isAdmin ? "Ja esta admin." : "NECESSITA ADMIN para enumerar servicos!")}");
                    return results;
                }
                Logger.Log($"[DRIVER] SCM aberto com sucesso.");

                // Enumerate all services to find ones matching our .sys files
                IntPtr buffer = IntPtr.Zero;
                try
                {
                    // Get ALL service types to catch WinDivert and other drivers
                    uint bytesNeeded = 0;
                    EnumServicesStatusEx(scm, SC_ENUM_PROCESS_INFO, 0 /* all types */,
                        SERVICE_STATE_ALL, IntPtr.Zero, 0, out bytesNeeded,
                        IntPtr.Zero, IntPtr.Zero, null);

                    Logger.Log($"[DRIVER] SCM EnumServicesStatusEx bytesNeeded={bytesNeeded}");

                    if (bytesNeeded == 0)
                    {
                        Logger.Log($"[DRIVER] SCM retornou 0 bytes — nenhum servico encontrado via API.");
                    }
                    else
                    {
                        buffer = Marshal.AllocHGlobal((int)bytesNeeded);
                        bool ok = EnumServicesStatusEx(scm, SC_ENUM_PROCESS_INFO, 0 /* all types */,
                            SERVICE_STATE_ALL, buffer, bytesNeeded, out bytesNeeded,
                            IntPtr.Zero, IntPtr.Zero, null);

                        if (!ok)
                        {
                            int err = Marshal.GetLastWin32Error();
                            Logger.Log($"[DRIVER] SCM EnumServicesStatusEx falhou: erro {err}.");
                        }
                        else
                        {
                            int structSize = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESS>();
                            int count = (int)bytesNeeded / structSize;
                            Logger.Log($"[DRIVER] SCM enumerou {count} servicos. Verificando matches...");

                            int emptyPathCount = 0;
                            for (int i = 0; i < count; i++)
                            {
                                IntPtr ptr = buffer + (i * structSize);
                                var svc = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESS>(ptr);

                                // Match using fuzzy name matching (handles WinDivert ↔ WinDivert64.sys)
                                bool matches = ServiceMatchesSysFiles(svc.lpServiceName, sysFilesInFolder);

                                // Also match by path if ImagePath exists
                                string driverPath = GetDriverPath(svc.lpServiceName);
                                if (!matches && !string.IsNullOrEmpty(driverPath))
                                {
                                    string expandedPath = Environment.ExpandEnvironmentVariables(driverPath).ToLowerInvariant();
                                    string serviceFileName = Path.GetFileName(expandedPath).ToLowerInvariant();
                                    if (sysFilesInFolder.Contains(serviceFileName))
                                        matches = true;
                                    if (!matches && (expandedPath == normalizedTarget ||
                                        expandedPath.Contains(normalizedTarget) ||
                                        normalizedTarget.Contains(expandedPath)))
                                        matches = true;
                                }
                                if (string.IsNullOrEmpty(driverPath)) driverPath = "(sem ImagePath)";

                                if (matches)
                                {
                                    Logger.Log($"[DRIVER] MATCH: servico '{svc.lpServiceName}' estado={svc.ServiceStatusProcess.dwCurrentState}");
                                    string state = svc.ServiceStatusProcess.dwCurrentState == SERVICE_RUNNING
                                        ? "Running" : "Stopped";

                                    results.Add(new BlockingDriverInfo
                                    {
                                        DriverName = Path.GetFileName(driverPath),
                                        ServiceName = svc.lpServiceName,
                                        DisplayName = svc.lpDisplayName,
                                        DriverPath = driverPath,
                                        RegistryPath = $@"HKLM\SYSTEM\CurrentControlSet\Services\{svc.lpServiceName}",
                                        Pid = (int)svc.ServiceStatusProcess.dwProcessId,
                                        CurrentState = state,
                                        IsSelected = true
                                    });
                                }
                            }
                            Logger.Log($"[DRIVER] SCM: {count} servicos verificados, {emptyPathCount} sem ImagePath, {results.Count} matches.");
                        }
                    }
                }
                finally
                {
                    if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                    CloseServiceHandle(scm);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[DRIVER] ERRO na enumeracao SCM: {ex.GetType().Name}: {ex.Message}");
            }

            Logger.Log($"[DRIVER] SCM enum encontrou: {results.Count} driver(es)");
            foreach (var r in results)
                Logger.Log($"[DRIVER]   SCM: {r.DriverName} ({r.ServiceName}) estado={r.CurrentState}");

            // ─── AGGRESSIVE FALLBACK 1: sc query state=all ────────────
            // Catches ALL services including Win32 ones like WinDivert
            // Always run to catch services missed by SCM enum
            Logger.Log($"[DRIVER] Fallback 1: sc query state=all (todos os servicos)...");
            try
            {
                var scResults = FindDriversViaScQuery(sysFilesInFolder);
                Logger.Log($"[DRIVER] sc query encontrou: {scResults.Count} driver(es)");
                foreach (var r in scResults)
                {
                    Logger.Log($"[DRIVER]   sc: {r.DriverName} ({r.ServiceName}) estado={r.CurrentState}");
                    if (!results.Any(x => x.ServiceName == r.ServiceName))
                        results.Add(r);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[DRIVER] Fallback 1 ERRO: {ex.Message}");
            }

            // ─── AGGRESSIVE FALLBACK 2: Registry scan ────────────────────
            // Reads HKLM\SYSTEM\CurrentControlSet\Services directly
            // Always run to catch services missed by SCM enum
            Logger.Log($"[DRIVER] Fallback 2: Registry scan...");
            try
            {
                var regResults = FindDriversViaRegistry(sysFilesInFolder);
                Logger.Log($"[DRIVER] Registry scan encontrou: {regResults.Count} driver(es)");
                foreach (var r in regResults)
                {
                    Logger.Log($"[DRIVER]   reg: {r.DriverName} ({r.ServiceName}) estado={r.CurrentState}");
                    if (!results.Any(x => x.ServiceName == r.ServiceName))
                        results.Add(r);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[DRIVER] Fallback 2 ERRO: {ex.Message}");
            }

            // ─── AGGRESSIVE FALLBACK 3: .sys file direct match ───────────
            // Check if ANY .sys in the folder is loaded as a driver service
            if (sysFilesInFolder.Count > 0)
            {
                Logger.Log($"[DRIVER] Fallback 3: Verificando drivers carregados contra .sys da pasta...");
                try
                {
                    var loadedDrivers = GetAllServiceNames();
                    Logger.Log($"[DRIVER] Total de drivers carregados no sistema: {loadedDrivers.Count}");
                    foreach (var ld in loadedDrivers)
                        Logger.Log($"[DRIVER]   driver: {ld}");

                    foreach (var sysName in sysFilesInFolder)
                    {
                        string baseName = Path.GetFileNameWithoutExtension(sysName);
                        Logger.Log($"[DRIVER] Buscando '{baseName}' na lista de drivers carregados...");
                        var match = loadedDrivers.FirstOrDefault(d =>
                            d.ToLowerInvariant().Contains(baseName.ToLowerInvariant()));
                        if (match != null)
                        {
                            Logger.Log($"[DRIVER] MATCH! '{baseName}' corresponde a driver carregado: {match}");
                            results.Add(new BlockingDriverInfo
                            {
                                DriverName = sysName,
                                ServiceName = baseName,
                                DisplayName = $"Driver .sys: {match}",
                                DriverPath = Path.Combine(filePath, sysName),
                                CurrentState = "Loaded",
                                IsSelected = true
                            });
                        }
                        else
                        {
                            Logger.Log($"[DRIVER] '{baseName}' NAO encontrado na lista de drivers carregados.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[DRIVER] Fallback 3 ERRO: {ex.Message}");
                }
            }

            Logger.Log($"[DRIVER] === Total de drivers bloqueadores: {results.Count}");
            return results;
        }

        /// <summary>
        /// Attempt to unload a driver via SCM (stop + delete service).
        /// </summary>
        public static (bool Success, string Message) UnloadDriverViaScm(string serviceName)
        {
            if (string.IsNullOrEmpty(serviceName))
                return (false, "Nome do serviço vazio.");

            // Safety: never touch critical drivers
            if (CriticalDrivers.Contains(serviceName))
                return (false, $"Driver crítico '{serviceName}' — remoção bloqueada por segurança.");

            try
            {
                IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
                if (scm == IntPtr.Zero)
                    return (false, "Não foi possível conectar ao Service Control Manager.");

                try
                {
                    IntPtr svc = OpenService(scm, serviceName, SERVICE_STOP | SERVICE_QUERY_STATUS | SERVICE_ALL_ACCESS);
                    if (svc == IntPtr.Zero)
                        return (false, $"Serviço '{serviceName}' não encontrado.");

                    try
                    {
                        // Phase 1: Stop the service
                        var status = new SERVICE_STATUS();
                        bool stopped = ControlService(svc, SERVICE_CONTROL_STOP, ref status);

                        if (stopped)
                        {
                            Logger.Log($"[DRIVER] Serviço '{serviceName}' parado com sucesso.");
                        }
                        else
                        {
                            uint error = (uint)Marshal.GetLastWin32Error();
                            if (error == 1062) // ERROR_SERVICE_NOT_ACTIVE
                            {
                                Logger.Log($"[DRIVER] Serviço '{serviceName}' já estava parado.");
                            }
                            else
                            {
                                Logger.Log($"[DRIVER] Aviso ao parar '{serviceName}': erro {error}");
                            }
                        }

                        // Phase 2: Delete the service
                        bool deleted = DeleteService(svc);
                        if (deleted)
                        {
                            Logger.Log($"[DRIVER] Serviço '{serviceName}' removido com sucesso.");
                            return (true, $"Driver '{serviceName}' descarregado e removido.");
                        }
                        else
                        {
                            uint error = (uint)Marshal.GetLastWin32Error();
                            if (error == 1072) // ERROR_SERVICE_MARKED_FOR_DELETE
                            {
                                return (true, $"Driver '{serviceName}' marcado para remoção.");
                            }
                            return (false, $"Falha ao remover serviço '{serviceName}': erro {error}");
                        }
                    }
                    finally
                    {
                        CloseServiceHandle(svc);
                    }
                }
                finally
                {
                    CloseServiceHandle(scm);
                }
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao descarregar driver: {ex.Message}");
            }
        }

        /// <summary>
        /// Attempt to unload a driver via NtUnloadDriver (NT API direct).
        /// Requires SeLoadDriverPrivilege (admin).
        /// </summary>
        public static (bool Success, string Message) UnloadDriverViaNtApi(string serviceName)
        {
            if (string.IsNullOrEmpty(serviceName))
                return (false, "Nome do serviço vazio.");

            if (CriticalDrivers.Contains(serviceName))
                return (false, $"Driver crítico '{serviceName}' — remoção bloqueada por segurança.");

            try
            {
                string registryPath = $@"\Registry\Machine\System\CurrentControlSet\Services\{serviceName}";
                RtlInitUnicodeString(out UNICODE_STRING driverName, registryPath);

                int status = NtUnloadDriver(ref driverName);

                if (status == 0) // STATUS_SUCCESS
                {
                    Logger.Log($"[DRIVER] '{serviceName}' descarregado via NtUnloadDriver.");
                    return (true, $"Driver '{serviceName}' descarregado via NT API.");
                }
                else
                {
                    // Common NTSTATUS codes:
                    // 0xC0000001 = STATUS_UNSUCCESSFUL
                    // 0xC00000BB = STATUS_NOT_SUPPORTED (PnP driver)
                    string error = $"NTSTATUS 0x{status:X8}";
                    if (status == unchecked((int)0xC00000BB))
                        error = "Driver PnP — não pode ser descarregado via NtUnloadDriver";
                    else if (status == unchecked((int)0xC0000001))
                        error = "Falha interna — driver pode não ter rotina de descarregamento";

                    Logger.Log($"[DRIVER] NtUnloadDriver para '{serviceName}': {error}");
                    return (false, $"Falha ao descarregar '{serviceName}': {error}");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao descarregar via NT API: {ex.Message}");
            }
        }

        /// <summary>
        /// Close a handle from another process by duplicating and closing it.
        /// This is the most powerful user-mode technique for releasing file locks.
        /// </summary>
        public static bool CloseHandleFromProcess(int pid, IntPtr handleValue)
        {
            try
            {
                IntPtr processHandle = OpenProcess(PROCESS_DUP_HANDLE, false, pid);
                if (processHandle == IntPtr.Zero) return false;

                try
                {
                    bool dupOk = DuplicateHandle(processHandle, handleValue,
                        GetCurrentProcess(), out IntPtr dupHandle,
                        0, false, DUPLICATE_CLOSE_SOURCE);

                    if (dupOk)
                    {
                        CloseHandle(dupHandle);
                        Logger.Log($"[HANDLE] Handle 0x{handleValue.ToInt64():X} do PID {pid} duplicado e fechado.");
                        return true;
                    }
                    return false;
                }
                finally
                {
                    CloseHandle(processHandle);
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// Force-delete a file that is held open by a driver.
        /// Uses NtSetInformationFile with FileDispositionInformation.
        /// </summary>
        public static bool ForceDeleteFile(string filePath)
        {
            try
            {
                // Method 1: FILE_FLAG_DELETE_ON_CLOSE
                IntPtr hFile = CreateFileW(filePath,
                    FILE_GENERIC_READ | FILE_GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    IntPtr.Zero, OPEN_EXISTING,
                    FILE_FLAG_DELETE_ON_CLOSE, IntPtr.Zero);

                if (hFile != IntPtr.Zero && hFile != new IntPtr(-1))
                {
                    CloseHandle(hFile);
                    if (!File.Exists(filePath))
                    {
                        Logger.Log($"[FORCE DELETE] '{filePath}' deletado via DELETE_ON_CLOSE.");
                        return true;
                    }
                }

                // Method 2: NtSetInformationFile with FileDispositionInformation
                hFile = CreateFileW(filePath,
                    FILE_GENERIC_READ | FILE_GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

                if (hFile != IntPtr.Zero && hFile != new IntPtr(-1))
                {
                    try
                    {
                        IntPtr pDisposition = Marshal.AllocHGlobal(4);
                        Marshal.WriteInt32(pDisposition, FILE_DISPOSITION_INFO_DELETE);

                        IntPtr pIoStatus = Marshal.AllocHGlobal(24); // sizeof(IO_STATUS_BLOCK)
                        try
                        {
                            int status = NtSetInformationFile(hFile, pIoStatus,
                                pDisposition, 4, FileDispositionInformation);

                            Marshal.FreeHGlobal(pDisposition);

                            if (status == 0)
                            {
                                Logger.Log($"[FORCE DELETE] '{filePath}' marcado para deleção via NtSetInformationFile.");
                                return true;
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(pIoStatus);
                        }
                    }
                    finally
                    {
                        CloseHandle(hFile);
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Schedule a file for deletion on next reboot.
        /// Uses MoveFileEx with MOVEFILE_DELAY_UNTIL_REBOOT.
        /// Requires admin. Falls back to PendingFileRenameOperations registry key.
        /// </summary>
        public static bool ScheduleDeleteOnReboot(string filePath)
        {
            try
            {
                // Method 1: MoveFileEx
                bool ok = MoveFileExW(filePath, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                if (ok)
                {
                    Logger.Log($"[REBOOT DELETE] '{filePath}' agendado para deleção no próximo reboot.");
                    return true;
                }

                // Method 2: Direct PendingFileRenameOperations registry
                return AddPendingRename(filePath);
            }
            catch (Exception ex)
            {
                Logger.Log($"[REBOOT DELETE] Erro ao agendar '{filePath}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Force-unload all blocking drivers for a target file.
        /// Tries SCM first, then NtUnloadDriver.
        /// </summary>
        public static (int unloaded, List<string> errors) UnloadBlockingDrivers(string filePath)
        {
            int unloaded = 0;
            var errors = new List<string>();

            var drivers = FindBlockingDrivers(filePath);
            foreach (var driver in drivers)
            {
                if (CriticalDrivers.Contains(driver.ServiceName))
                {
                    errors.Add($"Driver crítico '{driver.ServiceName}' ignorado por segurança.");
                    continue;
                }

                // Try SCM first
                var (ok, msg) = UnloadDriverViaScm(driver.ServiceName);
                if (ok)
                {
                    unloaded++;
                    driver.IsUnloaded = true;
                    continue;
                }

                // Try NtUnloadDriver
                (ok, msg) = UnloadDriverViaNtApi(driver.ServiceName);
                if (ok)
                {
                    unloaded++;
                    driver.IsUnloaded = true;
                }
                else
                {
                    errors.Add($"{driver.ServiceName}: {msg}");
                }
            }

            return (unloaded, errors);
        }

        // ─── Helpers ─────────────────────────────────────────────────────

        private static string GetDriverPath(string serviceName)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
                return key?.GetValue("ImagePath")?.ToString() ?? "";
            }
            catch { return ""; }
        }

        private static bool AddPendingRename(string filePath)
        {
            try
            {
                const string keyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager";
                using var key = Registry.LocalMachine.OpenSubKey(keyPath, true);
                if (key == null) return false;

                var existing = key.GetValue("PendingFileRenameOperations") as string[];
                var entries = new List<string>();

                if (existing != null)
                    entries.AddRange(existing);

                // Format: \??\<path>\0<newpath>\0  (empty newpath = delete)
                entries.Add($@"\??\{filePath}");
                entries.Add(""); // empty = delete on reboot

                key.SetValue("PendingFileRenameOperations", entries.ToArray(), RegistryValueKind.MultiString);

                Logger.Log($"[REBOOT DELETE] '{filePath}' adicionado ao PendingFileRenameOperations.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[REBOOT DELETE] Erro ao escrever PendingFileRenameOperations: {ex.Message}");
                return false;
            }
        }

        // ─── Aggressive fallback methods ────────────────────────────────

        /// <summary>
        /// Parse 'sc query state= all' (NO type filter!) to find ALL services.
        /// This catches Win32 services like WinDivert that 'sc query type=driver' misses.
        /// </summary>
        private static List<string> GetAllServiceNames()
        {
            var services = new List<string>();
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = "query state= all",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return services;

                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(10000);

                // Parse: SERVICE_NAME: WinDivert
                foreach (Match m in Regex.Matches(output, @"SERVICE_NAME:\s+(\S+)", RegexOptions.IgnoreCase))
                {
                    services.Add(m.Groups[1].Value);
                }
                Logger.Log($"[DRIVER] GetAllServiceNames: {services.Count} servicos encontrados via sc query state=all");
            }
            catch (Exception ex)
            {
                Logger.Log($"[DRIVER] GetAllServiceNames ERRO: {ex.Message}");
            }
            return services;
        }

        /// <summary>
        /// Check if a service name matches any .sys file in the target folder.
        /// Matches by:
        /// 1. ImagePath filename matches .sys file exactly
        /// 2. Service name contains the .sys base name (e.g., "WinDivert" matches "WinDivert64.sys")
        /// 3. .sys base name contains the service name (e.g., "WinDivert64" contains "WinDivert")
        /// </summary>
        private static bool ServiceMatchesSysFiles(string serviceName, HashSet<string> sysFilesInFolder)
        {
            // Check 1: ImagePath filename match
            string driverPath = GetDriverPath(serviceName);
            if (!string.IsNullOrEmpty(driverPath))
            {
                string expanded = Environment.ExpandEnvironmentVariables(driverPath).ToLowerInvariant();
                string fileName = Path.GetFileName(expanded);
                if (sysFilesInFolder.Contains(fileName))
                    return true;
            }

            // Check 2 & 3: Service name fuzzy matching against .sys base names
            string svcLower = serviceName.ToLowerInvariant();
            foreach (var sysFile in sysFilesInFolder)
            {
                string baseName = Path.GetFileNameWithoutExtension(sysFile).ToLowerInvariant();
                // "WinDivert" matches "WinDivert64" and vice versa
                if (svcLower.Contains(baseName) || baseName.Contains(svcLower))
                    return true;
                // Also try removing digits: "WinDivert64" → "windivert"
                string baseNoDigits = Regex.Replace(baseName, @"\d+", "");
                string svcNoDigits = Regex.Replace(svcLower, @"\d+", "");
                if (svcNoDigits.Length >= 4 && baseNoDigits.Contains(svcNoDigits))
                    return true;
                if (baseNoDigits.Length >= 4 && svcNoDigits.Contains(baseNoDigits))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Scan ALL services via 'sc query state= all' and match against .sys files.
        /// Uses both ImagePath and service name fuzzy matching.
        /// </summary>
        private static List<BlockingDriverInfo> FindDriversViaScQuery(HashSet<string> sysFilesInFolder)
        {
            var results = new List<BlockingDriverInfo>();
            if (sysFilesInFolder.Count == 0) return results;

            try
            {
                // Get ALL service names (not just drivers)
                var allServiceNames = GetAllServiceNames();
                Logger.Log($"[DRIVER] sc query: verificando {allServiceNames.Count} servicos...");

                foreach (string svcName in allServiceNames)
                {
                    try
                    {
                        if (ServiceMatchesSysFiles(svcName, sysFilesInFolder))
                        {
                            string driverPath = GetDriverPath(svcName);
                            string expanded = string.IsNullOrEmpty(driverPath)
                                ? "(sem ImagePath)"
                                : Environment.ExpandEnvironmentVariables(driverPath);

                            Logger.Log($"[DRIVER] sc query MATCH: servico '{svcName}' -> '{expanded}'");

                            // Get current state
                            string state = "Unknown";
                            try
                            {
                                var psi = new ProcessStartInfo
                                {
                                    FileName = "sc.exe",
                                    Arguments = $"query \"{svcName}\"",
                                    RedirectStandardOutput = true,
                                    UseShellExecute = false,
                                    CreateNoWindow = true
                                };
                                using var proc = Process.Start(psi);
                                if (proc != null)
                                {
                                    string output = proc.StandardOutput.ReadToEnd();
                                    proc.WaitForExit(5000);
                                    if (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
                                        state = "Running";
                                    else if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
                                        state = "Stopped";
                                }
                            }
                            catch { }

                            results.Add(new BlockingDriverInfo
                            {
                                DriverName = Path.GetFileNameWithoutExtension(
                                    string.IsNullOrEmpty(driverPath) ? svcName : driverPath),
                                ServiceName = svcName,
                                DisplayName = $"{svcName} ({expanded})",
                                DriverPath = driverPath ?? "",
                                CurrentState = state,
                                IsSelected = true
                            });
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[DRIVER] FindDriversViaScQuery ERRO: {ex.Message}");
            }
            return results;
        }

        /// <summary>
        /// Scan HKLM\SYSTEM\CurrentControlSet\Services registry directly.
        /// Finds drivers that SCM API misses.
        /// </summary>
        private static List<BlockingDriverInfo> FindDriversViaRegistry(HashSet<string> sysFilesInFolder)
        {
            var results = new List<BlockingDriverInfo>();
            if (sysFilesInFolder.Count == 0) return results;

            try
            {
                using var servicesKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
                if (servicesKey == null) return results;

                foreach (string svcName in servicesKey.GetSubKeyNames())
                {
                    try
                    {
                        using var svcKey = servicesKey.OpenSubKey(svcName);
                        if (svcKey == null) continue;

                        // Use fuzzy name matching (handles WinDivert ↔ WinDivert64.sys)
                        // Don't filter by Start type or Type — we want ALL possible matches
                        if (!ServiceMatchesSysFiles(svcName, sysFilesInFolder))
                        {
                            // Also check ImagePath directly as fallback
                            string imagePath = svcKey.GetValue("ImagePath")?.ToString() ?? "";
                            if (string.IsNullOrEmpty(imagePath)) continue;
                            string expanded = Environment.ExpandEnvironmentVariables(imagePath).ToLowerInvariant();
                            string fileName = Path.GetFileName(expanded);
                            if (!sysFilesInFolder.Contains(fileName))
                                continue;
                        }

                        int svcType = 0;
                        if (svcKey.GetValue("Type") is int t) svcType = t;
                        string driverPath = svcKey.GetValue("ImagePath")?.ToString() ?? "(sem ImagePath)";

                        Logger.Log($"[DRIVER] Registry MATCH: servico '{svcName}' tipo={svcType} path='{driverPath}'");

                        // Check if service is currently running
                        bool isRunning = false;
                        try
                        {
                            IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
                            if (scm != IntPtr.Zero)
                            {
                                IntPtr svc = OpenService(scm, svcName, SERVICE_QUERY_STATUS);
                                if (svc != IntPtr.Zero)
                                {
                                    isRunning = true;
                                    CloseServiceHandle(svc);
                                }
                                CloseServiceHandle(scm);
                            }
                        }
                        catch { }

                        results.Add(new BlockingDriverInfo
                        {
                            DriverName = Path.GetFileNameWithoutExtension(driverPath),
                            ServiceName = svcName,
                            DisplayName = $"{svcName} ({driverPath})",
                            DriverPath = driverPath,
                            CurrentState = isRunning ? "Running" : "Unknown",
                            IsSelected = true
                        });
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Logger.Log($"[DRIVER] Registry scan ERRO: {ex.Message}"); }
            return results;
        }
    }
}
