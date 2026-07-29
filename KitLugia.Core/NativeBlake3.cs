using System.IO;
using System.Runtime.InteropServices;

namespace KitLugia.Core
{
    public static class NativeBlake3
    {
        private const string RustDll = "rust_native.dll";

        [DllImport(RustDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int blake3_file_ffi(string path, byte[] outBuf, int outCapacity);

        [DllImport(RustDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int blake3_bytes_ffi(byte[] data, int length, byte[] outBuf, int outCapacity);

        public static readonly bool UseNative;

        static NativeBlake3()
        {
            try
            {
                int result = blake3_file_ffi("", new byte[65], 65);
                UseNative = (result == -1);
            }
            catch
            {
                UseNative = false;
            }
        }

        public static string HashFile(string filePath)
        {
            if (UseNative)
            {
                byte[] buf = new byte[65];
                int result = blake3_file_ffi(filePath, buf, buf.Length);
                if (result == 0)
                {
                    int len = System.Array.IndexOf(buf, (byte)0);
                    if (len < 0) len = 64;
                    return System.Text.Encoding.ASCII.GetString(buf, 0, len);
                }
                return null;
            }

            try
            {
                using var stream = File.OpenRead(filePath);
                var hash = System.Security.Cryptography.SHA256.HashData(stream);
                return System.Convert.ToHexStringLower(hash);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return null; }
        }

        public static string HashBytes(byte[] data)
        {
            if (UseNative && data != null && data.Length > 0)
            {
                byte[] buf = new byte[65];
                int result = blake3_bytes_ffi(data, data.Length, buf, buf.Length);
                if (result == 0)
                {
                    int len = System.Array.IndexOf(buf, (byte)0);
                    if (len < 0) len = 64;
                    return System.Text.Encoding.ASCII.GetString(buf, 0, len);
                }
                return null;
            }

            try
            {
                if (data == null || data.Length == 0) return null;
                var hash = System.Security.Cryptography.SHA256.HashData(data);
                return System.Convert.ToHexStringLower(hash);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return null; }
        }
    }
}
