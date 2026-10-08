using System;
using System.Runtime.InteropServices;

namespace KitLugia.Core
{
    /// <summary>
    /// Helper para detectar versão do Windows e informações do sistema
    /// </summary>
    public static class SystemInfo
    {
        [DllImport("kernel32.dll")]
        private static extern bool GetVersionEx(ref OSVERSIONINFOEX lpVersionInfo);

        [StructLayout(LayoutKind.Sequential)]
        private struct OSVERSIONINFOEX
        {
            public uint dwOSVersionInfoSize;
            public uint dwMajorVersion;
            public uint dwMinorVersion;
            public uint dwBuildNumber;
            public uint dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
            public ushort wServicePackMajor;
            public ushort wServicePackMinor;
            public ushort wSuiteMask;
            public byte wProductType;
            public byte wReserved;
        }

        public enum WindowsVersion
        {
            Unknown,
            Windows7,
            Windows8,
            Windows81,
            Windows10,
            Windows11,
            WindowsServer
        }

        private static WindowsVersion? _cachedVersion;
        private static string _cachedVersionString = string.Empty;

        /// <summary>
        /// Detecta a versão do Windows
        /// </summary>
        public static WindowsVersion GetWindowsVersion()
        {
            if (_cachedVersion.HasValue)
                return _cachedVersion.Value;

            try
            {
                var osInfo = new OSVERSIONINFOEX();
                osInfo.dwOSVersionInfoSize = (uint)Marshal.SizeOf(typeof(OSVERSIONINFOEX));

                if (GetVersionEx(ref osInfo))
                {
                    WindowsVersion version = osInfo.dwMajorVersion switch
                    {
                        6 when osInfo.dwMinorVersion == 1 => WindowsVersion.Windows7,
                        6 when osInfo.dwMinorVersion == 2 => WindowsVersion.Windows8,
                        6 when osInfo.dwMinorVersion == 3 => WindowsVersion.Windows81,
                        10 when osInfo.dwBuildNumber >= 22000 => WindowsVersion.Windows11,
                        10 => WindowsVersion.Windows10,
                        _ => WindowsVersion.Unknown
                    };

                    // Detectar Windows Server
                    if (osInfo.wProductType == 3) // VER_NT_SERVER
                    {
                        version = WindowsVersion.WindowsServer;
                    }

                    _cachedVersion = version;
                    return version;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("SystemInfo.GetWindowsVersion", $"Erro: {ex.Message}");
            }

            _cachedVersion = WindowsVersion.Unknown;
            return WindowsVersion.Unknown;
        }

        /// <summary>
        /// Retorna string amigável da versão do Windows
        /// </summary>
        public static string GetWindowsVersionString()
        {
            if (!string.IsNullOrEmpty(_cachedVersionString))
                return _cachedVersionString;

            var version = GetWindowsVersion();
            _cachedVersionString = version switch
            {
                WindowsVersion.Windows7 => "Windows 7",
                WindowsVersion.Windows8 => "Windows 8",
                WindowsVersion.Windows81 => "Windows 8.1",
                WindowsVersion.Windows10 => "Windows 10",
                WindowsVersion.Windows11 => "Windows 11",
                WindowsVersion.WindowsServer => "Windows Server",
                _ => "Desconhecido"
            };

            return _cachedVersionString;
        }

        /// <summary>
        /// Verifica se o sistema é 64-bit
        /// </summary>
        public static bool Is64Bit()
        {
            return Environment.Is64BitOperatingSystem;
        }

        /// <summary>
        /// Verifica se uma feature é suportada na versão atual do Windows
        /// </summary>
        /// <param name="feature">Nome da feature (ex: "RegistryCache", "CombineMemoryLists")</param>
        /// <returns>True se a feature é suportada</returns>
        public static bool IsFeatureSupported(string feature)
        {
            var version = GetWindowsVersion();

            return feature switch
            {
                "RegistryCache" => version == WindowsVersion.Windows81 || version == WindowsVersion.Windows10 || version == WindowsVersion.Windows11,
                "CombineMemoryLists" => version == WindowsVersion.Windows10 || version == WindowsVersion.Windows11,
                "StandbyListPriority" => version == WindowsVersion.Windows10 || version == WindowsVersion.Windows11,
                "Windows11Only" => version == WindowsVersion.Windows11,
                "Windows10OrLater" => version == WindowsVersion.Windows10 || version == WindowsVersion.Windows11,
                "Windows81OrLater" => version == WindowsVersion.Windows81 || version == WindowsVersion.Windows10 || version == WindowsVersion.Windows11,
                _ => true
            };
        }
    }
}
