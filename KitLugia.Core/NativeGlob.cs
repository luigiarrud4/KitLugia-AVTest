using System.Runtime.InteropServices;

namespace KitLugia.Core
{
    public static class NativeGlob
    {
        private const string RustDll = "rust_native.dll";

        [DllImport(RustDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool glob_match_ffi(string pattern, string path);

        public static readonly bool UseNative;

        static NativeGlob()
        {
            try
            {
                UseNative = glob_match_ffi("*.txt", "test.txt");
            }
            catch
            {
                UseNative = false;
            }
        }

        public static bool IsMatch(string pattern, string path)
        {
            if (UseNative)
                return glob_match_ffi(pattern, path);

            try
            {
                string regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                    .Replace("\\*", ".*").Replace("\\?", ".") + "$";
                return System.Text.RegularExpressions.Regex.IsMatch(path, regexPattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
    }
}
