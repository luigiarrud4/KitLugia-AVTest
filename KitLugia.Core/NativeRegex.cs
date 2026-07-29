using System.Runtime.InteropServices;

namespace KitLugia.Core
{
    public sealed class RegexMatchResult
    {
        public bool Success { get; }
        public string Value { get; }
        public string[] Groups { get; }

        internal RegexMatchResult(string[] groups)
        {
            Success = true;
            Value = groups[0];
            Groups = groups;
        }

        private RegexMatchResult()
        {
            Success = false;
            Value = "";
            Groups = [];
        }

        internal static readonly RegexMatchResult Failed = new();
    }

    public static class NativeRegex
    {
        private const string RustDll = "rust_native.dll";

        [DllImport(RustDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool regex_match_ffi(string text, string pattern);

        [DllImport(RustDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int regex_replace_ffi(string text, string pattern, string replacement, char[] outBuf, int outCapacity);

        [DllImport(RustDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int regex_capture_ffi(string text, string pattern, [MarshalAs(UnmanagedType.Bool)] bool caseInsensitive, char[] outBuf, int outCapacity);

        public static readonly bool UseNative;

        static NativeRegex()
        {
            try
            {
                UseNative = regex_match_ffi("test", "^test$");
            }
            catch
            {
                UseNative = false;
            }
        }

        public static bool IsMatch(string text, string pattern)
        {
            if (UseNative)
                return regex_match_ffi(text, pattern);

            try
            {
                return System.Text.RegularExpressions.Regex.IsMatch(text, pattern);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static string Replace(string text, string pattern, string replacement)
        {
            if (UseNative)
            {
                char[] buf = new char[4096];
                int result = regex_replace_ffi(text, pattern, replacement, buf, buf.Length);
                if (result >= 0)
                    return new string(buf, 0, result);
            }

            try
            {
                return System.Text.RegularExpressions.Regex.Replace(text, pattern, replacement);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return text; }
        }

        public static RegexMatchResult Match(string text, string pattern, bool caseInsensitive = false)
        {
            if (UseNative)
            {
                char[] buf = new char[4096];
                int count = regex_capture_ffi(text, pattern, caseInsensitive, buf, buf.Length);
                if (count > 0)
                {
                    var groups = new List<string>(count);
                    int start = 0;
                    for (int i = 0; i < count; i++)
                    {
                        int end = Array.IndexOf(buf, '\0', start);
                        if (end < 0) break;
                        groups.Add(new string(buf, start, end - start));
                        start = end + 1;
                    }
                    if (groups.Count > 0)
                        return new RegexMatchResult(groups.ToArray());
                }
                return RegexMatchResult.Failed;
            }

            try
            {
                var m = System.Text.RegularExpressions.Regex.Match(text, pattern,
                    caseInsensitive ? System.Text.RegularExpressions.RegexOptions.IgnoreCase :
                        System.Text.RegularExpressions.RegexOptions.None);
                if (m.Success)
                {
                    var groups = new string[m.Groups.Count];
                    for (int i = 0; i < m.Groups.Count; i++)
                        groups[i] = m.Groups[i].Value;
                    return new RegexMatchResult(groups);
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            return RegexMatchResult.Failed;
        }
    }
}
