using System.Runtime.InteropServices;

namespace KitLugia.Core
{
    public static class NativeSearch
    {
        private const string RustDll = "rust_native.dll";

        [DllImport(RustDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int search_score_ffi(string title, string desc, string query);

        public static readonly bool UseNative;

        static NativeSearch()
        {
            try
            {
                int result = search_score_ffi("test", "", "test");
                UseNative = (result == 100);
            }
            catch
            {
                UseNative = false;
            }
        }

        public static int Score(string title, string desc, string query)
        {
            if (!UseNative) return -2;
            return search_score_ffi(title, desc, query);
        }
    }
}
