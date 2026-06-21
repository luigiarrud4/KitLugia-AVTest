using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace KitLugia.Core
{
    public class LeftoverJunkEntry
    {
        public string AppName { get; set; } = "";
        public DateTime Date { get; set; }
        public List<string> LeftoverFiles { get; set; } = new();
        public List<string> LeftoverRegistry { get; set; } = new();
    }

    public static class LeftoverJunkManager
    {
        private static readonly string FolderPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KitLugia");
        private static readonly string FilePath = Path.Combine(FolderPath, "LeftoverJunk.json");
        private static readonly object _lock = new();

        public static List<LeftoverJunkEntry> Load()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(FilePath))
                        return new List<LeftoverJunkEntry>();
                    string json = File.ReadAllText(FilePath);
                    return JsonSerializer.Deserialize<List<LeftoverJunkEntry>>(json) ?? new List<LeftoverJunkEntry>();
                }
                catch
                {
                    return new List<LeftoverJunkEntry>();
                }
            }
        }

        public static void Save(List<LeftoverJunkEntry> entries)
        {
            lock (_lock)
            {
                try
                {
                    if (!Directory.Exists(FolderPath))
                        Directory.CreateDirectory(FolderPath);
                    string json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(FilePath, json);
                }
                catch (Exception ex)
                {
                    Logger.LogError("LeftoverJunkManager.Save", ex.Message);
                }
            }
        }

        public static void Add(LeftoverJunkEntry entry)
        {
            var entries = Load();
            entries.Insert(0, entry);
            Save(entries);
        }

        public static void RemoveAt(int index)
        {
            var entries = Load();
            if (index >= 0 && index < entries.Count)
            {
                entries.RemoveAt(index);
                Save(entries);
            }
        }

        public static void Clear()
        {
            Save(new List<LeftoverJunkEntry>());
        }
    }
}
