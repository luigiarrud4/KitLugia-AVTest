using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace KitLugia.Core.TaskManager
{
    /// <summary>
    /// Ultra-fast process enumeration via Rust native library (rust_native.dll).
    /// Uses NtQuerySystemInformation + QueryFullProcessImageNameW for zero-exception scanning.
    /// Falls back to Process.GetProcesses() if the native library is not available.
    /// FIX: probe com buffer pequeno, evita Access Violation, corrige Pack e estouro
    /// </summary>
    public static class SafeProcessHelper
    {
        private const string DllName = "rust_native.dll";
        private static bool? _nativeAvailable;

        // FIX: remover Pack=1 (desalinhamento se Rust usa #[repr(C)]). Sem Pack = default (0) alinha como C.
        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessInfoRaw
        {
            public uint Pid;
            public uint ParentPid;
            public uint SessionId;
            public uint HandleCount;
            public int BasePriority;
            public byte IsSystem;
            // padding para alinhar a 4 bytes (sem Pack=1, o marshaler já alinha)
            private byte _pad0;
            private byte _pad1;
            private byte _pad2;
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int enumerate_processes_fast(IntPtr buffer, int maxCount);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int get_process_path_safe(uint pid, IntPtr outBuf, int outCapacity);

        public static bool IsNativeAvailable()
        {
            if (_nativeAvailable.HasValue) return _nativeAvailable.Value;
            // FIX: não passar IntPtr.Zero (pode causar AV na DLL nativa). Use buffer pequeno.
            IntPtr tmp = IntPtr.Zero;
            try
            {
                tmp = Marshal.AllocHGlobal(16 * sizeof(char));
                int r = get_process_path_safe(4, tmp, 16);
                // Se retornou >=0 ou -ERROR_INSUFFICIENT_BUFFER, a DLL existe e não crashou
                _nativeAvailable = true;
            }
            catch (DllNotFoundException) { _nativeAvailable = false; }
            catch (EntryPointNotFoundException) { _nativeAvailable = false; }
            catch { _nativeAvailable = false; }
            finally { if (tmp != IntPtr.Zero) Marshal.FreeHGlobal(tmp); }
            return _nativeAvailable.Value;
        }

        public static string GetProcessPath(int pid)
        {
            if (!IsNativeAvailable()) return GetProcessPathFallback(pid);
            if (pid <= 4) return "";
            const int cap = 1024;
            var buf = Marshal.AllocHGlobal(cap * sizeof(char));
            try
            {
                int len = get_process_path_safe((uint)pid, buf, cap);
                // FIX: se len >= cap, houve truncamento/estouro; descarta
                if (len <= 0 || len >= cap) return "";
                return Marshal.PtrToStringUni(buf, len) ?? "";
            }
            catch { return ""; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public static Dictionary<int, string> GetProcessPathsBatch(IEnumerable<int> pids)
        {
            var result = new Dictionary<int, string>();
            if (!IsNativeAvailable())
            {
                foreach (var pid in pids) result[pid] = GetProcessPathFallback(pid);
                return result;
            }
            const int cap = 1024;
            var buf = Marshal.AllocHGlobal(cap * sizeof(char));
            try
            {
                foreach (var pid in pids)
                {
                    if (pid <= 4) { result[pid] = ""; continue; }
                    try
                    {
                        int len = get_process_path_safe((uint)pid, buf, cap);
                        if (len > 0 && len < cap) result[pid] = Marshal.PtrToStringUni(buf, len) ?? "";
                        else result[pid] = ""; // truncado ou erro
                    }
                    catch { result[pid] = ""; }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return result;
        }

        private static string GetProcessPathFallback(int pid)
        {
            // Fallback sem usar MainModule (evita Win32Exception WoW64 / AccessDenied)
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return "";
                var sb = new StringBuilder(1024);
                uint cap = (uint)sb.Capacity;
                if (QueryFullProcessImageNameW(h, 0, sb, ref cap) && cap > 0)
                    return sb.ToString(0, (int)cap);
                return "";
            }
            catch { return ""; }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
        }

        // ── Native high-level helpers ────────────────────────
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr TokenHandle, uint TokenInformationClass, IntPtr TokenInformation, uint TokenInformationLength, out uint ReturnLength);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LookupAccountSidW(string? lpSystemName, IntPtr Sid, StringBuilder? lpName, ref uint cchName, StringBuilder? lpReferencedDomainName, ref uint cchReferencedDomainName, out uint peUse);
        private const uint TOKEN_QUERY = 0x0008;
        private const uint TokenUser = 1;

        /// <summary>
        /// Obtém dono do processo via token nativo (&lt;0.1ms) — substitui WMI lento.
        /// </summary>
        public static string GetProcessUserFast(int pid)
        {
            if (pid == 0) return "System Idle Process";
            if (pid == 4) return "NT AUTHORITY\\SYSTEM";
            IntPtr hProc = IntPtr.Zero;
            IntPtr hTok = IntPtr.Zero;
            IntPtr pInfo = IntPtr.Zero;
            try
            {
                hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hProc == IntPtr.Zero) return "—";
                if (!OpenProcessToken(hProc, TOKEN_QUERY, out hTok) || hTok == IntPtr.Zero) return "—";
                uint need = 0;
                GetTokenInformation(hTok, TokenUser, IntPtr.Zero, 0, out need);
                if (need == 0 || need > 4096) return "—";
                pInfo = Marshal.AllocHGlobal((int)need);
                if (!GetTokenInformation(hTok, TokenUser, pInfo, need, out _)) return "—";
                IntPtr pSid = Marshal.ReadIntPtr(pInfo);
                if (pSid == IntPtr.Zero) return "—";
                uint cchName = 0, cchDom = 0; uint peUse;
                LookupAccountSidW(null, pSid, null, ref cchName, null, ref cchDom, out peUse);
                if (cchName == 0) return "—";
                var name = new StringBuilder((int)cchName);
                var dom = new StringBuilder((int)cchDom);
                if (!LookupAccountSidW(null, pSid, name, ref cchName, dom, ref cchDom, out peUse)) return name.ToString();
                if (dom.Length > 0) return $"{dom}\\{name}";
                return name.ToString();
            }
            catch { return "—"; }
            finally
            {
                if (pInfo != IntPtr.Zero) Marshal.FreeHGlobal(pInfo);
                if (hTok != IntPtr.Zero) CloseHandle(hTok);
                if (hProc != IntPtr.Zero) CloseHandle(hProc);
            }
        }

        /// <summary>
        /// Enumerates all processes via Rust NtQuerySystemInformation, returns pid → info.
        /// Returns null if native not available or failed.
        /// Reutiliza código já validado em task_scan.rs: enumerate_processes_fast.
        /// </summary>
        public static Dictionary<int, ProcessInfoRaw>? TryEnumerateFast()
        {
            if (!IsNativeAvailable()) return null;
            // Buffer dinâmico: se atingir 2048, tenta 4096/8192 (servidor/Docker pode ter >2k)
            int[] sizes = { 2048, 4096, 8192 };
            int sz = Marshal.SizeOf<ProcessInfoRaw>();
            foreach (var max in sizes)
            {
                IntPtr buf = Marshal.AllocHGlobal(sz * max);
                try
                {
                    int count = enumerate_processes_fast(buf, max);
                    if (count < 0) return null;
                    if (count == 0) return new Dictionary<int, ProcessInfoRaw>();
                    // Se encheu o buffer, pode ter truncado — tenta maior
                    if (count >= max && max != sizes[sizes.Length - 1]) continue;
                    int safe = Math.Min(count, max);
                    var dict = new Dictionary<int, ProcessInfoRaw>(safe);
                    for (int i = 0; i < safe; i++)
                    {
                        IntPtr ptr = IntPtr.Add(buf, i * sz);
                        var info = Marshal.PtrToStructure<ProcessInfoRaw>(ptr);
                        dict[(int)info.Pid] = info;
                    }
                    return dict;
                }
                catch { return null; }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return null;
        }

        /// <summary>
        /// Stackalloc-friendly single-path fetch that avoids heap alloc (for hot loops).
        /// Uses native buffer on heap but minimized; fallback uses OpenProcess.
        /// </summary>
        public static string GetProcessPathFast(int pid)
        {
            return GetProcessPath(pid);
        }
    }
}
