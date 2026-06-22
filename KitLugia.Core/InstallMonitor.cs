using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace KitLugia.Core
{
    public class InstallMonitorChange
    {
        public string Path { get; set; }
        public string Type { get; set; }
        public string Category { get; set; }
        public DateTime Timestamp { get; set; }
        public string Details { get; set; }
    }

    public static class InstallMonitor
    {
        private static readonly ConcurrentBag<InstallMonitorChange> _changes = new();
        private static readonly List<FileSystemWatcher> _watchers = new();
        private static Dictionary<string, long> _registrySnapshot;
        private static bool _isRunning;
        private static string[] _watchDirs;

        public static bool IsRunning => _isRunning;
        public static int ChangeCount => _changes.Count;

        public static event Action<InstallMonitorChange> OnChange;

        public static void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _changes.Clear();

            _watchDirs = new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            };

            foreach (var dir in _watchDirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    var watcher = new FileSystemWatcher(dir)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                        InternalBufferSize = 65536
                    };

                    watcher.Created += (s, e) => AddChange(e.FullPath, "Criado", "Arquivo");
                    watcher.Deleted += (s, e) => AddChange(e.FullPath, "Removido", "Arquivo");
                    watcher.Renamed += (s, e) => AddChange(e.FullPath, "Renomeado", "Arquivo");
                    watcher.Error += (s, e) => { };

                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch { }
            }

            _registrySnapshot = null;
        }

        public static void Stop()
        {
            _isRunning = false;

            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch { }
            }
            _watchers.Clear();
        }

        public static List<InstallMonitorChange> GetChanges()
        {
            return _changes.OrderByDescending(c => c.Timestamp).ToList();
        }

        public static void ClearChanges()
        {
            _changes.Clear();
        }

        public static bool HasRegistrySnapshot => _registrySnapshot != null;

        public static void TakeRegistrySnapshot()
        {
            _registrySnapshot = DoTakeRegistrySnapshot();
        }

        public static int CompareRegistryWithSnapshot()
        {
            if (_registrySnapshot == null) return 0;

            var current = DoTakeRegistrySnapshot();
            int count = 0;

            foreach (var kvp in current)
            {
                if (!_registrySnapshot.ContainsKey(kvp.Key))
                {
                    AddChange(kvp.Key, "Adicionado", "Registro (Instalação)");
                    count++;
                }
            }

            foreach (var kvp in _registrySnapshot)
            {
                if (!current.ContainsKey(kvp.Key))
                {
                    AddChange(kvp.Key, "Removido", "Registro (Desinstalação)");
                    count++;
                }
            }

            _registrySnapshot = current;
            return count;
        }

        private static void AddChange(string path, string type, string category)
        {
            var change = new InstallMonitorChange
            {
                Path = path,
                Type = type,
                Category = category,
                Timestamp = DateTime.Now,
                Details = ""
            };
            _changes.Add(change);
            OnChange?.Invoke(change);
        }

        private static Dictionary<string, long> DoTakeRegistrySnapshot()
        {
            var snapshot = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            string[] paths = {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (var regPath in paths)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(regPath);
                    if (key != null)
                    {
                        foreach (var sub in key.GetSubKeyNames())
                        {
                            try
                            {
                                using var subKey = key.OpenSubKey(sub);
                                if (subKey != null)
                                {
                                    string dn = subKey.GetValue("DisplayName") as string;
                                    long installDate = 0;
                                    if (long.TryParse(subKey.GetValue("InstallDate") as string ?? "", out var d))
                                        installDate = d;

                                    string displayKey = $"{sub}\\{dn ?? sub}";
                                    snapshot[displayKey] = installDate;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            return snapshot;
        }
    }
}
