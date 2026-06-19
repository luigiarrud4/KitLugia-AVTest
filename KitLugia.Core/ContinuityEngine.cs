using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static class ContinuityEngine
    {
        public record ContinuityDossier(
            string PCName,
            string UserSID,
            string UserName,
            List<string> InstalledApps,
            List<UserFolderMapping> Folders,
            DateTime CreatedAt
        );

        public record UserFolderMapping(string SourcePath, string Label, long SizeBytes);

        /// <summary>
        /// Gera um dossiê do estado atual do sistema para permitir a migração posterior.
        /// </summary>
        public static (bool Success, string Message) GenerateDossier(string savePath)
        {
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                var dossier = new ContinuityDossier(
                    Environment.MachineName,
                    identity.User?.Value ?? "Unknown",
                    Environment.UserName,
                    GetInstalledAppsList(),
                    GetDefaultUserFolders(),
                    DateTime.Now
                );

                string json = JsonSerializer.Serialize(dossier, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(savePath, ".kitlugia_meta"), json);

                return (true, "Dossiê de continuidade gerado com sucesso.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        private static List<string> GetInstalledAppsList()
        {

            // Típico: 50-200 apps instalados
            var apps = new List<string>(200);
            string[] keys = { 
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", 
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" 
            };

            foreach (var keyPath in keys)
            {
                using (var baseKey = Registry.LocalMachine.OpenSubKey(keyPath))
                {
                    if (baseKey == null) continue;
                    foreach (var subKeyName in baseKey.GetSubKeyNames())
                    {
                        using (var subKey = baseKey.OpenSubKey(subKeyName))
                        {
                            string? name = subKey?.GetValue("DisplayName")?.ToString();
                            if (!string.IsNullOrEmpty(name)) apps.Add(name);
                        }
                    }
                }
            }
            return apps.OrderBy(a => a).ToList();
        }

        private static List<UserFolderMapping> GetDefaultUserFolders()
        {
            string userPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // Típico: 5-10 pastas de usuário
            var mappings = new List<UserFolderMapping>(10);

            string[] targets = { "Documents", "Downloads", "Desktop", "Music", "Videos", "Pictures", "AppData\\Roaming" };
            foreach (var t in targets)
            {
                string fullPath = Path.Combine(userPath, t);
                if (Directory.Exists(fullPath))
                {
                    // Obtém tamanho (pode ser lento, ideal usar o scanner ultra-rápido depois)
                    mappings.Add(new UserFolderMapping(fullPath, t, 0));
                }
            }
            return mappings;
        }

        /// <summary>
        /// Scanner de arquivos ultra-rápido (Prototipagem de MFT-like scan).
        /// Usa EnumerateFileSystemInfos para melhor performance que GetFiles.
        /// </summary>
        public static long GetFolderSizeFast(string path)
        {
            try
            {
                return new DirectoryInfo(path)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(fi => fi.Length);
            }
            catch { return 0; }
        }
    }
}
