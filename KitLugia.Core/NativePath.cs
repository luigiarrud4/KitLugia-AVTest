using System.Runtime.InteropServices;

namespace KitLugia.Core
{
    public static class NativePath
    {
        private const string RustDll = "rust_native.dll";

        [DllImport(RustDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int analyze_path_problems_ffi(string pathValue);

        public static readonly bool UseNative;

        static NativePath()
        {
            try
            {
                int result = analyze_path_problems_ffi("test");
                UseNative = (result == 2);
            }
            catch
            {
                UseNative = false;
            }
        }

        public static PathProblem Analyze(string pathValue)
        {
            if (!UseNative) return PathProblem.None;
            return (PathProblem)analyze_path_problems_ffi(pathValue);
        }
    }
}
