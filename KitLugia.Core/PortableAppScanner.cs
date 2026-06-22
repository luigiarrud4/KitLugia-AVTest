using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace KitLugia.Core
{
    public class PortableAppEntry
    {
        public string Name { get; set; }
        public string FolderPath { get; set; }
        public string MainExecutable { get; set; }
        public long TotalSizeBytes { get; set; }
        public string TotalSizeFormatted => TotalSizeBytes switch
        {
            >= 1_073_741_824 => $"{TotalSizeBytes / 1_073_741_824.0:N1} GB",
            >= 1_048_576 => $"{TotalSizeBytes / 1_048_576.0:N1} MB",
            >= 1_024 => $"{TotalSizeBytes / 1_024.0:N1} KB",
            _ => $"{TotalSizeBytes} B"
        };
        public DateTime LastModified { get; set; }
        public int Confidence { get; set; }
        public string ConfidenceLabel => Confidence switch
        {
            >= 80 => "Alta",
            >= 50 => "Média",
            _ => "Baixa"
        };
    }

    public static class PortableAppScanner
    {
        private static readonly HashSet<string> _excludedFolderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "System32", "SysWOW64", "Program Files", "Program Files (x86)",
            "ProgramData", "AppData", "Application Data", "Config.Msi", "PerfLogs",
            "Recovery", "System Volume Information", "$Recycle.Bin", "$WinREAgent",
            "Microsoft", "Common Files", "MSBuild", "Microsoft.NET", "Assembly",
            "node_modules", ".git", ".svn", ".vs", "packages"
        };

        private static readonly HashSet<string> _installerExePrefixes = new(StringComparer.OrdinalIgnoreCase)
        {
            "setup", "install", "uninstall", "vcredist", "dotnet", "dxsetup",
            "directx", "oemsetup", "autorun"
        };

        public static List<PortableAppEntry> Scan()
        {
            var results = new List<PortableAppEntry>();
            var scannedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var installedPaths = GetInstalledProgramPaths();
            var scanLocations = GetScanLocations();

            foreach (var root in scanLocations)
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    foreach (var dir in Directory.GetDirectories(root))
                    {
                        if (ShouldSkipFolder(dir, installedPaths)) continue;
                        if (!scannedFolders.Add(dir)) continue;

                        var entry = AnalyzeFolder(dir, installedPaths);
                        if (entry != null) results.Add(entry);
                    }
                }
                catch { }
            }

            return results.OrderByDescending(r => r.Confidence)
                          .ThenByDescending(r => r.TotalSizeBytes)
                          .ToList();
        }

        public static (bool success, string message) DeletePortableApp(PortableAppEntry entry)
        {
            try
            {
                if (!Directory.Exists(entry.FolderPath))
                    return (false, "Pasta não encontrada.");

                Directory.Delete(entry.FolderPath, true);
                return (true, $"{entry.Name} removido com sucesso.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao remover {entry.Name}: {ex.Message}");
            }
        }

        private static string[] GetScanLocations()
        {
            var locations = new List<string>();
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string downloads = Path.Combine(userProfile, "Downloads");
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            locations.Add(desktop);
            locations.Add(downloads);
            if (documents != desktop && documents != downloads)
                locations.Add(documents);

            string localPrograms = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            if (Directory.Exists(localPrograms))
                locations.Add(localPrograms);

            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                {
                    string root = drive.RootDirectory.FullName;
                    if (!root.StartsWith(
                        Environment.GetEnvironmentVariable("SystemDrive"),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        locations.Add(root);
                    }
                }
            }

            return locations.ToArray();
        }

        private static HashSet<string> GetInstalledProgramPaths()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            RegistryView[] regViews = { RegistryView.Registry64, RegistryView.Registry32 };

            foreach (var view in regViews)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var uninstallKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstallKey != null)
                    {
                        foreach (var sub in uninstallKey.GetSubKeyNames())
                        {
                            try
                            {
                                using var appKey = uninstallKey.OpenSubKey(sub);
                                if (appKey != null)
                                {
                                    string installPath = appKey.GetValue("InstallLocation") as string;
                                    if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                                        paths.Add(Path.GetFullPath(installPath).TrimEnd('\\'));
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            return paths;
        }

        private static bool ShouldSkipFolder(string folderPath, HashSet<string> installedPaths)
        {
            string folderName = Path.GetFileName(folderPath);

            if (_excludedFolderNames.Contains(folderName)) return true;

            if (installedPaths.Contains(Path.GetFullPath(folderPath).TrimEnd('\\'))) return true;

            var dirInfo = new DirectoryInfo(folderPath);
            if ((dirInfo.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden ||
                (dirInfo.Attributes & FileAttributes.System) == FileAttributes.System)
                return true;

            if (dirInfo.Name.StartsWith(".")) return true;

            return false;
        }

        private static PortableAppEntry AnalyzeFolder(string folderPath, HashSet<string> installedPaths)
        {
            try
            {
                var dirInfo = new DirectoryInfo(folderPath);
                var exeFiles = dirInfo.GetFiles("*.exe", SearchOption.TopDirectoryOnly);
                if (exeFiles.Length == 0) return null;

                var allExeFiles = dirInfo.GetFiles("*.exe", SearchOption.AllDirectories);
                var nonInstallerExes = allExeFiles
                    .Where(f => !_installerExePrefixes.Any(p =>
                        f.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (nonInstallerExes.Count == 0) return null;

                var mainExe = nonInstallerExes.OrderByDescending(f => f.Length).First();

                long totalSize = dirInfo.GetFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);

                if (totalSize < 1_048_576) return null;

                int confidence = 0;
                var allDlls = dirInfo.GetFiles("*.dll", SearchOption.AllDirectories);
                var allFiles = dirInfo.GetFiles("*", SearchOption.AllDirectories);

                if (allDlls.Length >= 3) confidence += 30;
                else if (allDlls.Length >= 1) confidence += 15;

                if (nonInstallerExes.Count >= 1 && allFiles.Length >= 10) confidence += 20;
                if (totalSize >= 10_485_760) confidence += 15;
                else if (totalSize >= 5_242_880) confidence += 10;

                bool hasUninsExe = allExeFiles.Any(f =>
                    f.Name.StartsWith("unins", StringComparison.OrdinalIgnoreCase));
                if (hasUninsExe) confidence -= 30;

                bool hasConfigFiles = allFiles.Any(f =>
                    f.Name.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase) ||
                    f.Name.Equals("settings.ini", StringComparison.OrdinalIgnoreCase) ||
                    f.Name.EndsWith(".conf", StringComparison.OrdinalIgnoreCase));
                if (hasConfigFiles) confidence += 10;

                if (exeFiles.Length == 1) confidence += 10;

                if (installedPaths.Contains(Path.GetFullPath(folderPath).TrimEnd('\\')))
                    return null;

                confidence = Math.Clamp(confidence, 0, 100);
                if (confidence < 30) return null;

                string appName = Path.GetFileNameWithoutExtension(mainExe.Name);
                if (appName.Length < 2) appName = dirInfo.Name;

                return new PortableAppEntry
                {
                    Name = appName,
                    FolderPath = folderPath,
                    MainExecutable = mainExe.FullName,
                    TotalSizeBytes = totalSize,
                    LastModified = dirInfo.LastWriteTime,
                    Confidence = confidence
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
