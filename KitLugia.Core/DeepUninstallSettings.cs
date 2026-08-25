using Microsoft.Win32;
using System;
using System.IO;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    /// <summary>
    /// Central de configuracoes do desinstalador (estilo Revo Uninstaller):
    /// toggles persistentes em HKCU\Software\KitLugia\DeepUninstall.
    /// Leitura sob demanda com cache p/ nao bater no registro a cada acesso.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class DeepUninstallSettings
    {
        private const string RegPath = @"Software\KitLugia\DeepUninstall";

        private static bool _loaded;
        private static bool _sendToRecycleBin = true;
        private static bool _killProcessesBeforeUninstall = true;
        private static bool _disableScanAfterUninstall;
        private static bool _selectLeftoversByDefault = true;
        private static bool _ignoreRecent24H;

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegPath);
                if (key == null) return;
                _sendToRecycleBin = ReadFlag(key, "SendToRecycleBin", true);
                _killProcessesBeforeUninstall = ReadFlag(key, "KillProcesses", true);
                _disableScanAfterUninstall = ReadFlag(key, "DisableScan", false);
                _selectLeftoversByDefault = ReadFlag(key, "SelectLeftovers", true);
                _ignoreRecent24H = ReadFlag(key, "IgnoreRecent24H", false);
            }
            catch { }
        }

        private static bool ReadFlag(RegistryKey key, string name, bool def)
        {
            var v = key.GetValue(name);
            return v switch
            {
                int i => i != 0,
                long l => l != 0,
                string s when bool.TryParse(s, out var b) => b,
                _ => def
            };
        }

        private static void SetFlag(string name, bool value, ref bool field)
        {
            field = value;
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegPath);
                key?.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { }
        }

        /// <summary>Enviar arquivos/pastas para a Lixeira em vez de apagar permanentemente (DelToBin).</summary>
        public static bool SendToRecycleBin
        {
            get { EnsureLoaded(); return _sendToRecycleBin; }
            set { SetFlag("SendToRecycleBin", value, ref _sendToRecycleBin); }
        }

        /// <summary>Encerrar processos do aplicativo antes de rodar o desinstalador (StopRunExe).</summary>
        public static bool KillProcessesBeforeUninstall
        {
            get { EnsureLoaded(); return _killProcessesBeforeUninstall; }
            set { SetFlag("KillProcesses", value, ref _killProcessesBeforeUninstall); }
        }

        /// <summary>Nao escanear residuos automaticamente apos a desinstalacao.</summary>
        public static bool DisableScanAfterUninstall
        {
            get { EnsureLoaded(); return _disableScanAfterUninstall; }
            set { SetFlag("DisableScan", value, ref _disableScanAfterUninstall); }
        }

        /// <summary>Marcar residuos seguros/moderados como selecionados por padrao no review.</summary>
        public static bool SelectLeftoversByDefault
        {
            get { EnsureLoaded(); return _selectLeftoversByDefault; }
            set { SetFlag("SelectLeftovers", value, ref _selectLeftoversByDefault); }
        }

        /// <summary>Ignorar residuos acessados nas ultimas 24h (provavelmente ainda em uso).</summary>
        public static bool IgnoreRecent24H
        {
            get { EnsureLoaded(); return _ignoreRecent24H; }
            set { SetFlag("IgnoreRecent24H", value, ref _ignoreRecent24H); }
        }

        /// <summary>
        /// Raiz persistente dos artefatos do desinstalador (%LOCALAPPDATA%\KitLugia):
        /// backups de arquivos/registro e logs sobrevivem ao reboot (undo pos-reboot).
        /// </summary>
        public static string PersistentRoot
        {
            get
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KitLugia");
                try { Directory.CreateDirectory(root); } catch { }
                return root;
            }
        }
    }
}