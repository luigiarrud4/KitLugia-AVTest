using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace KitLugia.Core
{
    /// <summary>
    /// Native wrapper for the Win32 registry scanner exposed by rust_native.dll
    /// (reg_scan_ffi). Mirrors ScanHiveForNames / ScanSoftwareRecursive /
    /// ScanHiveByValues of DeepUninstaller but enumerates the hive directly with
    /// RegEnumKeyExW / RegEnumValueW / RegQueryValueExW, skipping binary blobs.
    /// </summary>
    public static class NativeRegistry
    {
        private const string RustDll = "rust_native.dll";

        // mode: 0 = flat names+value (ScanHiveForNames),
        //       1 = recursive names+value (ScanSoftwareRecursive),
        //       2 = flat value-only (ScanHiveByValues)
        // Returns count of NUL-terminated UTF-16 strings written into outBuffer,
        // or -(count+1) when the buffer is too small (required count signalled).
        [DllImport(RustDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int reg_scan_ffi(
            string rootPath,
            string displayName,
            string installLocation,
            string exclusions,
            int mode,
            IntPtr outBuffer,
            int outCapacity);

        public static readonly bool UseNative;

        static NativeRegistry()
        {
            try
            {
                IntPtr probe = Marshal.AllocHGlobal(128 * 2);
                try
                {
                    int n = reg_scan_ffi(
                        "HKEY_CURRENT_USER\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run",
                        "zz-test-zz",
                        string.Empty,
                        "zz-test-zz;Microsoft",
                        0,
                        probe,
                        128);
                    UseNative = n >= 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(probe);
                }
            }
            catch
            {
                UseNative = false;
            }
        }

        /// <summary>
        /// Runs a native registry scan for one hive path.
        /// Returns the list of full hive keys found, or null when the native
        /// scanner is unavailable/failed (caller should fall back to C#).
        /// </summary>
        public static List<string>? Scan(string rootPath, string displayName, string installLocation, List<string>? exclusions, int mode)
        {
            if (!UseNative)
                return null;
            if (string.IsNullOrEmpty(rootPath))
                return null;

            string excl = exclusions != null
                ? string.Join(";", exclusions)
                : string.Empty;

            // Two-pass with an IntPtr buffer so the NUL-terminated multi-string
            // is parsed manually (StringBuilder marshalling truncates at the first NUL).
            int capacity = 4096;
            IntPtr buffer = Marshal.AllocHGlobal(capacity * 2);
            try
            {
                int n;
                for (int attempt = 0; ; attempt++)
                {
                    n = reg_scan_ffi(rootPath, displayName, installLocation, excl, mode, buffer, capacity);
                    if (n >= 0)
                        break;
                    // -(count+1) signals the required capacity when the buffer was too small.
                    int need = -(n + 1) * 40 + 1024;
                    if (need <= capacity || attempt >= 2)
                        return null;
                    capacity = need;
                    Marshal.FreeHGlobal(buffer);
                    buffer = Marshal.AllocHGlobal(capacity * 2);
                }
                if (n == 0)
                    return new List<string>(0);

                var list = new List<string>(n);
                int pos = 0;
                for (int i = 0; i < n && pos < capacity; i++)
                {
                    string? s = Marshal.PtrToStringUni(IntPtr.Add(buffer, pos * 2));
                    if (string.IsNullOrEmpty(s))
                        break;
                    list.Add(s);
                    pos += s.Length + 1;
                }
                return list;
            }
            catch
            {
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}