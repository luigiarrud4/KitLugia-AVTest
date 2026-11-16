using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace KitLugia.Core
{
    /// <summary>
    /// Fonte UNICA de verdade sobre os executaveis do KitLugia.
    ///
    /// CONTEXTO (16/11/2026): o release passou a ter DOIS executaveis na raiz —
    /// o LAUNCHER (KitLugia.exe, net48), que verifica o .NET antes de abrir o
    /// app, e o APP (KitLugia.App.exe). O usuario precisa clicar em UM arquivo
    /// so; por isso o app foi renomeado para "KitLugia.App.exe" (o nome antigo
    /// "KitLugia.GUI.exe" e' o do build de desenvolvimento e de installs antigos).
    ///
    /// Tudo que precisa montar caminho de executavel (menu de contexto, atalho
    /// do Desktop, auto-start, updater, ISO, WinPE) passa por aqui — assim o
    /// nome do arquivo nunca mais fica "chumbado" em varios lugares.
    /// </summary>
    public static class AppPaths
    {
        /// <summary>Launcher (net48) — e' o que o usuario clica.</summary>
        public const string LauncherExeFileName = "KitLugia.exe";

        /// <summary>Nome do APP no release publicado.</summary>
        public const string AppExeFileName = "KitLugia.App.exe";

        /// <summary>Nome antigo do APP (build de dev / installs anteriores).</summary>
        public const string LegacyAppExeFileName = "KitLugia.GUI.exe";

        /// <summary>
        /// Caminho do executavel em EXECUCAO (o app, quando chamado de dentro do
        /// app; o launcher, quando chamado do launcher).
        /// </summary>
        public static string GetRunningExePath()
        {
            try
            {
                string path = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (!string.IsNullOrEmpty(path)) return path;
            }
            catch { }

            try { return Environment.ProcessPath ?? ""; }
            catch { return ""; }
        }

        /// <summary>True se o caminho aponta para o launcher.</summary>
        public static bool IsLauncherPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string name;
            try { name = Path.GetFileName(path); } catch { return false; }
            return string.Equals(name, LauncherExeFileName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Descobre o executavel do APP a partir de uma pasta. Ordem:
        /// nome novo -&gt; nome antigo -&gt; qualquer KitLugia*.exe que NAO seja o
        /// launcher. Devolve null se nao houver nada.
        /// </summary>
        public static string? ResolveAppExePath(string? baseDirectory)
        {
            if (string.IsNullOrEmpty(baseDirectory)) return null;

            try
            {
                string newName = Path.Combine(baseDirectory, AppExeFileName);
                if (File.Exists(newName)) return newName;

                string legacyName = Path.Combine(baseDirectory, LegacyAppExeFileName);
                if (File.Exists(legacyName)) return legacyName;

                // Fallback: qualquer outro KitLugia*.exe (ex.: "KitLugia (2).exe"
                // de uma copia, ou um nome de build customizado) que nao seja o
                // launcher.
                foreach (string file in Directory.GetFiles(baseDirectory, "KitLugia*.exe")
                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileName(file);
                    if (string.Equals(name, LauncherExeFileName, StringComparison.OrdinalIgnoreCase)) continue;
                    return file;
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Caminho do APP para uso em registro/atalhos: o que roda agora se for
        /// o app; senao o que existe na pasta do executavel atual.
        /// </summary>
        public static string GetAppExePathForRegistration()
        {
            string running = GetRunningExePath();
            if (!string.IsNullOrEmpty(running) && !IsLauncherPath(running))
                return running;

            string? resolved = ResolveAppExePath(
                Path.GetDirectoryName(running) ?? AppContext.BaseDirectory);
            return resolved ?? running;
        }

        /// <summary>Caminho do launcher na mesma pasta do app (null se nao existir).</summary>
        public static string? ResolveLauncherPath(string? baseDirectory)
        {
            if (string.IsNullOrEmpty(baseDirectory)) return null;
            try
            {
                string launcher = Path.Combine(baseDirectory, LauncherExeFileName);
                return File.Exists(launcher) ? launcher : null;
            }
            catch { return null; }
        }
    }
}
