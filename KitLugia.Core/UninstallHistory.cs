using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;

namespace KitLugia.Core
{
    /// <summary>
    /// Entrada do historico de desinstalacao (undo persistente pos-reboot).
    /// Gurada quando um cleanup roda: backups de arquivos (origem|backup),
    /// backups de registro (.reg), log de delecao e contadores.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class UninstallHistoryEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string AppName { get; set; } = "";
        public int FilesDeleted { get; set; }
        public int RegistryDeleted { get; set; }
        public List<string> FilesBackedUp { get; set; } = new();
        public List<string> RegistryBackups { get; set; } = new();
        public string? DeletionLogFile { get; set; }
    }

    /// <summary>
    /// Log persistente de cleanups (estilo Revo "Uninstall History"): registra cada
    /// PerformCleanup em disco para permitir restauracao mesmo depois de reiniciar o PC.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class UninstallHistory
    {
        private const int MaxEntries = 50;

        public static string HistoryFile => Path.Combine(DeepUninstallSettings.PersistentRoot, "UninstallHistory.json");

        public static List<UninstallHistoryEntry> Load()
        {
            try
            {
                if (!File.Exists(HistoryFile)) return new List<UninstallHistoryEntry>();
                var list = JsonSerializer.Deserialize<List<UninstallHistoryEntry>>(File.ReadAllText(HistoryFile));
                return list ?? new List<UninstallHistoryEntry>();
            }
            catch { return new List<UninstallHistoryEntry>(); }
        }

        private static void Save(List<UninstallHistoryEntry> entries)
        {
            try
            {
                File.WriteAllText(HistoryFile,
                    JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public static void Record(UninstallHistoryEntry entry)
        {
            try
            {
                var entries = Load();
                entries.Insert(0, entry);
                if (entries.Count > MaxEntries)
                    entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
                Save(entries);
            }
            catch { }
        }

        public static UninstallHistoryEntry? Find(string id)
        {
            return Load().FirstOrDefault(e => e.Id == id);
        }

        /// <summary>Remove a entrada e apaga os backups associados a ela.</summary>
        public static void Remove(string id)
        {
            var entries = Load();
            var removed = entries.Where(e => e.Id == id).ToList();
            entries.RemoveAll(e => e.Id == id);
            Save(entries);
            foreach (var e in removed)
            {
                foreach (var fb in e.FilesBackedUp)
                {
                    var parts = fb.Split('|', 2);
                    if (parts.Length == 2) TryDeletePath(parts[1]);
                }
                foreach (var rb in e.RegistryBackups)
                    TryDeletePath(rb);
            }
        }

        private static void TryDeletePath(string p)
        {
            try
            {
                if (Directory.Exists(p)) Directory.Delete(p, true);
                else if (File.Exists(p)) File.Delete(p);
            }
            catch { }
        }
    }
}