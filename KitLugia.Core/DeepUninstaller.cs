using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualBasic.FileIO;

namespace KitLugia.Core
{
    public enum CleanupSafety
    {
        Safe,
        Moderate,
        Uncertain
    }

    public enum ScannerMode
    {
        Safe,
        Moderate,
        Advanced
    }

    public class ScanEntry
    {
        public string Path { get; set; } = "";
        public CleanupSafety Safety { get; set; } = CleanupSafety.Safe;
    }

    [SupportedOSPlatform("windows")]
    public static class DeepUninstaller
    {
        public class UninstallResult
        {
            public bool UninstallSuccess { get; set; }
            public string UninstallOutput { get; set; } = "";
            public List<string> LeftoverFiles { get; set; } = new();
            public List<string> LeftoverRegistry { get; set; } = new();
            public int FilesDeleted { get; set; }
            public int RegistryDeleted { get; set; }
            public List<string> Errors { get; set; } = new();
            public bool ForceDeleteUsed { get; set; }
            public List<string> BackupFiles { get; set; } = new();
            public List<string> BackupRegistryFiles { get; set; } = new();
            public string? DeletionLogFile { get; set; }
        }
        private static readonly string FileBackupDir = Path.Combine(Path.GetTempPath(), "KitLugia", "FileBackup");
        private static readonly string DeletionLogDir = Path.Combine(Path.GetTempPath(), "KitLugia", "Logs");

        public static void KillProcessesForApp(string displayName, string installLocation = "", List<string>? extraExeNames = null)
        {
            var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var killedIds = new HashSet<int>();

            if (!string.IsNullOrEmpty(displayName))
            {
                targetNames.Add(SanitizeName(displayName));
                string compressed = Regex.Replace(displayName, @"[\s\-_.]+", "");
                if (compressed.Length >= 3) targetNames.Add(compressed);
            }

            if (!string.IsNullOrEmpty(installLocation))
            {
                try
                {
                    string dir = Path.GetFileName(installLocation.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(dir)) targetNames.Add(dir);
                }
                catch { }
            }

            if (extraExeNames != null)
                foreach (var exe in extraExeNames)
                    if (!string.IsNullOrEmpty(exe))
                        targetNames.Add(exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            ? exe[..^4] : exe);

            int selfPid = Environment.ProcessId;

            foreach (var name in targetNames)
            {
                try
                {
                    foreach (var proc in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            if (proc.Id == selfPid || proc.HasExited || !killedIds.Add(proc.Id)) continue;
                            if (ProtectedProcessNames.Contains(proc.ProcessName)) continue;
                            KillProcessGracefully(proc);
                        }
                        catch { }
                        finally { proc.Dispose(); }
                    }
                }
                catch { }
            }
        }

        private static void KillProcessGracefully(Process proc)
        {
            try
            {
                if (proc.HasExited) return;
                proc.CloseMainWindow();
                if (!proc.WaitForExit(3000))
                {
                    proc.Kill();
                    proc.WaitForExit(2000);
                }
            }
            catch { }
        }

        public static async Task<UninstallResult> DeepUninstallProgram(string displayName, string uninstallString, string installLocation, string publisher, string displayIcon, bool createRestorePoint = true, IProgress<string>? progress = null)
        {
            var result = new UninstallResult();

            progress?.Report("Criando ponto de restauração...");
            if (createRestorePoint)
                TryCreateRestorePoint($"KitLugia: Uninstall {displayName}");

            progress?.Report("Encerrando processos do aplicativo...");
            KillProcessesWithTree(displayName, installLocation);

            progress?.Report("Localizando desinstalador...");
            string? actualUninstaller = FindInstalledUninstaller(installLocation, uninstallString);
            string effectiveUninstall = actualUninstaller ?? uninstallString;
            bool foundActualUninstaller = actualUninstaller != null;

            if (!string.IsNullOrEmpty(effectiveUninstall))
            {
                try
                {
                    progress?.Report("Executando desinstalação...");
                    string quietUninstall = GenerateQuietUninstallString(effectiveUninstall, installLocation);
                    var (fileName, args) = ParseCommandLine(quietUninstall);
                    if (string.IsNullOrEmpty(fileName))
                        (fileName, args) = ParseCommandLine(effectiveUninstall);

                    if (!string.IsNullOrEmpty(fileName))
                    {
                        var psi = new ProcessStartInfo(fileName, args)
                        {
                            UseShellExecute = true,
                            Verb = "runas",
                        };
                        using var proc = Process.Start(psi);
                        if (proc != null)
                        {
                            int timeoutMs = foundActualUninstaller ? 15_000 : 3_000;

                            if (!proc.WaitForExit(timeoutMs))
                            {
                                if (!foundActualUninstaller)
                                {
                                    result.Errors.Add("Uninstall string points to main app exe — no silent uninstall available. Force-deleting.");
                                    try { proc.Kill(); } catch { }
                                    proc.WaitForExit(2000);
                                }
                                else
                                {
                                    result.Errors.Add("Uninstaller opened. Complete the uninstall in the window that appeared.");
                                    for (int i = 0; i < 120 && !proc.HasExited; i++)
                                        await Task.Delay(1000);
                                }
                            }
                            if (proc.HasExited)
                            {
                                result.UninstallSuccess = InterpretExitCode(proc.ExitCode, quietUninstall, result);
                            }
                            else if (foundActualUninstaller)
                                result.Errors.Add("Uninstaller still open — close it when done.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Uninstaller: {ex.Message}");
                }
            }
            else
            {
                result.Errors.Add("No uninstall string available");
            }

            // Scan once after uninstall (no "snapshot before" scan — cuts total scan time in half
            // and avoids delaying the user with a redundant pre-uninstall pass)
            progress?.Report("Escaneando resíduos de arquivos...");
            result.LeftoverFiles = ScanLeftoverFiles(displayName, installLocation, displayIcon, uninstallString)
                .OrderBy(f => f).ToList();

            progress?.Report("Escaneando resíduos do registro...");
            result.LeftoverRegistry = ScanLeftoverRegistry(displayName, installLocation)
                .OrderBy(r => r).ToList();

            if (!result.UninstallSuccess && result.LeftoverFiles.Count == 0 && result.LeftoverRegistry.Count == 0)
                result.Errors.Add("Uninstall may have failed — no leftovers detected.");

            return result;
        }

        /// <summary>
        /// Force-delete an app by killing its processes, deleting the install directory,
        /// and removing all detected leftovers (files + registry). No uninstaller needed.
        /// Safety checks prevent deleting system/Windows paths.
        /// </summary>
        public static async Task<UninstallResult> ForceDeleteProgram(string displayName, string installLocation, string publisher = "", string displayIcon = "", bool createRestorePoint = true)
        {
            var result = new UninstallResult();

            if (string.IsNullOrEmpty(displayName))
            {
                result.Errors.Add("Display name is required");
                return result;
            }

            // Guard: refuse to delete system-protected paths
            if (!string.IsNullOrEmpty(installLocation))
            {
                string folderName = Path.GetFileName(installLocation.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(folderName) || SystemFolderNames.Contains(folderName) || IsSystemFolder(installLocation))
                {
                    result.Errors.Add("Refused: install location is a system-protected path");
                    result.Errors.Add(installLocation);
                    return result;
                }

                // Additional guard: the folder name should reasonably match the display name
                // to prevent accidentally force-deleting the wrong app
                int folderConfidence = Confidence.Generate(displayName, folderName);
                if (folderConfidence < 50 && !installLocation.Contains(displayName, StringComparison.OrdinalIgnoreCase))
                {
                    result.Errors.Add($"Refused: folder '{folderName}' does not match '{displayName}' (confidence {folderConfidence})");
                    return result;
                }
            }

            // Create System Restore Point before any destructive operation (Revo-like)
            if (createRestorePoint)
                TryCreateRestorePoint($"KitLugia: Force Delete {displayName}");

            // 1. Kill all processes including child trees via WMI
            KillProcessesWithTree(displayName, installLocation);
            await Task.Delay(500);

            // 2. Add install directory itself
            if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
                result.LeftoverFiles.Add(installLocation);

            // 3. Scan for file leftovers
            var filePaths = ScanLeftoverFiles(displayName, installLocation, displayIcon, "");
            foreach (var f in filePaths)
                if (!result.LeftoverFiles.Contains(f))
                    result.LeftoverFiles.Add(f);

            // 4. Scan for registry leftovers
            result.LeftoverRegistry = ScanLeftoverRegistry(displayName, installLocation);

            // 5. Also remove the app's own Uninstall registry key
            string? uninstallKey = FindUninstallRegistryKey(displayName);
            if (!string.IsNullOrEmpty(uninstallKey))
                result.LeftoverRegistry.Add(uninstallKey);

            // 6. Also try to delete App Paths entry
            if (!string.IsNullOrEmpty(installLocation))
            {
                string appPathKey = $@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{Path.GetFileName(installLocation)}.exe";
                result.LeftoverRegistry.Add(appPathKey);
            }

            // 7. Execute deletion
            PerformCleanup(result.LeftoverFiles, result.LeftoverRegistry, result);

            return result;
        }

        public static (List<ScanEntry> files, List<ScanEntry> registry) ScanLeftovers(string displayName, string publisher, ScannerMode mode = ScannerMode.Moderate)
        {
            string installLocation = GetInstallLocationFromRegistry(displayName) ?? "";
            var rawFiles = ScanLeftoverFiles(displayName, installLocation, "", "", mode);
            var rawReg = ScanLeftoverRegistry(displayName, installLocation, mode);
            var files = rawFiles.Select(f => new ScanEntry { Path = f, Safety = ClassifyFileSafety(displayName, installLocation, f) }).OrderBy(e => e.Path).ToList();
            var reg = rawReg.Select(r => new ScanEntry { Path = r, Safety = ClassifyRegistrySafety(displayName, installLocation, r) }).OrderBy(e => e.Path).ToList();
            return (files, reg);
        }

        public static CleanupSafety ClassifyFileSafety(string displayName, string installLocation, string path)
        {
            if (string.IsNullOrEmpty(path)) return CleanupSafety.Uncertain;

            // Inside install location → Safe
            if (!string.IsNullOrEmpty(installLocation) &&
                path.StartsWith(installLocation, StringComparison.OrdinalIgnoreCase))
                return CleanupSafety.Safe;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            // Local AppData with exact name match → Safe
            if (!string.IsNullOrEmpty(localAppData) &&
                path.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase))
            {
                string relative = path[localAppData.Length..].TrimStart('\\');
                if (relative.StartsWith(displayName, StringComparison.OrdinalIgnoreCase) ||
                    relative.Split('\\').Any(p => p.Equals(displayName, StringComparison.OrdinalIgnoreCase)))
                    return CleanupSafety.Safe;
                return CleanupSafety.Moderate;
            }

            // Roaming AppData with exact name match → Safe / otherwise Moderate
            if (!string.IsNullOrEmpty(roamingAppData) &&
                path.StartsWith(roamingAppData, StringComparison.OrdinalIgnoreCase))
            {
                string relative = path[roamingAppData.Length..].TrimStart('\\');
                if (relative.StartsWith(displayName, StringComparison.OrdinalIgnoreCase) ||
                    relative.Split('\\').Any(p => p.Equals(displayName, StringComparison.OrdinalIgnoreCase)))
                    return CleanupSafety.Safe;
                return CleanupSafety.Moderate;
            }

            // ProgramData → Uncertain (shared data)
            if (!string.IsNullOrEmpty(programData) &&
                path.StartsWith(programData, StringComparison.OrdinalIgnoreCase))
                return CleanupSafety.Uncertain;

            // Path contains display name → Moderate
            if (path.IndexOf(displayName, StringComparison.OrdinalIgnoreCase) >= 0)
                return CleanupSafety.Moderate;

            // Unknown → Uncertain
            return CleanupSafety.Uncertain;
        }

        public static CleanupSafety ClassifyRegistrySafety(string displayName, string installLocation, string path)
        {
            if (string.IsNullOrEmpty(path)) return CleanupSafety.Uncertain;

            // Uninstall key → Safe
            if (path.Contains("\\Uninstall\\", StringComparison.OrdinalIgnoreCase) &&
                path.IndexOf(displayName, StringComparison.OrdinalIgnoreCase) >= 0)
                return CleanupSafety.Safe;

            // HKCU\Software\AppName → Safe
            if (path.StartsWith("HKEY_CURRENT_USER\\SOFTWARE", StringComparison.OrdinalIgnoreCase))
            {
                string rest = path["HKEY_CURRENT_USER\\SOFTWARE".Length..].TrimStart('\\');
                if (rest.Equals(displayName, StringComparison.OrdinalIgnoreCase) ||
                    rest.StartsWith(displayName + "\\", StringComparison.OrdinalIgnoreCase))
                    return CleanupSafety.Safe;
                string firstPart = rest.Split('\\')[0];
                int conf = Confidence.Generate(displayName, firstPart);
                if (conf >= 85) return CleanupSafety.Moderate;
                return CleanupSafety.Uncertain;
            }

            // Classes\AppName → Moderate
            if (path.IndexOf("\\Classes\\", StringComparison.OrdinalIgnoreCase) >= 0 &&
                path.IndexOf(displayName, StringComparison.OrdinalIgnoreCase) >= 0)
                return CleanupSafety.Moderate;

            // Installer components referencing installLocation → Safe
            if (!string.IsNullOrEmpty(installLocation) &&
                path.Contains("\\Installer\\", StringComparison.OrdinalIgnoreCase))
                return CleanupSafety.Safe;

            // Name appears in path → Moderate
            if (path.IndexOf(displayName, StringComparison.OrdinalIgnoreCase) >= 0)
                return CleanupSafety.Moderate;

            return CleanupSafety.Uncertain;
        }

        // Processes that must NEVER be killed (browsers, shell, etc.)
        private static readonly HashSet<string> ProtectedProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "chrome", "firefox", "msedge", "brave", "opera",
            "iexplore", "vivaldi", "tor", "waterfox", "palemoon",
            "safari", "seamonkey", "k-meleon", "maxthon", "avant",
            "iridium", "epic", "comodo_dragon", "centbrowser",
            "superbird", "naver_whale", "yandex", "coccoc",
            "slimjet", "slimbrowser", "rocket", "valve_steam",
            "epicgameslauncher", "goggalaxy", "origin", "battle",
            "discord", "slack", "teams", "zoom", "outlook",
            "winword", "excel", "powerpnt", "onenote", "outlook",
        };

