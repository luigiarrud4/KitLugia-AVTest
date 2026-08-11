using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace KitLugia.Core
{
    public static class NativeSha256
    {
        private const string RustDll = "rust_native.dll";

        [DllImport(RustDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int sha256_file_ffi(string path, byte[] outBuf, int outCapacity);

        public static readonly bool UseNative;

        static NativeSha256()
        {
            try
            {
                int result = sha256_file_ffi("", new byte[65], 65);
                UseNative = (result == -1);
            }
            catch
            {
                UseNative = false;
            }
        }

        public static string? ComputeHash(string filePath)
        {
            if (UseNative)
            {
                byte[] buf = new byte[65];
                int result = sha256_file_ffi(filePath, buf, buf.Length);
                if (result == 0)
                {
                    int len = Array.IndexOf(buf, (byte)0);
                    if (len < 0) len = 64;
                    return System.Text.Encoding.ASCII.GetString(buf, 0, len);
                }
                return null;
            }

            try
            {
                using var stream = File.OpenRead(filePath);
                byte[] hash = SHA256.HashData(stream);
                return Convert.ToHexStringLower(hash);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return null; }
        }
    }
}
