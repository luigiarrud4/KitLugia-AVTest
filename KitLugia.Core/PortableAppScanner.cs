using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KitLugia.Core
{
    public class PortableAppEntry
    {
        public string Name { get; set; } = string.Empty;
        public string FolderPath { get; set; } = string.Empty;
        public string MainExecutable { get; set; } = string.Empty;
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
            "node_modules", ".git", ".svn", ".vs", "packages",
            "Packages", "Temp", "IsolatedStorage", "MicrosoftEdge", "cache",
            "Caches", "Logs", "logs", "Temporary Internet Files"
        };

        private static readonly HashSet<string> _installerExePrefixes = new(StringComparer.OrdinalIgnoreCase)
        {
            "setup", "install", "uninstall", "vcredist", "dotnet", "dxsetup",
            "directx", "oemsetup", "autorun"
        };

        private static readonly HashSet<string> _subtreeSkipNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", ".git", ".svn", ".vs", "packages", "Package Cache",
            "cache", "Caches", "Logs", "logs", "Temp", "IsolatedStorage",
            "$Recycle.Bin", "System Volume Information", "Config.Msi", "Windows",
            "System32", "SysWOW64", "ProgramData", "Program Files", "Program Files (x86)",
            "Recovery", "MSBuild", "Microsoft.NET", "Assembly", "MicrosoftEdge",
            "Temporary Internet Files",
            "Application Data", "Local Settings", "My Documents", "NetHood",
            "PrintHood", "Recent", "SendTo", "Templates", "Start Menu"
        };

        private const int MaxDepth = 10;

        private const int FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private const int FILE_ATTRIBUTE_HIDDEN = 0x2;
        private const int FILE_ATTRIBUTE_SYSTEM = 0x4;
        private const int FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
        private const int SkippedAttributes = FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM | FILE_ATTRIBUTE_REPARSE_POINT;
        private const uint FIND_FIRST_EX_LARGE_FETCH = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
        private struct Win32FindData
        {
            public uint dwFileAttributes;
            public long ftCreationTime;
            public long ftLastAccessTime;
            public long ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileExW(
            string lpFileName, int fInfoLevelId, out Win32FindData lpFindFileData,
            int fSearchOp, IntPtr lpSearchFilter, uint dwAdditionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFileW(IntPtr hFindFile, out Win32FindData lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr hFindFile);

        private static readonly IntPtr InvalidHandleValue = new(-1);

        private static IEnumerable<(string Name, long Size, bool IsDir, long LastWriteFileTime)> EnumerateDirNative(string dir)
        {
            IntPtr handle = FindFirstFileExW(
                dir + "\\*", 1 /*FindExInfoBasic*/, out var data, 1 /*FindExSearchNameMatch*/,
                IntPtr.Zero, FIND_FIRST_EX_LARGE_FETCH);

            if (handle == InvalidHandleValue) yield break;

            try
            {
                do
                {
                    if ((data.dwFileAttributes & SkippedAttributes) != 0) continue;
                    if (data.cFileName == "." || data.cFileName == "..") continue;
                    bool isDir = (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
                    long size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
                    yield return (data.cFileName, size, isDir, data.ftLastWriteTime);
                }
                while (FindNextFileW(handle, out data));
            }
            finally
            {
                FindClose(handle);
            }
        }

        private readonly struct Candidate
        {
            public readonly bool IsMft;
            public readonly uint Rec;
            public readonly string FolderPath;
            public readonly MftIndex? Index;

            public Candidate(bool isMft, uint rec, string folderPath, MftIndex? index)
            {
                IsMft = isMft;
                Rec = rec;
                FolderPath = folderPath;
                Index = index;
            }
        }

        public static List<PortableAppEntry> Scan(Action<int, int>? progress = null, CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var installedPaths = GetInstalledProgramPaths();
            var scanLocations = GetScanLocations();
            Logger.Log($"[Portatil] Scan iniciado: {scanLocations.Length} local(is) de scan");
            var volumes = NativeMft.ScanAllVolumes(scanLocations);
            Logger.Log($"[Portatil] Scan MFT: {volumes?.Count ?? 0} volume(s) analisado(s) em {sw.Elapsed.TotalSeconds:N1}s");
            var scannedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<Candidate>();

            foreach (var root in scanLocations)
            {
                if (!Directory.Exists(root)) continue;
                string rootKey = (Path.GetPathRoot(root) ?? root).TrimEnd('\\', '/');
                var vol = volumes?.FirstOrDefault(v =>
                    string.Equals(v.VolumeRoot, rootKey, StringComparison.OrdinalIgnoreCase));
                if (vol == null || vol.VolumeFailed || vol.Index == null)
                {
                    Logger.Log($"[Portatil] {root} -> scan classico ({vol switch { null => "sem dados MFT", _ => $"volume falhou rc={vol.ErrorCode}" }})");
                    CollectClassicCandidates(root, installedPaths, scannedFolders, candidates);
                    continue;
                }
                int li = Array.IndexOf(vol.Locations, root);
                if (li < 0 || vol.PrefixRecs[li] == NativeMft.PrefixNotFound)
                {
                    Logger.Log($"[Portatil] {root} -> scan classico (prefixo MFT nao resolvido)");
                    CollectClassicCandidates(root, installedPaths, scannedFolders, candidates);
                    continue;
                }
                uint pr = vol.PrefixRecs[li];
                Logger.Log($"[Portatil] {root} -> MFT (prefixo rec {pr})");
                var idx = vol.Index;
                // Bounds check: ensure pr+2 is within the Starts array
                if (pr + 2 >= (uint)idx.Starts.Length)
                {
                    Logger.Log($"[Portatil] {root} -> scan classico (prefixo rec {pr} fora dos limites do MFT index)");
                    CollectClassicCandidates(root, installedPaths, scannedFolders, candidates);
                    continue;
                }
                int s = idx.Starts[(int)pr + 1];
                int e = idx.Starts[(int)pr + 2];
                if (e > idx.Entries.Length) e = idx.Entries.Length;
                for (int j = s; j < e; j++)
                {
                    var child = idx.Entries[j];
                    if ((child.Flags & (MftFlags.Reparse | MftFlags.HiddenSystem)) != 0) continue;
                    if (child.Name.StartsWith(".")) continue;
                    if (_excludedFolderNames.Contains(child.Name)) continue;
                    string full = Path.Combine(root, child.Name);
                    if (installedPaths.Contains(Path.GetFullPath(full).TrimEnd('\\'))) continue;
                    if (!scannedFolders.Add(full)) continue;
                    candidates.Add(new Candidate(true, child.Rec, full, idx));
                }
            }

            if (candidates.Count == 0) return new List<PortableAppEntry>();

            Logger.Log($"[Portatil] {candidates.Count} candidato(s) em {sw.Elapsed.TotalSeconds:N1}s - analisando...");
            var results = new List<PortableAppEntry>();
            int total = candidates.Count;
            int done = 0;
            var gate = new object();

            var po = new ParallelOptions { CancellationToken = ct };
            Parallel.ForEach(candidates, po, c =>
            {
                var entry = c.IsMft
                    ? AnalyzeFolderMft(c.Rec, c.FolderPath, c.Index!, installedPaths, ct)
                    : AnalyzeFolder(c.FolderPath, installedPaths, ct);
                if (entry != null)
                {
                    lock (gate) results.Add(entry);
                }
                if (progress != null)
                {
                    int d = Interlocked.Increment(ref done);
                    if ((d & 0x3F) == 0 || d == total) progress(d, total);
                }
            });

            Logger.Log($"[Portatil] {results.Count} app(s) detectado(s) em {sw.Elapsed.TotalSeconds:N1}s");
            return results.OrderByDescending(r => r.Confidence)
                          .ThenByDescending(r => r.TotalSizeBytes)
                          .ToList();
        }

        private static void CollectClassicCandidates(string root, HashSet<string> installedPaths,
            HashSet<string> scannedFolders, List<Candidate> candidates)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (ShouldSkipFolder(dir, installedPaths)) continue;
                    if (scannedFolders.Add(dir)) candidates.Add(new Candidate(false, 0, dir, null));
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData) && Directory.Exists(localAppData))
                locations.Add(localAppData);

            string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(roamingAppData) && Directory.Exists(roamingAppData))
                locations.Add(roamingAppData);

            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                {
                    string root = drive.RootDirectory.FullName;
                    if (!root.StartsWith(
                        Environment.GetEnvironmentVariable("SystemDrive") ?? "C:",
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
                    using RegistryKey? uninstallKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstallKey != null)
                    {
                        foreach (var sub in uninstallKey.GetSubKeyNames())
                        {
                            try
                            {
                                using RegistryKey? appKey = uninstallKey.OpenSubKey(sub);
                                if (appKey != null)
                                {
                                    string? installPath = appKey.GetValue("InstallLocation") as string;
                                    if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                                        paths.Add(Path.GetFullPath(installPath).TrimEnd('\\'));
                                }
                            }
                            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                        }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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

        private static PortableAppEntry? AnalyzeFolder(string folderPath, HashSet<string> installedPaths, CancellationToken ct = default)
        {
            try
            {
                int exeTopCount = 0, nonInstallerExeCount = 0, dllCount = 0, fileCount = 0;
                long totalBytes = 0, mainExeSize = 0;
                string mainExePath = "";
                bool hasUninsExe = false, hasConfigFiles = false;

                var stack = new Stack<(string Dir, int Depth)>();
                stack.Push((folderPath, 0));

                while (stack.Count > 0)
                {
                    var (dir, depth) = stack.Pop();
                    if (depth > MaxDepth) continue;
                    ct.ThrowIfCancellationRequested();

                    foreach (var (name, size, isDir, _) in EnumerateDirNative(dir))
                    {
                        if (isDir)
                        {
                            string sub = Path.Combine(dir, name);
                            if (ShouldSkipSubtree(sub)) continue;
                            stack.Push((sub, depth + 1));
                            continue;
                        }

                        totalBytes += size;
                        fileCount++;
                        string ext = Path.GetExtension(name);
                        if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) dllCount++;
                        else if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            if (depth == 0) exeTopCount++;
                            if (name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)) hasUninsExe = true;
                            bool isInstaller = _installerExePrefixes.Any(p =>
                                name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                            if (!isInstaller)
                            {
                                nonInstallerExeCount++;
                                if (size > mainExeSize)
                                {
                                    mainExeSize = size;
                                    mainExePath = Path.Combine(dir, name);
                                }
                            }
                        }
                        if (IsConfigFile(name)) hasConfigFiles = true;
                    }
                }

                DateTime lastModified = Directory.Exists(folderPath)
                    ? new DirectoryInfo(folderPath).LastWriteTime
                    : DateTime.MinValue;
                return BuildPortableEntry(folderPath, installedPaths, exeTopCount, nonInstallerExeCount,
                    dllCount, fileCount, totalBytes, mainExePath, hasUninsExe, hasConfigFiles,
                    lastModified);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return null; }
        }

        private static PortableAppEntry? AnalyzeFolderMft(uint dirRec, string folderPath,
            MftIndex index, HashSet<string> installedPaths, CancellationToken ct = default)
        {
            try
            {
                int exeTopCount = 0, nonInstallerExeCount = 0, dllCount = 0, fileCount = 0;
                long totalBytes = 0, mainExeSize = 0;
                string mainExeName = "", mainExeRelPath = "";
                bool hasUninsExe = false, hasConfigFiles = false;

                var stack = new Stack<(uint Rec, int Depth, string RelDir)>();
                stack.Push((dirRec, 0, ""));

                while (stack.Count > 0)
                {
                    var (rec, depth, relDir) = stack.Pop();
                    if (depth > MaxDepth) continue;
                    ct.ThrowIfCancellationRequested();

                    int s = index.Starts[(int)rec + 1];
                    int e = index.Starts[(int)rec + 2];
                    if (e > index.Entries.Length) e = index.Entries.Length;
                    for (int j = s; j < e; j++)
                    {
                        var c = index.Entries[j];
                        if ((c.Flags & (MftFlags.Reparse | MftFlags.HiddenSystem)) != 0) continue;
                        if ((c.Flags & MftFlags.Directory) != 0)
                        {
                            if (_subtreeSkipNames.Contains(c.Name) || c.Name.StartsWith(".")) continue;
                            stack.Push((c.Rec, depth + 1, relDir + c.Name + "\\"));
                            continue;
                        }

                        totalBytes += (long)c.Size;
                        fileCount++;
                        string ext = Path.GetExtension(c.Name);
                        if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) dllCount++;
                        else if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            if (depth == 0) exeTopCount++;
                            if (c.Name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)) hasUninsExe = true;
                            bool isInstaller = _installerExePrefixes.Any(p =>
                                c.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                            if (!isInstaller)
                            {
                                nonInstallerExeCount++;
                                if ((long)c.Size > mainExeSize)
                                {
                                    mainExeSize = (long)c.Size;
                                    mainExeName = c.Name;
                                    mainExeRelPath = relDir;
                                }
                            }
                        }
                        if (IsConfigFile(c.Name)) hasConfigFiles = true;
                    }
                }

                string mainExePath = Path.Combine(folderPath, mainExeRelPath, mainExeName);
                int self = index.RecToIdx[(int)dirRec];
                DateTime lastModified;
                if (self >= 0)
                {
                    lastModified = DateTime.FromFileTimeUtc(
                        (long)index.Entries[self].LastWrite).ToLocalTime();
                }
                else if (Directory.Exists(folderPath))
                {
                    lastModified = new DirectoryInfo(folderPath).LastWriteTime;
                }
                else
                {
                    lastModified = DateTime.MinValue;
                }
                return BuildPortableEntry(folderPath, installedPaths, exeTopCount, nonInstallerExeCount,
                    dllCount, fileCount, totalBytes, mainExePath, hasUninsExe, hasConfigFiles, lastModified);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return null; }
        }

        private static PortableAppEntry? BuildPortableEntry(string folderPath,
            HashSet<string> installedPaths, int exeTopCount, int nonInstallerExeCount,
            int dllCount, int fileCount, long totalBytes, string mainExePath,
            bool hasUninsExe, bool hasConfigFiles, DateTime lastModified)
        {
            if (exeTopCount == 0) return null;
            if (nonInstallerExeCount == 0) return null;
            if (totalBytes < 1_048_576) return null;

            int confidence = 0;
            if (dllCount >= 3) confidence += 30;
            else if (dllCount >= 1) confidence += 15;

            if (nonInstallerExeCount >= 1 && fileCount >= 10) confidence += 20;
            if (totalBytes >= 10_485_760) confidence += 15;
            else if (totalBytes >= 5_242_880) confidence += 10;

            if (hasUninsExe) confidence -= 30;

            if (hasConfigFiles) confidence += 10;

            if (exeTopCount == 1) confidence += 10;

            if (installedPaths.Contains(Path.GetFullPath(folderPath).TrimEnd('\\')))
                return null;

            confidence = Math.Clamp(confidence, 0, 100);
            if (confidence < 30) return null;

            string appName = Path.GetFileNameWithoutExtension(Path.GetFileName(mainExePath));
            if (appName.Length < 2) appName = Path.GetFileName(folderPath);

            return new PortableAppEntry
            {
                Name = appName,
                FolderPath = folderPath,
                MainExecutable = mainExePath,
                TotalSizeBytes = totalBytes,
                LastModified = lastModified,
                Confidence = confidence
            };
        }

        private static bool ShouldSkipSubtree(string dir)
        {
            string name = Path.GetFileName(dir);
            return _subtreeSkipNames.Contains(name) || name.StartsWith('.');
        }

        private static bool IsConfigFile(string name)
        {
            return name.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("settings.ini", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".conf", StringComparison.OrdinalIgnoreCase);
        }
    }
}