        // KitLugia's own install path — skip these files/folders during deletion
        private static readonly string KitLugiaInstallPath = GetKitLugiaPath();

        private static string GetKitLugiaPath()
        {
            try
            {
                string? loc = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(loc))
                    return Path.GetDirectoryName(loc)?.TrimEnd('\\') ?? "";
            }
            catch { }
            return "";
        }

        // All known system / well-known folder paths (CSIDL + WinRT KnownFolders equivalent)
        // Built once at startup to prevent deleting protected OS locations.
        private static readonly HashSet<string> ProhibitedLocations = BuildProhibitedLocations();

        private static HashSet<string> BuildProhibitedLocations()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    void AddFolder(Environment.SpecialFolder sf)
                    {
                        try
                        {
                            string p = Environment.GetFolderPath(sf);
                            if (!string.IsNullOrEmpty(p))
                                set.Add(p.TrimEnd('\\'));
                        }
                        catch { }
                    }

            // CSIDL / SpecialFolders
            AddFolder(Environment.SpecialFolder.System);
            AddFolder(Environment.SpecialFolder.SystemX86);
            AddFolder(Environment.SpecialFolder.Windows);
            AddFolder(Environment.SpecialFolder.ProgramFiles);
            AddFolder(Environment.SpecialFolder.ProgramFilesX86);
            AddFolder(Environment.SpecialFolder.CommonProgramFiles);
            AddFolder(Environment.SpecialFolder.CommonProgramFilesX86);
            AddFolder(Environment.SpecialFolder.CommonApplicationData);
            AddFolder(Environment.SpecialFolder.ApplicationData);
            AddFolder(Environment.SpecialFolder.LocalApplicationData);
            AddFolder(Environment.SpecialFolder.Desktop);
            AddFolder(Environment.SpecialFolder.DesktopDirectory);
            AddFolder(Environment.SpecialFolder.CommonDesktopDirectory);
            AddFolder(Environment.SpecialFolder.MyDocuments);
            AddFolder(Environment.SpecialFolder.CommonDocuments);
            AddFolder(Environment.SpecialFolder.MyMusic);
            AddFolder(Environment.SpecialFolder.CommonMusic);
            AddFolder(Environment.SpecialFolder.MyPictures);
            AddFolder(Environment.SpecialFolder.CommonPictures);
            AddFolder(Environment.SpecialFolder.MyVideos);
            AddFolder(Environment.SpecialFolder.CommonVideos);
            AddFolder(Environment.SpecialFolder.Recent);
            AddFolder(Environment.SpecialFolder.SendTo);
            AddFolder(Environment.SpecialFolder.StartMenu);
            AddFolder(Environment.SpecialFolder.CommonStartMenu);
            AddFolder(Environment.SpecialFolder.Startup);
            AddFolder(Environment.SpecialFolder.CommonStartup);
            AddFolder(Environment.SpecialFolder.Favorites);
            AddFolder(Environment.SpecialFolder.CommonTemplates);
            AddFolder(Environment.SpecialFolder.Fonts);
            AddFolder(Environment.SpecialFolder.InternetCache);
            AddFolder(Environment.SpecialFolder.Cookies);
            AddFolder(Environment.SpecialFolder.History);
            AddFolder(Environment.SpecialFolder.Personal);
            AddFolder(Environment.SpecialFolder.CommonAdminTools);
            AddFolder(Environment.SpecialFolder.AdminTools);
            AddFolder(Environment.SpecialFolder.CDBurning);
            AddFolder(Environment.SpecialFolder.CommonOemLinks);
            AddFolder(Environment.SpecialFolder.CommonPrograms);
            AddFolder(Environment.SpecialFolder.Programs);
            AddFolder(Environment.SpecialFolder.Resources);
            AddFolder(Environment.SpecialFolder.UserProfile);

            // WinRT KnownFolders equivalent paths (built from known CSIDL + env)
            try
            {
                string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

                // Add paths equivalent to WinRT KnownFolders:
                set.Add(user.TrimEnd('\\'));
                set.Add(Path.Combine(user, "Downloads"));
                set.Add(Path.Combine(user, "Documents"));
                set.Add(Path.Combine(user, "Pictures"));
                set.Add(Path.Combine(user, "Music"));
                set.Add(Path.Combine(user, "Videos"));
                set.Add(Path.Combine(user, "Desktop"));
                set.Add(Path.Combine(user, "Favorites"));
                set.Add(Path.Combine(user, "Contacts"));
                set.Add(Path.Combine(user, "Links"));
                set.Add(Path.Combine(user, "Searches"));
                set.Add(Path.Combine(user, "Saved Games"));
                set.Add(Path.Combine(user, "OneDrive"));
                set.Add(Path.Combine(user, "3D Objects"));
                set.Add(Path.Combine(roaming, "Microsoft", "Windows", "Start Menu"));
                set.Add(Path.Combine(programData, "Microsoft", "Windows", "Start Menu"));
                set.Add(Path.Combine(localAppData, "Microsoft", "Windows", "INetCache"));
                set.Add(Path.Combine(localAppData, "Microsoft", "Windows", "Temporary Internet Files"));

                // Also add common well-known Windows paths with their canonical names
                string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string? winDir = Directory.GetParent(sysDir)?.FullName;
                if (winDir != null && !set.Contains(winDir.TrimEnd('\\')))
                    set.Add(winDir.TrimEnd('\\'));
            }
            catch { }

            return set;
        }

        private static bool IsProhibitedLocation(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return true;
            string normalized = fullPath.TrimEnd('\\');
            // Only block exact matches of known system folders,
            // not sub-items within them (those are app artifacts).
            return ProhibitedLocations.Contains(normalized);
        }

        private static bool IsKitLugiaSelfPath(string fullPath)
        {
            if (string.IsNullOrEmpty(KitLugiaInstallPath) || string.IsNullOrEmpty(fullPath)) return false;
            string normalized = fullPath.TrimEnd('\\');
            return normalized.Equals(KitLugiaInstallPath, StringComparison.OrdinalIgnoreCase) ||
                   normalized.StartsWith(KitLugiaInstallPath + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static readonly HashSet<string> SystemFolderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft", "Windows", "WinSxS", "System32", "SysWOW64",
            "Common Files", "MSBuild", "Reference Assemblies",
            "WindowsApps", "Windows NT", "WindowsPowerShell",
            "dotnet", "Assembly", "PackageManagement",
            "Temporary Internet Files", "Temp", "Templates",
            "Start Menu", "Desktop", "Favorites", "Fonts",
            "Installer", "Microsoft.NET", "Microsoft Shared",
            "ModifiableWindowsApps", "Resources", "servicing",
            "VSS", "Help", "inf", "L2Schemas", "Logs",
            "Media", "ModemLogs", "en-US", "Branding",
            "Cursors", "Debug", "ImmersiveControlPanel",
            "Registration", "rescache", "SchCache",
            "security", "ServicePackFiles", "Skin",
            "SoftwareDistribution", "Speech", "systemprofile",
            "ConfigMsi", "Msi", "mui", "OCR", "ras",
            "twain_32", "Web", "winsxs", "systemprofile",
            "IME", "InputMethod",
            "DirectX", "VulkanRT",
            "CRT", "MFC", "ATL",
        };

        public static List<string> FindProgramFilesOrphans()
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var knownLocations = GetAllInstallLocations();

            string[] pfDirs =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            };

            foreach (var pf in pfDirs)
            {
                if (string.IsNullOrEmpty(pf) || !Directory.Exists(pf)) continue;
                try
                {
                    foreach (var dir in Directory.GetDirectories(pf, "*", System.IO.SearchOption.TopDirectoryOnly))
                    {
                        string name = Path.GetFileName(dir);
                        if (string.IsNullOrEmpty(name)) continue;
                        if (name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)) continue;
                        if (SystemFolderNames.Contains(name)) continue;

                        bool known = knownLocations.Any(k =>
                            dir.StartsWith(k, StringComparison.OrdinalIgnoreCase));
                        if (!known)
                            results.Add(dir);
                    }
                }
                catch { }
            }
            return results.OrderBy(f => f).ToList();
        }

        // ── Scanning ─────────────────────────────────────────────

        // Safety guard: minimum name length to prevent Sift4 false positives on short/generic names
        private static readonly HashSet<string> ForbiddenScanFolderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "microsoft", "windows", "common files", "packages", "windowsapps",
            "temp", "programs", "program files", "program files (x86)",
            "appdata", "local", "local low", "roaming", "system32",
            "syswow64", "assembly", "globalization", "fonts", "inf",
            "installer", "help", "resources", "security", "servicing",
            "speech", "system", "tasks", "winsxs", "migration", "modem",
            "ras", "registration", "addins", "appcompat", "apppatch",
            "boot", "bthserv", "catsroot", "com", "cursors", "debug",
            "dell", "diagnostics", "directx", "drivers", "en-us",
            "es-es", "fr-fr", "de-de", "it-it", "pt-br", "ja-jp",
            "ko-kr", "zh-cn", "zh-tw", "ru-ru", "pl-pl", "nl-nl",
            "sv-se", "da-dk", "fi-fi", "nb-no", "tr-tr", "cs-cz",
            "hu-hu", "el-gr", "ro-ro", "sk-sk", "hr-hr", "sl-si",
            "lt-lt", "lv-lv", "et-ee", "bg-bg", "sr-latn-rs", "uk-ua",
            "he-il", "ar-sa", "th-th", "vi-vn", "ms-my", "id-id"
        };

        private static List<string> ScanLeftoverFiles(string displayName, string installLocation, string displayIcon, string uninstallString, ScannerMode mode = ScannerMode.Moderate)
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Safety guard: refuse to scan with very short/generic names (protect against Sift4 false positives)
            if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length < 3)
                return results.ToList();

            var otherInstallLocations = mode == ScannerMode.Advanced ? null : GetAllInstallLocations(excludeName: displayName);

            // InstallLocation
            if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
                results.Add(installLocation);

            // Safe mode: only install dir + exact AppData name matches
            if (mode == ScannerMode.Safe)
            {
                string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                foreach (var ad in new[] { roaming, local })
                {
                    if (!string.IsNullOrEmpty(ad) && Directory.Exists(ad))
                    {
                        string exact = Path.Combine(ad, displayName);
                        if (Directory.Exists(exact)) results.Add(exact);
                    }
                }
                return results.ToList();
            }

            // Scan well-known directories with confidence matching
            int maxDepth = mode == ScannerMode.Advanced ? 5 : 3;
            var dirs = BuildFileSearchDirs();
            foreach (var dir in dirs)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                ScanFolderConfidence(dir, displayName, results, otherInstallLocations, depth: 0, maxDepth: maxDepth);
            }

            // Temp — shallow
            string temp = Path.GetTempPath().TrimEnd('\\');
            if (!string.IsNullOrEmpty(temp) && Directory.Exists(temp))
                ScanFolderConfidence(temp, displayName, results, otherInstallLocations, depth: 0, maxDepth: 1);

            // Prefetch — shallow
            string sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.System).TrimEnd('\\');
            sysRoot = Directory.GetParent(sysRoot)?.FullName ?? sysRoot;
            string prefetch = Path.Combine(sysRoot, "Prefetch");
            if (Directory.Exists(prefetch))
                ScanFolderConfidence(prefetch, displayName, results, otherInstallLocations, depth: 0, maxDepth: 1);

            // Uninstaller-specific leftovers
            ScanUninstallerSpecific(installLocation, displayIcon, displayName, results);

            // Startup folder entries
            ScanStartupFolders(displayName, results);

            // Start Menu shortcuts (.lnk files)
            ScanStartMenuShortcuts(displayName, results);
            ScanDesktopShortcuts(displayName, results);

            // Empty dir + questionable name detection (BCUninstaller pattern)
            if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
                ScanEmptyAndQuestionableDirs(installLocation, displayName, results);
            foreach (var dir in BuildFileSearchDirs())
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    ScanEmptyAndQuestionableDirs(dir, displayName, results);
            }

            return results.ToList();
        }

        private static readonly HashSet<string> GenericPublishers = new(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft", "Google", "Adobe", "Oracle", "Intel", "AMD", "NVIDIA",
            "Apple", "Amazon", "Meta", "IBM", "SAP", "HP", "Dell", "Lenovo",
            "Samsung", "LG", "Sony", "Canon", "Epson", "Brother", "Logitech",
            "VMware", "Citrix", "Autodesk", "Cisco",
            "Qualcomm", "Realtek", "MediaTek", "Broadcom", "ASUS", "Acer",
            "Huawei", "Xiaomi", "Panasonic", "Nikon", "Fujitsu", "Toshiba",
            "Mozilla", "Spotify", "Discord", "Slack", "Zoom", "TeamViewer",
            "AnyDesk", "Steam", "EpicGames", "GOG", "Ubisoft", "Electronic Arts",
            "Blizzard", "Riot Games", "Valve", "Razer", "Corsair",
            "Docker", "GitHub", "Git", "Python", "Java", "Node.js",
            "MongoDB", "MySQL", "PostgreSQL", "Redis", "Elastic",
            "JetBrains", "Eclipse", "Apache", "Red Hat", "Canonical",
            "NuGet", "Chocolatey", "Scoop", "WinRAR", "7-Zip",
            "Nuance", "Dashlane", "LastPass", "Bitdefender", "Norton",
            "McAfee", "Avast", "AVG", "Kaspersky", "Malwarebytes",
            "CCleaner", "Defraggler", "Recuva", "Speccy",
            "Notepad++", "VLC", "GIMP", "Inkscape", "Audacity",
            "OBS Studio", "HandBrake", "FFmpeg", "ImgBurn",
            "FileZilla", "WinSCP", "PuTTY", "OpenVPN",
            "Cygwin", "MinGW", "MSYS2", "WSL",
        };

        private static bool IsTooBroadForDeletion(string fullPath)
        {
            try
            {
                string normalized = fullPath.TrimEnd('\\');
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(userProfile)) return false;

                if (!normalized.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
                    return false;

                string relative = normalized[userProfile.Length..].TrimStart('\\');
                if (string.IsNullOrEmpty(relative))
                    return true; // exact user profile

                string[] parts = relative.Split('\\');

                // Depth 1 = direct child of user profile (Desktop, AppData, etc.) → too broad
                if (parts.Length <= 1)
                    return true;

                // Depth 2 under AppData (AppData\Roaming, AppData\Local, AppData\LocalLow) → too broad
                if (parts.Length == 2 && parts[0].Equals("AppData", StringComparison.OrdinalIgnoreCase))
                    return true;

                // Depth 2 under known shell roots (Desktop\file.lnk is depth 2 = OK — individual files)
                // Only block depth 1 for these
            }
            catch { }
            return false;
        }

        private static bool IsRegPathTooBroad(string fullRegPath, string[] parts, string keyPath)
        {
            // Block exact matches of known-dangerous registry roots
            if (keyPath.Equals("SOFTWARE\\Microsoft", StringComparison.OrdinalIgnoreCase) ||
                keyPath.Equals("SOFTWARE\\Microsoft\\Windows", StringComparison.OrdinalIgnoreCase) ||
                keyPath.Equals("SOFTWARE\\WOW6432Node\\Microsoft", StringComparison.OrdinalIgnoreCase) ||
                keyPath.Equals("SOFTWARE\\Classes", StringComparison.OrdinalIgnoreCase) ||
                keyPath.Equals("SOFTWARE\\WOW6432Node", StringComparison.OrdinalIgnoreCase) ||
                keyPath.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) ||
                keyPath.Equals("SYSTEM\\CurrentControlSet", StringComparison.OrdinalIgnoreCase))
                return true;

            // Block paths that are too shallow for their hive type
            int keyDepth = keyPath.Split('\\').Length;
            string hiveName = parts[0];
            bool isSystemHive = hiveName.IndexOf("LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                hiveName.IndexOf("USERS", StringComparison.OrdinalIgnoreCase) >= 0;

            // SYSTEM hive: min 3 levels (SYSTEM\CurrentControlSet\Services) — 2 is too broad
            if (isSystemHive && keyPath.StartsWith("SYSTEM\\", StringComparison.OrdinalIgnoreCase) && keyDepth < 3)
                return true;

            // SOFTWARE hive: min 2 levels (SOFTWARE\AppName) — 1 is just "SOFTWARE" which is too broad
            if (keyPath.StartsWith("SOFTWARE\\", StringComparison.OrdinalIgnoreCase) && keyDepth < 2)
                return true;

            return false;
        }

        private static bool IsSystemFolder(string fullPath)
        {
            string sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.System);
            sysRoot = Directory.GetParent(sysRoot)?.FullName ?? sysRoot;
            if (fullPath.StartsWith(sysRoot, StringComparison.OrdinalIgnoreCase))
                return true;

            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            // Skip well-known system subfolders of ProgramData
            if (fullPath.StartsWith(programData, StringComparison.OrdinalIgnoreCase))
            {
                string relative = fullPath[programData.Length..].TrimStart('\\');
                var parts = relative.Split('\\');
                if (parts.Length > 0 && (parts[0].Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                                          parts[0].Equals("Windows", StringComparison.OrdinalIgnoreCase) ||
                                          parts[0].Equals("Package Cache", StringComparison.OrdinalIgnoreCase) ||
                                          parts[0].Equals("USOShared", StringComparison.OrdinalIgnoreCase) ||
                                          parts[0].Equals("USOPrivate", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }

            return false;
        }

        private static string[] BuildFileSearchDirs()
        {
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string localLow = Path.Combine(user, "AppData", "LocalLow");
            string savedGames = Path.Combine(user, "Saved Games");
            string userStartMenu = Path.Combine(roamingAppData, "Microsoft", "Windows", "Start Menu", "Programs");
            string commonStartMenu = Path.Combine(programData, "Microsoft", "Windows", "Start Menu", "Programs");
            string publicDesktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
            string virtualStore = Path.Combine(localAppData, "VirtualStore");
            string userPrograms = Path.Combine(roamingAppData, "Programs");
            string localPrograms = Path.Combine(localAppData, "Programs");
            string publicPrograms = Path.Combine(programData, "Programs");
            string werArchive = Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive");
            string werQueue = Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue");
            string werLocalArchive = Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportArchive");
            string werLocalQueue = Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportQueue");

            return new[]
            {
                localAppData, roamingAppData, programData, programFiles, programFilesX86,
                localLow, desktop, documents, savedGames, userStartMenu, commonStartMenu, publicDesktop,
                virtualStore, userPrograms, localPrograms, publicPrograms,
                werArchive, werQueue, werLocalArchive, werLocalQueue
            };
        }

        private static void ScanFolderConfidence(string baseDir, string displayName, HashSet<string> results, List<string> otherInstallLocations, int depth, int maxDepth)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(baseDir, "*", System.IO.SearchOption.TopDirectoryOnly))
                {
                    if (IsSystemFolder(dir)) continue;

                    string dirName = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(dirName) || SystemFolderNames.Contains(dirName)) continue;

                    // Skip forbidden generic directory names that could cause false positives
                    if (ForbiddenScanFolderNames.Contains(dirName))
                        continue;

                    // Skip paths that are too shallow/broad (depth protection)
                    if (IsTooBroadForDeletion(dir))
                        continue;

                    bool match = Confidence.Generate(displayName, dirName) >= 70;

                    if (match)
                    {
                        // Cross-reference: skip if another app uses this dir
                        if (otherInstallLocations.Any(loc => dir.StartsWith(loc, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        // Also skip dirs that are known Windows/System dirs
                        if (IsSystemFolder(dir))
                            continue;

                        results.Add(dir);
                    }

                    if (match && depth < maxDepth)
                        ScanFolderConfidence(dir, displayName, results, otherInstallLocations, depth + 1, maxDepth);
                }
            }
            catch { }
        }

        private static void ScanUninstallerSpecific(string installLocation, string displayIcon, string displayName, HashSet<string> results)
        {
            if (string.IsNullOrEmpty(installLocation) || !Directory.Exists(installLocation)) return;

            try
            {
                // Inno Setup: unins000.exe/.dat, unins001.exe/.dat...
                foreach (var f in Directory.GetFiles(installLocation, "unins0*.exe"))
                    results.Add(f);
                foreach (var f in Directory.GetFiles(installLocation, "unins0*.dat"))
                    results.Add(f);

                // NSIS: uninstall.exe
                string nsisPath = Path.Combine(installLocation, "uninstall.exe");
                if (File.Exists(nsisPath)) results.Add(nsisPath);

                // InstallShield: setup.exe, isuninst.exe, _ISREG32.DLL
                string isuninst = Path.Combine(installLocation, "isuninst.exe");
                if (File.Exists(isuninst)) results.Add(isuninst);
                string isreg = Path.Combine(installLocation, "_ISREG32.DLL");
                if (File.Exists(isreg)) results.Add(isreg);

                // DisplayIcon points to uninstaller
                if (!string.IsNullOrEmpty(displayIcon))
                {
                    string iconDir = Path.GetDirectoryName(displayIcon) ?? "";
                    if (!string.IsNullOrEmpty(iconDir) && Directory.Exists(iconDir) &&
                        !iconDir.StartsWith(installLocation, StringComparison.OrdinalIgnoreCase))
                    {
                        string iconName = Path.GetFileNameWithoutExtension(displayIcon);
                        if (!string.IsNullOrEmpty(iconName) && Confidence.Generate(displayName, iconName) >= 70)
                            results.Add(iconDir);
                    }
                }
            }
            catch { }
        }

        private static void ScanStartupFolders(string displayName, HashSet<string> results)
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string[] startupDirs =
            {
                Path.Combine(roaming, "Microsoft", "Windows", "Start Menu", "Programs", "Startup"),
                Path.Combine(common, "Microsoft", "Windows", "Start Menu", "Programs", "Startup"),
            };

            foreach (var sd in startupDirs)
            {
                if (!Directory.Exists(sd)) continue;
                try
                {
                    foreach (var f in Directory.GetFiles(sd))
                    {
                        string name = Path.GetFileNameWithoutExtension(f);
                        if (string.IsNullOrEmpty(name) || name.Length < 3) continue;
                        if (Confidence.Generate(displayName, name) >= 70) results.Add(f);
                    }
                }
                catch { }
            }
        }

        private static void ScanStartMenuShortcuts(string displayName, HashSet<string> results)
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string[] dirs =
            {
                Path.Combine(roaming, "Microsoft", "Windows", "Start Menu", "Programs"),
                Path.Combine(common, "Microsoft", "Windows", "Start Menu", "Programs"),
            };

            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (var f in Directory.GetFiles(dir, "*.lnk", System.IO.SearchOption.AllDirectories))
                    {
                        string name = Path.GetFileNameWithoutExtension(f);
                        if (string.IsNullOrEmpty(name) || name.Length < 3) continue;
                        if (Confidence.Generate(displayName, name) >= 70)
                            results.Add(f);
                    }
                }
                catch { }
            }
        }

        private static void ScanDesktopShortcuts(string displayName, HashSet<string> results)
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

            foreach (var dir in new[] { desktop, commonDesktop })
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                try
                {
                    foreach (var f in Directory.GetFiles(dir, "*.lnk"))
                    {
                        string name = Path.GetFileNameWithoutExtension(f);
                        if (string.IsNullOrEmpty(name) || name.Length < 3) continue;
                        if (Confidence.Generate(displayName, name) >= 70)
                            results.Add(f);
                    }
                }
                catch { }
            }
        }

        // ── Registry Scanning ──────────────────────────────────────

        private static readonly bool Is64Bit = Environment.Is64BitOperatingSystem;

        private static List<string> ScanLeftoverRegistry(string displayName, string installLocation = "", ScannerMode mode = ScannerMode.Moderate)
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Uninstall keys (always scanned)
            ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", displayName, results, installLocation);
            ScanHiveForNames(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", displayName, results, installLocation);
            if (Is64Bit)
                ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", displayName, results, installLocation);

            // Safe mode: only Uninstall keys + exact HKCU\Software\AppName — no recursive scan
            if (mode == ScannerMode.Safe)
            {
                ScanHiveForNames(@"HKEY_CURRENT_USER\SOFTWARE", displayName, results, installLocation);
                return results.ToList();
            }

            // Advanced mode: no publisher exclusions
            string[] commonPublishers;
            if (mode == ScannerMode.Advanced)
            {
                commonPublishers = [];
            }
            else
            {
                commonPublishers = GenericPublishers.Concat(["Wow6432Node", "Classes", "Clients", "RegisteredApplications", "VirtualBox", "Battle.net"]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            ScanSoftwareRecursive(@"HKEY_LOCAL_MACHINE\SOFTWARE", displayName, results, commonPublishers, 0, installLocation);
            ScanSoftwareRecursive(@"HKEY_CURRENT_USER\SOFTWARE", displayName, results, ["Microsoft", "Classes", "Wow6432Node", ..commonPublishers], 0, installLocation);
            if (Is64Bit)
                ScanSoftwareRecursive(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node", displayName, results, ["Microsoft", "Windows", ..commonPublishers], 0, installLocation);

            // Classes hives — first pass by name, second pass by value (catches GUID leftovers)
            string[] classPaths =
            {
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\AppID",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\AppUserModelId",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Interface",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\TypeLib",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Directory\shell",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\*\shell",
                @"HKEY_CURRENT_USER\SOFTWARE\Classes\CLSID",
                @"HKEY_CURRENT_USER\SOFTWARE\Classes\AppID",
                @"HKEY_CURRENT_USER\SOFTWARE\Classes\Interface",
                @"HKEY_CURRENT_USER\SOFTWARE\Classes\TypeLib",
            };
            if (Is64Bit)
            {
                classPaths =
                [
                    ..classPaths,
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\WOW6432Node\CLSID",
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\WOW6432Node\AppID",
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\WOW6432Node\Interface",
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\WOW6432Node\TypeLib",
                    @"HKEY_CURRENT_USER\SOFTWARE\Classes\WOW6432Node\CLSID",
                    @"HKEY_CURRENT_USER\SOFTWARE\Classes\WOW6432Node\AppID",
                ];
            }
            foreach (var cp in classPaths)
                ScanHiveForNames(cp, displayName, results, installLocation);

            // Expanded COM scanning: ProgID, ShellEx, PersistentHandler, OpenWithProgIDs
            ScanComHives(displayName, installLocation, results);

            // Second pass on GUID-heavy hives: scan by value content
            string[] guidHives =
            [
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\AppID",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Interface",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\TypeLib",
            ];
            if (Is64Bit)
            {
                guidHives =
                [
                    ..guidHives,
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\WOW6432Node\CLSID",
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\WOW6432Node\AppID",
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\WOW6432Node\Interface",
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\WOW6432Node\TypeLib",
                ];
            }
            foreach (var gh in guidHives)
                ScanHiveByValues(gh, installLocation, displayName, results);

            // App Paths & Run
            ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", displayName, results, installLocation);
            ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", displayName, results, installLocation);
            ScanHiveForNames(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", displayName, results, installLocation);

            // Shell extensions
            ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers", displayName, results, installLocation);
            ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved", displayName, results, installLocation);

            // MSI Installer
            string[] msiPaths =
            {
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Installer\Products",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Installer\Features",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Installer\Patches",
            };
            ScanMsiUserData(displayName, results, installLocation);
            foreach (var mp in msiPaths)
                ScanHiveForNames(mp, displayName, results, installLocation);

            // SharedDLLs — reference counts for shared components
            ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs", displayName, results, installLocation);

            // Installer\Folders — value names are literal install paths
            ScanInstallerFolders(installLocation, results);

            // Installer\Components — GUID keys whose values point to installed files
            ScanInstallerComponentsByValues(installLocation, displayName, results);

            // AppCompat
            string[] compatPaths =
            {
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store",
                @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
                @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store",
            };
            foreach (var cp in compatPaths)
                ScanHiveForNames(cp, displayName, results, installLocation);

            // RegisteredApplications
            ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SOFTWARE\RegisteredApplications", displayName, results, installLocation);
            ScanHiveForNames(@"HKEY_CURRENT_USER\SOFTWARE\RegisteredApplications", displayName, results, installLocation);

            // VirtualStore
            string[] vsPaths =
            {
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\VirtualStore\MACHINE\SOFTWARE",
                @"HKEY_CURRENT_USER\SOFTWARE\Classes\VirtualStore\MACHINE\SOFTWARE",
            };
            if (Is64Bit)
            {
                vsPaths =
                [
                    ..vsPaths,
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\VirtualStore\MACHINE\SOFTWARE\WOW6432Node",
                    @"HKEY_CURRENT_USER\SOFTWARE\Classes\VirtualStore\MACHINE\SOFTWARE\WOW6432Node",
                ];
            }
            foreach (var vp in vsPaths)
                ScanSoftwareRecursive(vp, displayName, results, installLocation: installLocation);

            // Services / Firewall / EventLog / Tracing / UserAssist / Heap / Audio
            ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services", displayName, results, installLocation);
            ScanFirewallRules(displayName, results);
            ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\EventLog\Application", displayName, results, installLocation);
            ScanTracing(displayName, results);
            ScanUserAssist(displayName, results);
            ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\RADAR\HeapLeakDetection\DiagnosedApplications", displayName, results, installLocation);
            ScanHiveForNames(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore", displayName, results, installLocation);

            // Cross-hive registry linking: for each found key, check equivalent hives
            var linkedResults = new List<string>();
            foreach (var r in results)
                linkedResults.AddRange(GetRelatedKeys(r));
            foreach (var lr in linkedResults)
                results.Add(lr);

            return results.ToList();
        }

        private static void ScanHiveForNames(string hiveKey, string displayName, HashSet<string> results, string installLocation = "")
        {
            try
            {
                var hive = ResolveHive(hiveKey, out string subKey);
                if (hive == null || string.IsNullOrEmpty(subKey)) return;
                using var key = hive.OpenSubKey(subKey, false);
                if (key == null) return;

                foreach (var name in key.GetSubKeyNames())
                {
                    if (string.IsNullOrEmpty(name) || name.Length < 2) continue;
                    if (name.StartsWith('.') || name.StartsWith("_")) continue;
                    if (SystemFolderNames.Contains(name)) continue;

                    bool nameMatch = Confidence.Generate(displayName, name) >= 70;
                    bool valueMatch = false;
                    if (!nameMatch && !string.IsNullOrEmpty(installLocation))
                    {
                        using var sk = key.OpenSubKey(name, false);
                        if (sk != null)
                            valueMatch = KeyHasValueReferencing(sk, installLocation, displayName);
                    }

                    if (nameMatch || valueMatch)
                        results.Add($@"{hiveKey}\{name}");
                }
            }
            catch { }
        }

        private static void ScanSoftwareRecursive(string hiveKey, string displayName, HashSet<string> results, string[]? exclusions = null, int depth = 0, string installLocation = "")
        {
            if (depth > 2) return;
            try
            {
                var hive = ResolveHive(hiveKey, out string subKey);
                if (hive == null || string.IsNullOrEmpty(subKey)) return;
                using var key = hive.OpenSubKey(subKey, false);
                if (key == null) return;

                foreach (var name in key.GetSubKeyNames())
                {
                    if (string.IsNullOrEmpty(name) || name.Length < 2) continue;
                    if (name.StartsWith('.') || name.StartsWith("_")) continue;
                    if (exclusions != null && exclusions.Any(e => name.Equals(e, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (SystemFolderNames.Contains(name)) continue;

                    string full = $@"{hiveKey}\{name}";
                    bool nameMatch = Confidence.Generate(displayName, name) >= 70;
                    bool valueMatch = false;
                    if (!nameMatch && !string.IsNullOrEmpty(installLocation))
                    {
                        using var sk = key.OpenSubKey(name, false);
                        if (sk != null)
                            valueMatch = KeyHasValueReferencing(sk, installLocation, displayName);
                    }

                    if (nameMatch || valueMatch)
                        results.Add(full);

                    ScanSoftwareRecursive(full, displayName, results, exclusions, depth + 1, installLocation);
                }
            }
            catch { }
        }

        private static void ScanMsiUserData(string displayName, HashSet<string> results, string installLocation = "")
        {
            try
            {
                using var userData = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData", false);
                if (userData == null) return;
                foreach (var sid in userData.GetSubKeyNames())
                {
                    foreach (var sub in new[] { "Products", "Patches", "Components" })
                        ScanHiveForNames($@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\{sid}\{sub}", displayName, results, installLocation);
                }
            }
            catch { }
        }

        private static void ScanFirewallRules(string displayName, HashSet<string> results)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules", false);
                if (key == null) return;
                foreach (var valName in key.GetValueNames())
                {
                    var val = key.GetValue(valName) as string;
                    if (string.IsNullOrEmpty(val)) continue;
                    int idx = val.IndexOf("|App=", StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) continue;
                    idx += 5;
                    int end = val.IndexOf('|', idx);
                    string path = end > idx ? val[idx..end] : val[idx..];
                    string file = Path.GetFileNameWithoutExtension(path);
                    if (string.IsNullOrEmpty(file) || file.Length < 3) continue;
                    if (Confidence.Generate(displayName, file) >= 70)
                        results.Add($@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules\{valName}");
                }
            }
            catch { }
        }

        private static void ScanTracing(string displayName, HashSet<string> results)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Tracing", false);
                if (key == null) return;
                foreach (var name in key.GetSubKeyNames())
                {
                    int idx = name.LastIndexOf('_');
                    if (idx > 0)
                    {
                        string stem = name[..idx];
                        if (string.IsNullOrEmpty(stem) || stem.Length < 3) continue;
                        if (Confidence.Generate(displayName, stem) >= 70)
                            results.Add($@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Tracing\{name}");
                    }
                }
            }
            catch { }
        }

        private static void ScanUserAssist(string displayName, HashSet<string> results)
        {
            try
            {
                using var ua = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\UserAssist", false);
                if (ua == null) return;
                foreach (var guid in ua.GetSubKeyNames())
                {
                    using var countKey = ua.OpenSubKey($@"{guid}\Count", false);
                    if (countKey == null) continue;
                    foreach (var valName in countKey.GetValueNames())
                    {
                        string decoded = Rot13(valName);
                        string itemName = Path.GetFileNameWithoutExtension(decoded);
                        if (!string.IsNullOrEmpty(itemName) && Confidence.Generate(displayName, itemName) >= 70)
                        {
                            results.Add($@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{guid}\Count");
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        private static string Rot13(string input)
        {
            char[] chars = input.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (chars[i] >= 'a' && chars[i] <= 'z')
                    chars[i] = (char)((chars[i] - 'a' + 13) % 26 + 'a');
                else if (chars[i] >= 'A' && chars[i] <= 'Z')
                    chars[i] = (char)((chars[i] - 'A' + 13) % 26 + 'A');
            }
            return new string(chars);
        }

        // ── Cross-reference helpers ─────────────────────────────────

        private static List<string> GetAllInstallLocations(string? excludeName = null)
        {
            var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] hiveKeys =
            {
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            };

            foreach (var hiveKey in hiveKeys)
            {
                var hive = ResolveHive(hiveKey, out string subKey);
                if (hive == null) continue;
                try
                {
                    using var key = hive.OpenSubKey(subKey, false);
                    if (key == null) continue;
                    foreach (var name in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var sk = key.OpenSubKey(name);
                            var dn = sk?.GetValue("DisplayName") as string;
                            if (!string.IsNullOrEmpty(excludeName) && dn == excludeName) continue;
                            var loc = sk?.GetValue("InstallLocation") as string;
                            if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc))
                                locations.Add(Path.GetFullPath(loc).TrimEnd('\\'));
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return locations.ToList();
        }

        // ── Cleanup ────────────────────────────────────────────────

        public static string? GetInstallLocationFromRegistry(string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return null;
            string[] hivePaths =
            [
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            ];
            foreach (var hivePath in hivePaths)
            {
                try
                {
                    var hive = ResolveHive(hivePath, out string subKey);
                    if (hive == null || string.IsNullOrEmpty(subKey)) continue;
                    using var key = hive.OpenSubKey(subKey, false);
                    if (key == null) continue;
                    foreach (var name in key.GetSubKeyNames())
                    {
                        using var sk = key.OpenSubKey(name, false);
                        if (sk == null) continue;
                        var dn = sk.GetValue("DisplayName") as string;
                        if (string.IsNullOrEmpty(dn)) continue;
                        if (dn.Equals(displayName, StringComparison.OrdinalIgnoreCase) ||
                            dn.StartsWith(displayName, StringComparison.OrdinalIgnoreCase))
                        {
                            var loc = sk.GetValue("InstallLocation") as string;
                            if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc))
                                return loc.TrimEnd('\\');
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        public static void PerformCleanup(List<string> filesToDelete, List<string> registryToDelete, UninstallResult result)
        {
            var logEntries = new List<string>();
            logEntries.Add($"=== KitLugia Deletion Log — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            logEntries.Add("");

            foreach (var file in filesToDelete.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    // Self-check: never delete KitLugia's own files
                    if (IsKitLugiaSelfPath(file))
                        continue;

                    // Prohibited-location check: never delete system paths
                    if (IsProhibitedLocation(file))
                        continue;

                    // System folder check (additional guard)
                    if (IsSystemFolder(file))
                        continue;

                    // Too-broad check: never delete the root of user shell folders or shallow paths
                    if (IsTooBroadForDeletion(file))
                        continue;

                    // Backup file before deletion
                    string? fileBackup = BackupFileItem(file);
                    if (fileBackup != null)
                        result.BackupFiles.Add($"{file}|{fileBackup}");

                    if (Directory.Exists(file))
                    {
                        // Recycle bin first approach (BCUninstaller pattern)
                        try
                        {
                            FileSystem.DeleteDirectory(file,
                                UIOption.OnlyErrorDialogs,
                                RecycleOption.SendToRecycleBin);
                            result.FilesDeleted++;
                            logEntries.Add($"REMOVED  {file} [to Recycle Bin]");
                        }
                        catch
                        {
                            // Fallback to permanent delete if recycle bin fails
                            Directory.Delete(file, true);
                            result.FilesDeleted++;
                            logEntries.Add($"REMOVED  {file} [permanent]");
                        }
                    }
                    else if (File.Exists(file))
                    {
                        try
                        {
                            FileSystem.DeleteFile(file,
                                UIOption.OnlyErrorDialogs,
                                RecycleOption.SendToRecycleBin);
                            result.FilesDeleted++;
                            logEntries.Add($"REMOVED  {file} [to Recycle Bin]");
                        }
                        catch
                        {
                            File.Delete(file);
                            result.FilesDeleted++;
                            logEntries.Add($"REMOVED  {file} [permanent]");
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"File: {file} -> {ex.Message}");
                    logEntries.Add($"FAILED   {file} -> {ex.Message}");
                }
            }

            foreach (var reg in registryToDelete.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var parts = reg.Split(new[] { '\\' }, 3, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    RegistryKey? hive = ResolveHive(reg, out _);
                    if (hive == null) continue;
                    string keyPath = parts.Length > 2 ? parts[2] : parts[1];

                    // Safety: never delete the root of Microsoft hives - only specific subkeys
                    // Our scanning only produces specific subkey paths, so this is a guard.
                    if (IsRegPathTooBroad(reg, parts, keyPath))
                        continue;

                    // Backup registry key to .reg before deletion (restore safety)
                    string? backupFile = BackupRegistryKey(reg);
                    if (backupFile != null)
                    {
                        result.BackupRegistryFiles.Add(backupFile);
                    }

                    hive.DeleteSubKeyTree(keyPath, false);
                    result.RegistryDeleted++;
                    logEntries.Add($"REMOVED  {reg}");
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Registry: {reg} -> {ex.Message}");
                    logEntries.Add($"FAILED   {reg} -> {ex.Message}");
                }
            }

            // Write deletion log
            try
            {
                if (!Directory.Exists(DeletionLogDir))
                    Directory.CreateDirectory(DeletionLogDir);
                string logFile = Path.Combine(DeletionLogDir, $"cleanup_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                File.WriteAllLines(logFile, logEntries);
                result.DeletionLogFile = logFile;
            }
            catch { }
        }

        private static string? BackupFileItem(string fullPath)
        {
            try
            {
                if (!Directory.Exists(FileBackupDir))
                    Directory.CreateDirectory(FileBackupDir);
                string safeName = fullPath
                    .Replace('\\', '_')
                    .Replace(':', '_')
                    .Replace('/', '_')
                    .Trim('_');
                if (safeName.Length > 150) safeName = safeName[^150..];
                string dest = Path.Combine(FileBackupDir, $"{safeName}_{DateTime.Now:yyyyMMddHHmmss}");
                if (Directory.Exists(fullPath))
                {
                    if (!Directory.Exists(dest))
                        Directory.CreateDirectory(dest);
                    foreach (var srcDir in Directory.GetDirectories(fullPath, "*", System.IO.SearchOption.AllDirectories))
                        Directory.CreateDirectory(srcDir.Replace(fullPath, dest));
                    foreach (var srcFile in Directory.GetFiles(fullPath, "*", System.IO.SearchOption.AllDirectories))
                    {
                        string rel = srcFile.Replace(fullPath, "");
                        string targetFile = dest + rel;
                        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                        File.Copy(srcFile, targetFile, true);
                    }
                    return dest;
                }
                else if (File.Exists(fullPath))
                {
                    File.Copy(fullPath, dest, true);
                    return dest;
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Restores a previously backed-up registry .reg file.
        /// </summary>
        public static void RestoreRegistryBackup(string regBackupFile)
        {
            try
            {
                if (!File.Exists(regBackupFile)) return;
                var psi = new ProcessStartInfo("reg.exe", $"import \"{regBackupFile}\"")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(10000);
            }
            catch { }
        }

        /// <summary>
        /// Restores backed-up files from the temp backup folder.
        /// </summary>
        public static void RestoreFileBackup(string backupPath, string originalPath)
        {
            try
            {
                if (Directory.Exists(backupPath))
                {
                    if (!Directory.Exists(originalPath))
                        Directory.CreateDirectory(originalPath);
                    foreach (var srcFile in Directory.GetFiles(backupPath, "*", System.IO.SearchOption.AllDirectories))
                    {
                        string rel = srcFile[backupPath.Length..].TrimStart('\\');
                        string target = Path.Combine(originalPath, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(srcFile, target, true);
                    }
                }
                else if (File.Exists(backupPath))
                {
                    string? dir = Path.GetDirectoryName(originalPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.Copy(backupPath, originalPath, true);
                }
            }
            catch { }
        }

        // ── Value-based Registry Match ─────────────────────────────

        /// <summary>
        /// Checks if any value data inside a registry key references the app's install location or executable name.
        /// This catches CLSID/AppID/TypeLib entries that would never match by subkey name (GUIDs).
        /// </summary>
        private static bool KeyHasValueReferencing(RegistryKey key, string installLocation, string displayName)
        {
            if (key == null) return false;
            try
            {
                foreach (var valueName in key.GetValueNames())
                {
                    var data = key.GetValue(valueName) as string;
                    if (string.IsNullOrEmpty(data)) continue;

                    if (!string.IsNullOrEmpty(installLocation))
                    {
                        string normalizedInstall = installLocation.TrimEnd('\\');
                        if (data.IndexOf(normalizedInstall, StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                        string dir = Path.GetDirectoryName(data) ?? "";
                        if (dir.IndexOf(normalizedInstall, StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }

                    if (!string.IsNullOrEmpty(displayName))
                    {
                        string fileName = Path.GetFileNameWithoutExtension(data) ?? "";
                        if (!string.IsNullOrEmpty(fileName) && Confidence.Generate(displayName, fileName) >= 85)
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Scans a flat hive (e.g. CLSID, AppID, TypeLib) by value content rather than key name.
        /// Catches GUID-based entries that reference the install path in their default/inproc values.
        /// </summary>
        private static void ScanHiveByValues(string hiveKey, string installLocation, string displayName, HashSet<string> results)
        {
            if (string.IsNullOrEmpty(installLocation)) return;
            try
            {
                var hive = ResolveHive(hiveKey, out string subKey);
                if (hive == null || string.IsNullOrEmpty(subKey)) return;
                using var key = hive.OpenSubKey(subKey, false);
                if (key == null) return;

                foreach (var name in key.GetSubKeyNames())
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    if (SystemFolderNames.Contains(name)) continue;
                    try
                    {
                        using var sk = key.OpenSubKey(name, false);
                        if (sk != null && KeyHasValueReferencing(sk, installLocation, displayName))
                            results.Add($@"{hiveKey}\{name}");
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// Scans Installer\Folders where value names are literal paths to install directories.
        /// </summary>
        private static void ScanInstallerFolders(string installLocation, HashSet<string> results)
        {
            if (string.IsNullOrEmpty(installLocation)) return;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Folders", false);
                if (key == null) return;
                string normalizedInstall = installLocation.TrimEnd('\\');
                foreach (var valName in key.GetValueNames())
                {
                    if (valName.StartsWith(normalizedInstall, StringComparison.OrdinalIgnoreCase))
                        results.Add($@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Folders\{valName}");
                }
            }
            catch { }
        }

        /// <summary>
        /// Scans Installer\Components for GUID entries whose default value references a file
        /// installed by this app.
        /// </summary>
        private static void ScanInstallerComponentsByValues(string installLocation, string displayName, HashSet<string> results)
        {
            if (string.IsNullOrEmpty(installLocation)) return;
            try
            {
                using var userData = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData", false);
                if (userData == null) return;
                string normalizedInstall = installLocation.TrimEnd('\\');
                foreach (var sid in userData.GetSubKeyNames())
                {
                    using var components = userData.OpenSubKey($@"{sid}\Components", false);
                    if (components == null) continue;
                    foreach (var guid in components.GetSubKeyNames())
                    {
                        try
                        {
                            using var sk = components.OpenSubKey(guid, false);
                            if (sk == null) continue;
                            foreach (var valName in sk.GetValueNames())
                            {
                                var data = sk.GetValue(valName) as string;
                                if (string.IsNullOrEmpty(data)) continue;
                                if (data.IndexOf(normalizedInstall, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    results.Add($@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\{sid}\Components\{guid}");
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        // ── Helpers ────────────────────────────────────────────────

        private static RegistryKey? ResolveHive(string fullPath, out string subKey)
        {
            subKey = "";
            if (fullPath.StartsWith("HKEY_LOCAL_MACHINE\\")) { subKey = fullPath["HKEY_LOCAL_MACHINE\\".Length..]; return Registry.LocalMachine; }
            if (fullPath.StartsWith("HKEY_CURRENT_USER\\")) { subKey = fullPath["HKEY_CURRENT_USER\\".Length..]; return Registry.CurrentUser; }
            if (fullPath.StartsWith("HKEY_CLASSES_ROOT\\")) { subKey = fullPath["HKEY_CLASSES_ROOT\\".Length..]; return Registry.ClassesRoot; }
            if (fullPath.StartsWith("HKEY_USERS\\")) { subKey = fullPath["HKEY_USERS\\".Length..]; return Registry.Users; }
            return null;
        }

        private static (string fileName, string args) ParseCommandLine(string cmd)
        {
            cmd = cmd.Trim();
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                if (end > 0) return (cmd[1..end], end + 1 < cmd.Length ? cmd[(end + 1)..].TrimStart() : "");
            }
            int space = cmd.IndexOf(' ');
            if (space > 0) return (cmd[..space], cmd[(space + 1)..].TrimStart());
            return (cmd, "");
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            name = Regex.Replace(name, @"\s+\d+[\d.]*\d$", "");
            name = Regex.Replace(name, @"\s+(Inc|LLC|Ltd|Limited|Corp|Corporation|GmbH|SAS|SRL|SA|Pty|Ltee)\.?$", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\s*\([^)]*\)$", "");
            name = Regex.Replace(name, @"[™©®]", "");
            return name.Trim().TrimEnd('.');
        }

        /// <summary>
        /// Looks for a known uninstaller executable inside the install directory.
        /// Prevents running the main app exe (e.g. RustDesk.exe) when the registry
        /// points to the app itself instead of a real uninstaller.
        /// Returns the full path to the best candidate, or null.
        /// </summary>
        private static string? FindInstalledUninstaller(string installLocation, string registryUninstallString)
        {
            if (string.IsNullOrEmpty(installLocation) || !Directory.Exists(installLocation))
                return null;

            // Extended list of known uninstaller executables (InnoSetup, NSIS, InstallShield, Wise, custom)
            string[] knownUninstallers =
            {
                "unins000.exe", "unins001.exe", "unins002.exe", "unins003.exe",
                "uninstall.exe", "Uninstall.exe", "uninst.exe",
                "isuninst.exe", "setup.exe", "Setup.exe",
                "uninstall64.exe", "aux_unins.exe",
            };

            // Pattern: also match uninsNNN.exe for any NNN
            try
            {
                foreach (var f in Directory.GetFiles(installLocation, "unins*.exe"))
                {
                    string name = Path.GetFileName(f);
                    if (!knownUninstallers.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        var match = Regex.Match(name, @"^unins\d{3}\.exe$", RegexOptions.IgnoreCase);
                        if (match.Success)
                            knownUninstallers = [..knownUninstallers, name];
                    }
                }
            }
            catch { }

            // Parse what the registry currently points to
            var (regFile, regArgs) = ParseCommandLine(registryUninstallString ?? "");
            string regFileName = !string.IsNullOrEmpty(regFile) ? Path.GetFileName(regFile) : "";

            // Build all candidates from install directory
            var candidates = new List<(string path, string name)>();

            // Root install dir
            foreach (var name in knownUninstallers)
            {
                string path = Path.Combine(installLocation, name);
                if (File.Exists(path))
                    candidates.Add((path, name));
            }

            // Subdirectories: uninstall, Uninstall, _uninst, and any dir named like "uninstall"
            try
            {
                foreach (var subDir in Directory.GetDirectories(installLocation))
                {
                    string dirName = Path.GetFileName(subDir);
                    if (string.IsNullOrEmpty(dirName)) continue;
                    if (dirName.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase) ||
                        dirName.StartsWith("_uninst", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("Installer", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var name in knownUninstallers)
                        {
                            string fp = Path.Combine(subDir, name);
                            if (File.Exists(fp))
                                candidates.Add((fp, name));
                        }
                    }
                }
            }
            catch { }

            // If the registry already points to a known uninstaller INSIDE the install dir,
            // prefer the registry version (it may have important switches).
            // Otherwise, if we found one physically, use it — this handles cases where
            // the registry points to the main app exe instead of the real uninstaller.
            if (!string.IsNullOrEmpty(regFileName))
            {
                foreach (var (path, name) in candidates)
                {
                    if (regFileName.Equals(name, StringComparison.OrdinalIgnoreCase))
                        return null; // Registry already has this uninstaller, keep it
                }
            }

            // Return the first physical candidate (prefer unins000 over uninstall.exe)
            string[] priority = { "unins000.exe", "unins001.exe", "uninstall.exe", "isuninst.exe", "setup.exe" };
            foreach (var p in priority)
            {
                var match = candidates.FirstOrDefault(c => c.name.Equals(p, StringComparison.OrdinalIgnoreCase));
                if (match.path != null) return match.path;
            }

            return candidates.Count > 0 ? candidates[0].path : null;
        }

        /// <summary>
        /// Finds the full registry path to the app's Uninstall key, or null if not found.
        /// </summary>
        private static string? FindUninstallRegistryKey(string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return null;
            (string hive, string path)[] uninstallKeys =
            [
                (@"HKEY_LOCAL_MACHINE", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (@"HKEY_LOCAL_MACHINE", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (@"HKEY_CURRENT_USER", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            ];
            foreach (var (hive, keyPath) in uninstallKeys)
            {
                try
                {
                    var hiveKey = ResolveHive(hive + "\\dummy", out _);
                    if (hiveKey == null) continue;
                    using var key = hiveKey.OpenSubKey(keyPath, false);
                    if (key == null) continue;
                    foreach (var sub in key.GetSubKeyNames())
                    {
                        using var sk = key.OpenSubKey(sub, false);
                        var dn = sk?.GetValue("DisplayName") as string;
                        if (!string.IsNullOrEmpty(dn) && dn.Equals(displayName, StringComparison.OrdinalIgnoreCase))
                            return $@"{hive}\{keyPath}\{sub}";
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Checks if the app is still listed in any Uninstall registry key.
        /// Used to verify whether the uninstaller actually removed the app.
        /// </summary>
        private static bool IsAppStillRegistered(string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return false;
            string[] uninstallKeys =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };
            foreach (var key in uninstallKeys)
            {
                try
                {
                    using var hklm = Registry.LocalMachine.OpenSubKey(key, false);
                    if (hklm == null) continue;
                    foreach (var sub in hklm.GetSubKeyNames())
                    {
                        using var sk = hklm.OpenSubKey(sub, false);
                        var dn = sk?.GetValue("DisplayName") as string;
                        if (!string.IsNullOrEmpty(dn) && dn.Equals(displayName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                catch { }
            }
            return false;
        }

        // ── System Restore Point ──────────────────────────────────

        [DllImport("Srclient.dll", CharSet = CharSet.Unicode)]
        private static extern int SRSetRestorePointW(ref RestorePointInfo pRestorePtSpec, out StatMgrStatus pSMgrStatus);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RestorePointInfo
        {
            public int dwEventType;
            public int dwRestorePtType;
            public long llSequenceNumber;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szDescription;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StatMgrStatus
        {
            public int nStatus;
            public long llSequenceNumber;
        }

        private const int BEGIN_SYSTEM_CHANGE = 100;
        private const int END_SYSTEM_CHANGE = 101;
        private const int APPLICATION_INSTALL = 0;
        private const int MODIFY_SETTINGS = 1;
        private const int CANCELLED_OPERATION = 13;

        /// <summary>
        /// Creates a System Restore Point before destructive operations.
        /// Returns true if successful or if the call failed non-critically.
        /// </summary>
        public static bool TryCreateRestorePoint(string description)
        {
            try
            {
                var info = new RestorePointInfo
                {
                    dwEventType = BEGIN_SYSTEM_CHANGE,
                    dwRestorePtType = MODIFY_SETTINGS,
                    szDescription = description ?? "KitLugia DeepUninstaller"
                };
                int result = SRSetRestorePointW(ref info, out _);
                if (result == 0) return false; // ERROR

                info.dwEventType = END_SYSTEM_CHANGE;
                info.dwRestorePtType = MODIFY_SETTINGS;
                SRSetRestorePointW(ref info, out _);
                return true;
            }
            catch { return false; }
        }

        // ── Registry .reg Backup ─────────────────────────────────

        private static readonly string RegistryBackupDir = Path.Combine(
            Path.GetTempPath(), "KitLugia", "RegBackup");

        /// <summary>
        /// Exports a registry key to a .reg file before deletion.
        /// Returns the path to the backup file, or null on failure.
        /// </summary>
        private static string? BackupRegistryKey(string fullRegistryPath)
        {
            try
            {
                if (!Directory.Exists(RegistryBackupDir))
                    Directory.CreateDirectory(RegistryBackupDir);

                string safeName = fullRegistryPath
                    .Replace('\\', '_')
                    .Replace('/', '_')
                    .Replace(':', '_')
                    .Trim('_');
                if (safeName.Length > 200) safeName = safeName[^200..];
                string backupFile = Path.Combine(RegistryBackupDir,
                    $"{safeName}_{DateTime.Now:yyyyMMddHHmmss}.reg");

                var sb = new StringBuilder();
                sb.AppendLine("Windows Registry Editor Version 5.00");
                sb.AppendLine();

                var hive = ResolveHive(fullRegistryPath, out string subKey);
                if (hive == null || string.IsNullOrEmpty(subKey)) return null;

                using var key = hive.OpenSubKey(subKey, false);
                if (key == null) return null;

                ExportKeyToReg(sb, fullRegistryPath, key);
                File.WriteAllText(backupFile, sb.ToString(), Encoding.Unicode);
                return backupFile;
            }
            catch { return null; }
        }

        private static void ExportKeyToReg(StringBuilder sb, string fullPath, RegistryKey key)
        {
            sb.AppendLine($@"[{fullPath}]");
            foreach (var valName in key.GetValueNames())
            {
                var val = key.GetValue(valName);
                if (val == null) continue;
                var kind = key.GetValueKind(valName);

                string escapedName = valName == "" ? "@" : EscapeRegString(valName);
                switch (kind)
                {
                    case RegistryValueKind.String:
                    case RegistryValueKind.ExpandString:
                        sb.AppendLine($@"{escapedName}=""{EscapeRegString(val.ToString() ?? "")}""");
                        break;
                    case RegistryValueKind.DWord:
                        sb.AppendLine($@"{escapedName}=dword:{unchecked((uint)(int)val):x8}");
                        break;
                    case RegistryValueKind.QWord:
                        sb.Append($@"{escapedName}=hex(b):");
                        foreach (byte b in BitConverter.GetBytes((long)val))
                            sb.Append($"{b:x2},");
                        sb.AppendLine();
                        break;
                    case RegistryValueKind.Binary:
                        sb.AppendLine($@"{escapedName}=hex:{BitConverter.ToString((byte[])val).Replace('-', ',').ToLowerInvariant()}");
                        break;
                    case RegistryValueKind.MultiString:
                        var parts = (string[])val;
                        sb.AppendLine($@"{escapedName}=hex(7):{string.Join(",", parts.SelectMany(s => Encoding.Unicode.GetBytes(s + "\0")).Select(b => b.ToString("x2")))}");
                        break;
                }
            }
            sb.AppendLine();

            foreach (var name in key.GetSubKeyNames())
            {
                using var sk = key.OpenSubKey(name, false);
                if (sk != null)
                    ExportKeyToReg(sb, $@"{fullPath}\{name}", sk);
            }
        }

        private static string EscapeRegString(string s)
        {
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r");
        }

        // ── Process Tree Kill (single WMI query) ──────────────────

        /// <summary>
        /// Kills a process by PID gracefully, then forcefully if needed.
        /// </summary>
        private static void KillProcessById(int pid)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (proc != null && !proc.HasExited)
                    KillProcessGracefully(proc);
            }
            catch { }
        }

        /// <summary>
        /// Kills all processes related to the app + child processes.
        /// Uses Process.GetProcesses (fast, no WMI) and tries WMI once briefly as fallback
        /// for child process cleanup. WMI timeout is capped at 2s.
        /// </summary>
        private static void KillProcessesWithTree(string displayName, string installLocation, List<string>? extraExeNames = null)
        {
            var killedIds = new HashSet<int>();
            var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(displayName))
            {
                targetNames.Add(SanitizeName(displayName));
                string compressed = Regex.Replace(displayName, @"[\s\-_.]+", "");
                if (compressed.Length >= 3) targetNames.Add(compressed);
            }

            if (!string.IsNullOrEmpty(installLocation))
            {
                try
                {
                    string dir = Path.GetFileName(installLocation.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(dir)) targetNames.Add(dir);
                }
                catch { }
            }

            if (extraExeNames != null)
                foreach (var exe in extraExeNames)
                    if (!string.IsNullOrEmpty(exe))
                        targetNames.Add(exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            ? exe[..^4] : exe);

            int selfPid = Environment.ProcessId;

            // Phase 1: Kill matching processes by name (fast, no WMI)
            foreach (var name in targetNames)
            {
                try
                {
                    foreach (var proc in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            if (proc.Id == selfPid || proc.HasExited || !killedIds.Add(proc.Id)) continue;
                            if (ProtectedProcessNames.Contains(proc.ProcessName)) continue;
                            KillProcessGracefully(proc);
                        }
                        catch { }
                        finally { proc.Dispose(); }
                    }
                }
                catch { }
            }

            // Phase 2: Try WMI once for child process cleanup (capped at 2s via task timeout)
            if (killedIds.Count > 0)
            {
                try
                {
                    var wmiTask = Task.Run(() =>
                    {
                        try
                        {
                            var parentToChildren = new Dictionary<int, List<int>>();
                            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process");
                            foreach (ManagementObject mo in searcher.Get())
                            {
                                try
                                {
                                    int pid = Convert.ToInt32(mo["ProcessId"]);
                                    int ppid = Convert.ToInt32(mo["ParentProcessId"]);
                                    if (pid > 0 && ppid > 0)
                                    {
                                        if (!parentToChildren.ContainsKey(ppid))
                                            parentToChildren[ppid] = new List<int>();
                                        parentToChildren[ppid].Add(pid);
                                    }
                                }
                                catch { }
                            }

                            // Collect descendants of killed PIDs
                            var toKill = new List<int>();
                            var visited = new HashSet<int>();
                            foreach (var pid in killedIds)
                                CollectDescendants(pid, parentToChildren, toKill, visited);

                            // Kill deepest first
                            for (int i = toKill.Count - 1; i >= 0; i--)
                            {
                                int pid = toKill[i];
                                if (pid == selfPid) continue;
                                try
                                {
                                    using var proc = Process.GetProcessById(pid);
                                    if (proc != null && !proc.HasExited)
                                    {
                                        if (ProtectedProcessNames.Contains(proc.ProcessName)) continue;
                                        KillProcessGracefully(proc);
                                    }
                                }
                                catch { }
                            }
                        }
                        catch { }
                    });
                    wmiTask.Wait(2000);
                }
                catch { }
            }
        }

        private static void CollectDescendants(int parentPid, Dictionary<int, List<int>> parentToChildren, List<int> killOrder, HashSet<int> visited)
        {
            if (parentToChildren.TryGetValue(parentPid, out var children))
            {
                foreach (var child in children)
                {
                    if (!visited.Contains(child))
                    {
                        visited.Add(child);
                        CollectDescendants(child, parentToChildren, killOrder, visited);
                        killOrder.Add(child);
                    }
                }
            }
        }

        // ── SortedExecutables Builder ──────────────────────────────

        /// <summary>
        /// Builds a list of executable paths associated with the app.
        /// Used to improve Prefetch/WER/HeapLeak/Tracing scanners.
        /// </summary>
        private static List<string> BuildSortedExecutables(string installLocation, string displayIcon, string uninstallString)
        {
            var exes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Exes from install directory
            if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
            {
                try
                {
                    foreach (var f in Directory.GetFiles(installLocation, "*.exe", System.IO.SearchOption.TopDirectoryOnly))
                        exes.Add(f);
                    foreach (var f in Directory.GetFiles(installLocation, "*.dll", System.IO.SearchOption.TopDirectoryOnly))
                        exes.Add(f);
                }
                catch { }
            }

            // DisplayIcon
            if (!string.IsNullOrEmpty(displayIcon) && File.Exists(displayIcon))
                exes.Add(displayIcon);

            // Uninstall string
            if (!string.IsNullOrEmpty(uninstallString))
            {
                var (file, _) = ParseCommandLine(uninstallString);
                if (!string.IsNullOrEmpty(file) && File.Exists(file))
                    exes.Add(file);
            }

            return exes.OrderBy(e => e).ToList();
        }

        // ── Cross-Hive Registry Linking ────────────────────────────

        /// <summary>
        /// Given a found registry key in one hive, checks equivalent paths in other hives
        /// and adds them if they exist. E.g., if found in HKLM\SOFTWARE\Foo,
        /// also checks HKCU\SOFTWARE\Foo and HKLM\SOFTWARE\WOW6432Node\Foo.
        /// </summary>
        private static List<string> GetRelatedKeys(string foundKey)
        {
            var related = new List<string>();
            try
            {
                // Hive equivalence mapping
                var equivalents = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["HKEY_LOCAL_MACHINE"] = new[] { "HKEY_CURRENT_USER" },
                    ["HKEY_CURRENT_USER"] = new[] { "HKEY_LOCAL_MACHINE" },
                };

                string prefix = "";
                string remainder = foundKey;
                foreach (var kv in equivalents)
                {
                    if (foundKey.StartsWith(kv.Key + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        prefix = kv.Key;
                        remainder = foundKey[(kv.Key.Length + 1)..];
                        foreach (var alt in kv.Value)
                        {
                            string altPath = $@"{alt}\{remainder}";
                            var altHive = ResolveHive(altPath, out string altSub);
                            if (altHive == null || string.IsNullOrEmpty(altSub)) continue;
                            using var altKey = altHive.OpenSubKey(altSub, false);
                            if (altKey != null)
                                related.Add(altPath);

                            // Also check WOW6432Node equivalent
                            if (remainder.StartsWith("SOFTWARE\\", StringComparison.OrdinalIgnoreCase) && Is64Bit)
                            {
                                string wowPath = $@"{alt}\SOFTWARE\WOW6432Node\{remainder["SOFTWARE\\".Length..]}";
                                var wowHive = ResolveHive(wowPath, out string wowSub);
                                if (wowHive == null || string.IsNullOrEmpty(wowSub)) continue;
                                using var wowKey = wowHive.OpenSubKey(wowSub, false);
                                if (wowKey != null)
                                    related.Add(wowPath);
                            }
                        }
                        break;
                    }
                }
            }
            catch { }
            return related;
        }

        // ── Expanded COM Scanning ──────────────────────────────────

        /// <summary>
        /// Scans additional COM-related hives beyond CLSID/AppID/Interface/TypeLib:
        /// ProgID, ShellEx, PersistentHandler, OpenWithProgIDs.
        /// </summary>
        private static void ScanComHives(string displayName, string installLocation, HashSet<string> results)
        {
            string[][] comPaths =
            [
                // ProgIDs
                ["HKEY_LOCAL_MACHINE\\SOFTWARE\\Classes", "HKEY_LOCAL_MACHINE\\SOFTWARE\\Classes\\Installer\\Components"],
                // ShellEx
                ["HKEY_LOCAL_MACHINE\\SOFTWARE\\Classes\\ShellEx", "HKEY_LOCAL_MACHINE\\SOFTWARE\\Classes\\AllFileSystemObjects\\ShellEx"],
                // PersistentHandler
                ["HKEY_LOCAL_MACHINE\\SOFTWARE\\Classes\\PersistentHandler", "HKEY_LOCAL_MACHINE\\SOFTWARE\\Classes\\CLSID\\PersistentHandler"],
                // OpenWithProgIDs
                ["HKEY_LOCAL_MACHINE\\SOFTWARE\\Classes\\OpenWithProgIds", "HKEY_CURRENT_USER\\SOFTWARE\\Classes\\OpenWithProgIds"],
            ];

            foreach (var group in comPaths)
            {
                foreach (var path in group)
                {
                    ScanHiveForNames(path, displayName, results, installLocation);
                    ScanHiveByValues(path, installLocation, displayName, results);
                }
            }
        }

        // ── Empty Directory / Questionable Name Detection ──────────

        private static readonly HashSet<string> QuestionableDirNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "install", "Installer", "setup", "config", "configuration",
            "data", "temp", "tmp", "cache", "log", "logs",
            "bin", "lib", "share", "common", "resources", "runtime",
            "backup", "backups", "archive", "archives", "download",
            "update", "updates", "patch", "patches", "plugin",
            "plugins", "addon", "addons", "extension", "extensions",
        };

        /// <summary>
        /// Returns true if the directory is empty (no files, no non-empty subdirs).
        /// </summary>
        private static bool IsEmptyDirectory(string dirPath)
        {
            try
            {
                if (!Directory.Exists(dirPath)) return true;
                if (Directory.GetFiles(dirPath).Length > 0) return false;
                foreach (var sub in Directory.GetDirectories(dirPath))
                {
                    if (!IsEmptyDirectory(sub)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Returns true if the directory name is a "questionable" generic name
        /// that commonly appears in many apps (install, config, data, etc.).
        /// </summary>
        private static bool IsQuestionableName(string dirName)
        {
            return !string.IsNullOrEmpty(dirName) && QuestionableDirNames.Contains(dirName);
        }

        /// <summary>
        /// Scans leftover files and flags empty directories + questionable names
        /// for additional cleanup.
        /// </summary>
        private static void ScanEmptyAndQuestionableDirs(string baseDir, string displayName, HashSet<string> results)
        {
            if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) return;
            try
            {
                foreach (var dir in Directory.GetDirectories(baseDir, "*", System.IO.SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name) || SystemFolderNames.Contains(name)) continue;
                    if (IsProhibitedLocation(dir) || IsSystemFolder(dir)) continue;

                    // Empty dir with matching name
                    if (IsEmptyDirectory(dir) && Confidence.Generate(displayName, name) >= 70)
                        results.Add(dir);

                    // Questionable-name subdirs inside a confidence-matched dir
                    if (IsQuestionableName(name))
                    {
                        string parent = Directory.GetParent(dir)?.FullName ?? "";
                        if (!string.IsNullOrEmpty(parent))
                        {
                            string parentName = Path.GetFileName(parent);
                            if (!string.IsNullOrEmpty(parentName) && Confidence.Generate(displayName, parentName) >= 70)
                                results.Add(dir);
                        }
                    }
                }
            }
            catch { }
        }

        // ── Quiet Uninstall String Generation ─────────────────────

        /// <summary>
        /// Generates a quiet/silent uninstall string for known installer types.
        /// Falls back to the original string if the type is unknown.
        /// </summary>
        private static string GenerateQuietUninstallString(string uninstallString, string installLocation)
        {
            if (string.IsNullOrEmpty(uninstallString)) return uninstallString;

            string lower = uninstallString.ToLowerInvariant();
            string trimmed = uninstallString.TrimEnd();

            // MSI-based: msiexec /x {product-code} /qn
            if (lower.Contains("msiexec") || lower.Contains(".msi"))
            {
                // Extract the product code (GUID) from the string
                var msiMatch = Regex.Match(uninstallString, @"\{[0-9A-Fa-f\-]{36}\}");
                if (msiMatch.Success)
                    return $"msiexec /x {msiMatch.Value} /qn /norestart";
                // Also check for /I{guid} pattern
                var iMatch = Regex.Match(uninstallString, @"/I\{[0-9A-Fa-f\-]{36}\}");
                if (iMatch.Success)
                    return $"msiexec /x {iMatch.Value[3..]} /qn /norestart";
            }

            // Inno Setup: /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
            if (lower.Contains("/verysilent") || lower.Contains("unins000"))
                return $"{trimmed} /VERYSILENT /SUPPRESSMSGBOXES /NORESTART";

            // NSIS: /S (silent), /SD (silent with default answers)
            if (lower.Contains("/s") || lower.Contains("uninstall.exe") ||
                lower.Contains("insthelper"))
                return $"{trimmed} /S /SD";

            // InstallShield: -s /s /sms
            if (lower.Contains("isuninst.exe") || lower.Contains("-s") ||
                lower.Contains("setup.exe"))
                return $"{trimmed} -s -f1\"{installLocation}\"uninstall.iss";

            // Wise Installation System: /s
            if (lower.Contains("wise") || lower.Contains("/s"))
                return $"{trimmed} /s";

            // Generic app exe uninstall switches (when no known installer type is detected)
            // Some apps use their own exe as the uninstaller with special switches
            {
                var (fName, fArgs) = ParseCommandLine(uninstallString);
                if (!string.IsNullOrEmpty(fName) && fName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    string fLower = fName.ToLowerInvariant();
                    string baseName = Path.GetFileNameWithoutExtension(fLower);
                    // Only add --uninstall if the exe name doesn't already look like a known uninstaller
                    if (string.IsNullOrEmpty(fArgs) &&
                        !fLower.Contains("unins") &&
                        !baseName.Contains("uninstall") &&
                        !baseName.Contains("uninst") &&
                        !fLower.Contains("msiexec"))
                    {
                        return $"\"{fName}\" --uninstall";
                    }
                }
            }

            return uninstallString;
        }

        // ── Exit Code Interpretation ──────────────────────────────

        /// <summary>
        /// Interprets uninstaller exit codes.
        /// Returns true if the uninstall was successful or user-cancelled
        /// (user cancel = not a failure).
        /// </summary>
        private static bool InterpretExitCode(int exitCode, string uninstallString, UninstallResult result)
        {
            // MSI: 1602 = user cancelled (not a failure)
            if (exitCode == 1602)
            {
                result.Errors.Add("Uninstall cancelled by user (exit code 1602).");
                return false;
            }

            // NSIS: exit code 1 or 2 typically means user cancelled
            if (exitCode == 1 || exitCode == 2)
            {
                string lower = uninstallString.ToLowerInvariant();
                if (lower.Contains("unins000") || lower.Contains("uninstall.exe") ||
                    lower.Contains("nsis") || lower.Contains("/s"))
                {
                    result.Errors.Add($"Uninstall cancelled by user (NSIS exit code {exitCode}).");
                    return false;
                }
            }

            if (exitCode == 0) return true;

            result.Errors.Add($"Uninstaller exited with code {exitCode}.");
            return false;
        }

        // ── Child Process Monitoring ──────────────────────────────

        /// <summary>
        /// Monitors the uninstaller process and its children for stalls.
        /// Returns true if the process tree has been active recently,
        /// false if stalled (no CPU time change in threshold ms).
        /// </summary>
        private static bool IsProcessTreeActive(Process parent, int thresholdMs = 30_000)
        {
            try
            {
                if (parent.HasExited) return false;

                var now = DateTime.UtcNow;
                var snapshot = GetProcessTreeSnapshot(parent);

                foreach (var (proc, lastCpu, lastSample) in snapshot)
                {
                    try
                    {
                        if (proc.HasExited) continue;
                        TimeSpan cpu = proc.TotalProcessorTime;
                        if ((now - lastSample).TotalMilliseconds >= thresholdMs &&
                            (cpu - lastCpu).TotalMilliseconds < 100)
                        {
                            // Process hasn't used meaningful CPU in threshold window
                            return false;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return true;
        }

        private static List<(Process proc, TimeSpan cpu, DateTime sample)> GetProcessTreeSnapshot(Process parent)
        {
            var list = new List<(Process, TimeSpan, DateTime)>();
            try
            {
                var now = DateTime.UtcNow;
                list.Add((parent, parent.TotalProcessorTime, now));

                // Get children by checking processes with a PPID matching our parent
                int parentId = parent.Id;
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        using var p = proc;
                        if (p.Id == parentId) continue;
                        // Check if this process is a child of the parent
                        // We do this by checking process name similarity or
                        // by examining if they share the same process tree
                        // Simple heuristic: check start time proximity & same session
                        if (Math.Abs((p.StartTime - parent.StartTime).TotalSeconds) < 120 &&
                            p.SessionId == parent.SessionId)
                        {
                            list.Add((proc, proc.TotalProcessorTime, now));
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }
    }

    // ── Confidence Scoring ──────────────────────────────────────

    internal static class Confidence
    {
        public static int Generate(string displayName, string folderName)
        {
            if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(folderName)) return 0;

            // Exact match or display name starts with folder name
            if (displayName.Equals(folderName, StringComparison.OrdinalIgnoreCase)) return 100;
            if (displayName.StartsWith(folderName, StringComparison.OrdinalIgnoreCase)) return 90;
            if (folderName.StartsWith(displayName, StringComparison.OrdinalIgnoreCase)) return 85;

            // Remove common words and compare
            string cleanDisplay = CleanName(displayName);
            string cleanFolder = CleanName(folderName);

            if (string.IsNullOrEmpty(cleanDisplay) || string.IsNullOrEmpty(cleanFolder)) return 0;
            if (cleanDisplay.Equals(cleanFolder, StringComparison.OrdinalIgnoreCase)) return 80;

            // Check if all words in folder name appear in display name
            var words = cleanFolder.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
            int matchCount = words.Count(w => cleanDisplay.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
            if (matchCount == words.Length && words.Length > 0) return 75;

            // Sift4 string distance
            int dist = Sift4Distance(cleanDisplay.ToLowerInvariant(), cleanFolder.ToLowerInvariant(), 5);
            int maxLen = Math.Max(cleanDisplay.Length, cleanFolder.Length);
            if (maxLen == 0) return 0;
            double ratio = 1.0 - (double)dist / maxLen;

            if (ratio >= 0.8) return 70;
            if (ratio >= 0.6) return 60;
            if (ratio >= 0.4) return 40;

            return 0;
        }

        private static string CleanName(string name)
        {
            name = Regex.Replace(name, @"\s+(Inc|LLC|Ltd|Limited|Corp|Corporation|GmbH|SAS|SRL|SA|Pty|Ltee)\.?$", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\s*\([^)]*\)$", "");
            name = Regex.Replace(name, @"[™©®]", "");
            name = Regex.Replace(name, @"\s+", " ").Trim();
            return name;
        }

        private static int Sift4Distance(string s1, string s2, int maxOffset)
        {
            if (string.IsNullOrEmpty(s1)) return s2?.Length ?? 0;
            if (string.IsNullOrEmpty(s2)) return s1.Length;

            int l1 = s1.Length, l2 = s2.Length;
            int c1 = 0, c2 = 0, lcss = 0, localCs = 0;
            int trans = 0;

            while (c1 < l1 && c2 < l2)
            {
                if (s1[c1] == s2[c2])
                {
                    localCs++;
                }
                else
                {
                    lcss += localCs;
                    localCs = 0;
                    if (c1 != c2) trans++;
                }
                c1++;
                c2++;
            }
            lcss += localCs;
            return (int)Math.Round((double)(Math.Max(l1, l2) - lcss + trans));
        }
    }
}
