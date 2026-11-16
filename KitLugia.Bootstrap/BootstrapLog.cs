using System;
using System.IO;
using System.Text;

namespace KitLugia.Bootstrap
{
    /// <summary>
    /// Log simples do launcher em %LOCALAPPDATA%\KitLugia\bootstrap.log.
    /// O launcher roda em maquinas em que o Kit ainda NAO abre (sem runtime),
    /// entao o log e' a unica forma de o usuario (ou o suporte) ver o que houve.
    /// </summary>
    public static class BootstrapLog
    {
        private static readonly object _lock = new object();

        public static string LogPath
        {
            get
            {
                try
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KitLugia");
                    return Path.Combine(dir, "bootstrap.log");
                }
                catch { return Path.Combine(Path.GetTempPath(), "kitlugia-bootstrap.log"); }
            }
        }

        public static void Write(string message)
        {
            try
            {
                lock (_lock)
                {
                    string path = LogPath;
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    File.AppendAllText(path,
                        "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
