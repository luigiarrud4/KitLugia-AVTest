using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
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
            // Heuristic items: found in post-scan but NOT in pre-scan baseline (lower confidence)
            public List<string> HeuristicFiles { get; set; } = new();
            public List<string> HeuristicRegistry { get; set; } = new();
            // Pre-scan baseline snapshot (how many items were detected before uninstall)
            public int BaselineFileCount { get; set; }
            public int BaselineRegistryCount { get; set; }
            public int FilesDeleted { get; set; }
            public int RegistryDeleted { get; set; }
            public List<string> Errors { get; set; } = new();
            public bool ForceDeleteUsed { get; set; }
            public List<string> BackupFiles { get; set; } = new();
            public List<string> BackupRegistryFiles { get; set; } = new();
            public string? DeletionLogFile { get; set; }
            // Data-folder markers captured pre-uninstall (Revo ADCU/ADAU pattern):
            // exact AppData/ProgramData/etc paths that existed BEFORE the uninstaller ran.
            public List<string> DataFoldersCaptured { get; set; } = new();

            // Byte-level baseline snapshots captured during the uninstall phase and used
            // by the scan phase (captureBaseline flows). Internal � not surfaced to UI.
            internal HashSet<string>? BaselineFileSet { get; set; }
            internal HashSet<string>? BaselineRegSet { get; set; }
        }
        private static readonly string FileBackupDir = Path.Combine(DeepUninstallSettings.PersistentRoot, "FileBackup");
        private static readonly string DeletionLogDir = Path.Combine(DeepUninstallSettings.PersistentRoot, "Logs");

        public static void KillProcessesForApp(string displayName, string installLocation = "", List<string>? extraExeNames = null)
        {
            var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var killedIds = new HashSet<int>();

            if (!string.IsNullOrEmpty(displayName))
            {
                targetNames.Add(SanitizeName(displayName));
                string compressed = NativeRegex.Replace(displayName, @"[\s\-_.]+", "");
                if (compressed.Length >= 3) targetNames.Add(compressed);
            }

            if (!string.IsNullOrEmpty(installLocation))
            {
                try
                {
                    string dir = Path.GetFileName(installLocation.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(dir)) targetNames.Add(dir);
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                        finally { proc.Dispose(); }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static async Task<UninstallResult> DeepUninstallProgram(string displayName, string uninstallString, string installLocation, string publisher, string displayIcon, bool createRestorePoint = true, IProgress<string>? progress = null, bool captureBaseline = true)
        {
            var result = new UninstallResult();
            await RunUninstallPhaseAsync(result, displayName, uninstallString, installLocation, publisher, displayIcon, createRestorePoint, progress, captureBaseline).ConfigureAwait(false);
            await RunScanPhaseAsync(result, displayName, uninstallString, installLocation, publisher, displayIcon, captureBaseline, progress).ConfigureAwait(false);
            return result;
        }

        /// <summary>
        /// Fase 1 � desinstalacao. Rapida: restore point, kill de processos, snapshot de
        /// baseline (se captureBaseline) e execucao do uninstaller. NAO escaneia residuos.
        /// O escaneamento pesado fica em <see cref="RunScanPhaseAsync"/>, que pode rodar em
        /// background (BackgroundTaskTracker) enquanto o usuario desinstala outros apps.
        /// </summary>
        public static async Task<UninstallResult> RunUninstallPhaseAsync(UninstallResult result, string displayName, string uninstallString, string installLocation, string publisher, string displayIcon, bool createRestorePoint = true, IProgress<string>? progress = null, bool captureBaseline = true)
        {
            progress?.Report("Preparando...");
            if (createRestorePoint)
                TryCreateRestorePoint($"KitLugia: Uninstall {displayName}");

            if (DeepUninstallSettings.KillProcessesBeforeUninstall)
            {
                progress?.Report("Encerrando processos do aplicativo...");
                KillProcessesWithTree(displayName, installLocation);
            }

            HashSet<string>? baselineFiles = null;
            HashSet<string>? baselineReg = null;
            if (captureBaseline)
            {
                // Pre-scan baseline (true Revo Uninstaller pattern): snapshot ALL
                // directories in key file locations BEFORE the uninstaller ran.
                progress?.Report("Snapshot de diret�rios (pr�-instala��o)...");
                baselineFiles = SnapshotKeyFileLocations(installLocation);
                baselineReg = new HashSet<string>(ScanLeftoverRegistry(displayName, installLocation), StringComparer.OrdinalIgnoreCase);
                result.BaselineFileCount = baselineFiles.Count;
                result.BaselineRegistryCount = baselineReg.Count;
            }
            result.BaselineFileSet = baselineFiles;
            result.BaselineRegSet = baselineReg;

            progress?.Report("Localizando desinstalador...");
            string? actualUninstaller = FindInstalledUninstaller(installLocation, uninstallString);
            string effectiveUninstall = actualUninstaller ?? uninstallString;
            bool foundActualUninstaller = actualUninstaller != null;

            if (!string.IsNullOrEmpty(effectiveUninstall))
            {
                try
                {
                    progress?.Report("Executando desinstala��o...");
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
                                    result.Errors.Add("Uninstall string points to main app exe � no silent uninstall available. Force-deleting.");
                                    try { proc.Kill(); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
                                result.Errors.Add("Uninstaller still open � close it when done.");
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

            progress?.Report("Desinstala��o conclu�da. Escanear res�duos agora.");
            return result;
        }

        /// <summary>
        /// Fase 2 � escaneamento de residuos (arquivos + registro + tarefas + env).
        /// Pesado; pode rodar em background (BackgroundTaskTracker) sem travar a UI.
        /// Reutiliza o baseline capturado em <see cref="RunUninstallPhaseAsync"/> (ou roda
        /// em modo confirmado se captureBaseline=false). Preenche o mesmo <paramref name="result"/>.
        /// </summary>
        /// <summary>
        /// Per-stage timing helper for the post-uninstall scan (tuning aid). Logs
        /// [SCAN-TIMING] lines via the same Logger the app reads, so the user can
        /// re-run a scan and report which stage actually dominates before we optimize.
        /// </summary>
        private static void LogScanStage(string section, Stopwatch sw, int? count = null)
        {
            string countSuffix = count == null ? "" : $" ({count.Value} itens)";
            Logger.Log($"[SCAN-TIMING] {section}: {sw.ElapsedMilliseconds} ms{countSuffix}");
        }

        public static async Task<UninstallResult> RunScanPhaseAsync(UninstallResult result, string displayName, string uninstallString, string installLocation, string publisher, string displayIcon, bool captureBaseline = true, IProgress<string>? progress = null)
        {
            var totalWatch = Stopwatch.StartNew();

            var stage = Stopwatch.StartNew();
            DateTime? installDate = GetInstallDateFromRegistry(displayName);
            LogScanStage("GetInstallDateFromRegistry", stage);

            progress?.Report("Escaneando res�duos de arquivos...");
            stage.Restart();
            var fileSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            fileSet.UnionWith(ScanLeftoverFiles(displayName, installLocation, displayIcon, uninstallString, publisher, installDate));
            LogScanStage("ScanLeftoverFiles", stage, fileSet.Count);

            // installLocation faltando (registro n�o fornece) ORIGINA o problema do review:
            // o scan de registro por path precisava dele. Inferir dos arquivos j� achados.
            if (string.IsNullOrEmpty(installLocation))
            {
                string? inferred = ResolveInstallLocation(displayName, fileSet, publisher);
                if (!string.IsNullOrEmpty(inferred))
                {
                    Logger.Log($"[INST-INFER] installLocation vazio ? inferido dos leftovers: {inferred}");
                    installLocation = inferred;
                }
            }

            // Revo ADCU/ADAU pattern: data folders under the user/all-users profile dirs
            // are NOT scanned blindly after uninstall. Instead they are captured by name
            // BEFORE/at uninstall time and only included if they still exist.
            stage.Restart();
            foreach (var dir in CaptureDataFoldersForApp(displayName, publisher))
            {
                if (Directory.Exists(dir))
                {
                    fileSet.Add(dir);
                    result.DataFoldersCaptured.Add(dir);
                }
            }
            LogScanStage("CaptureDataFoldersForApp", stage, result.DataFoldersCaptured.Count);

            progress?.Report("Escaneando res�duos do registro...");
            stage.Restart();
            var regSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            regSet.UnionWith(ScanLeftoverRegistry(displayName, installLocation));
            LogScanStage("ScanLeftoverRegistry", stage, regSet.Count);

            // Additional scans: scheduled tasks + env vars
            progress?.Report("Escaneando tarefas agendadas...");
            stage.Restart();
            ScanScheduledTasks(displayName, installLocation, "", fileSet, regSet);
            LogScanStage("ScanScheduledTasks", stage);

            progress?.Report("Escaneando vari�veis de ambiente...");
            stage.Restart();
            ScanUserEnvironmentVars(displayName, installLocation, regSet);
            LogScanStage("ScanUserEnvironmentVars", stage);

            var baselineFiles = result.BaselineFileSet;
            var baselineReg = result.BaselineRegSet;
            if (captureBaseline && baselineFiles != null && baselineReg != null)
            {
                // DIFF: confirmed = was in baseline AND still exists after uninstall
                // heuristic = only found after uninstall (lower confidence)
                var confirmedFiles = new HashSet<string>(fileSet, StringComparer.OrdinalIgnoreCase);
                confirmedFiles.IntersectWith(baselineFiles);
                var heuristicFiles = new HashSet<string>(fileSet, StringComparer.OrdinalIgnoreCase);
                heuristicFiles.ExceptWith(baselineFiles);

                var confirmedReg = new HashSet<string>(regSet, StringComparer.OrdinalIgnoreCase);
                confirmedReg.IntersectWith(baselineReg);
                var heuristicReg = new HashSet<string>(regSet, StringComparer.OrdinalIgnoreCase);
                heuristicReg.ExceptWith(baselineReg);

                result.LeftoverFiles = confirmedFiles.OrderBy(f => f).ToList();
                result.LeftoverRegistry = confirmedReg.OrderBy(r => r).ToList();
                result.HeuristicFiles = heuristicFiles.OrderBy(f => f).ToList();
                result.HeuristicRegistry = heuristicReg.OrderBy(r => r).ToList();
            }
            else
            {
                // Sem baseline (captureBaseline=false): todo o p�s-scan � tratado como
                // res�duo confirmado � mais leve e r�pido, sem snapshot global.
                result.LeftoverFiles = fileSet.OrderBy(f => f).ToList();
                result.LeftoverRegistry = regSet.OrderBy(r => r).ToList();
                result.BaselineFileCount = result.LeftoverFiles.Count;
                result.BaselineRegistryCount = result.LeftoverRegistry.Count;
            }

            if (!result.UninstallSuccess && result.LeftoverFiles.Count == 0 && result.LeftoverRegistry.Count == 0 && result.HeuristicFiles.Count == 0 && result.HeuristicRegistry.Count == 0)
                result.Errors.Add("Uninstall may have failed � no leftovers detected.");

            LogScanStage("RunScanPhaseAsync TOTAL", totalWatch);
            return await Task.FromResult(result);
        }

        /// <summary>
        /// Full snapshot of top-level directories in key file system locations.
        /// No confidence matching � captures everything for true Revo-style pre/post diff.
        /// </summary>
        private static HashSet<string> SnapshotKeyFileLocations(string installLocation)
        {
            var snapshot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
                snapshot.Add(installLocation);

            string[] searchDirs = {
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };

            foreach (var dir in searchDirs)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                        snapshot.Add(sub);
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            return snapshot;
        }

        /// <summary>
        /// P�s-scan LEVE e focado para apps UWP/AppX: verifica apenas as localiza��es
        /// determin�sticas do pacote (Packages, alias de execu��o, registro AppModel),
        /// em vez da varredura Revo-style global que � lenta e gera falsos positivos.
        /// </summary>
        public static (List<string> Files, List<string> Registry) ScanUwpLeftovers(string packageFullName, string displayName)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reg = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(packageFullName))
                return (new List<string>(), new List<string>());

            string baseName = packageFullName.Split('_')[0];
            if (baseName.Length < 3)
                return (new List<string>(), new List<string>());

            // 1) Pasta de dados em %LocalAppData%\Packages
            try
            {
                string packagesRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
                if (Directory.Exists(packagesRoot))
                {
                    foreach (var dir in Directory.EnumerateDirectories(packagesRoot, baseName + "*"))
                        files.Add(dir);
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            // 2) Alias de execu��o (stub .exe) em WindowsApps
            try
            {
                string stub = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", baseName + ".exe");
                if (File.Exists(stub)) files.Add(stub);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            // 3) Registro: AppModel Repository (HKCU + HKLM) e PackageRoot
            string[] repos = {
                @"HKEY_CURRENT_USER\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages",
                @"HKEY_CURRENT_USER\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\PackageRoot"
            };
            foreach (var repo in repos)
            {
                try
                {
                    var hive = ResolveHive(repo, out string subKey);
                    if (hive == null) continue;
                    using var key = hive.OpenSubKey(subKey);
                    if (key == null) continue;
                    foreach (var name in key.GetSubKeyNames())
                    {
                        if (name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith(packageFullName, StringComparison.OrdinalIgnoreCase))
                            reg.Add(repo + "\\" + name);
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            // 4) Provisioned AppxAllUserStore (HKLM)
            const string allUserStore = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications";
            try
            {
                var hive = ResolveHive(allUserStore, out string subKey);
                if (hive != null)
                {
                    using var key = hive.OpenSubKey(subKey);
                    if (key != null)
                    {
                        foreach (var name in key.GetSubKeyNames())
                        {
                            if (name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                                reg.Add(allUserStore + "\\" + name);
                        }
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            return (files.OrderBy(f => f).ToList(), reg.OrderBy(r => r).ToList());
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

            // 2. Scan for file leftovers (includes install dir via ScanLeftoverFiles)
            var fileSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(installLocation))
                fileSet.Add(installLocation);
            fileSet.UnionWith(ScanLeftoverFiles(displayName, installLocation, displayIcon, "", publisher));

            // Revo ADCU/ADAU pattern (same as DeepUninstallProgram)
            foreach (var dir in CaptureDataFoldersForApp(displayName, publisher))
            {
                if (Directory.Exists(dir))
                {
                    fileSet.Add(dir);
                    result.DataFoldersCaptured.Add(dir);
                }
            }

            // installLocation faltando (registro n�o fornece): inferir dos arquivos j� achados
            if (string.IsNullOrEmpty(installLocation))
            {
                string? inferred = ResolveInstallLocation(displayName, fileSet, publisher);
                if (!string.IsNullOrEmpty(inferred))
                {
                    Logger.Log($"[INST-INFER] installLocation vazio ? inferido dos leftovers: {inferred}");
                    installLocation = inferred;
                }
            }

            // 3. Scan for registry leftovers
            var regSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            regSet.UnionWith(ScanLeftoverRegistry(displayName, installLocation));

            // 4. Additional scans: scheduled tasks + env vars
            ScanScheduledTasks(displayName, installLocation, "", fileSet, regSet);
            ScanUserEnvironmentVars(displayName, installLocation, regSet);

            result.LeftoverFiles = fileSet.OrderBy(f => f).ToList();
            result.LeftoverRegistry = regSet.OrderBy(r => r).ToList();

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
            PerformCleanup(result.LeftoverFiles, result.LeftoverRegistry, result, displayName, installLocation ?? "");

            return result;
        }

        public static (List<ScanEntry> files, List<ScanEntry> registry) ScanLeftovers(string displayName, string publisher, ScannerMode mode = ScannerMode.Moderate)
        {
            string installLocation = GetInstallLocationFromRegistry(displayName) ?? "";
            DateTime? installDate = GetInstallDateFromRegistry(displayName);
            var rawFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rawReg = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            rawFiles.UnionWith(ScanLeftoverFiles(displayName, installLocation, "", "", publisher, installDate, mode));

            // installLocation faltando: inferir dos arquivos j� achados antes do scan de registro
            if (string.IsNullOrEmpty(installLocation))
            {
                string? inferred = ResolveInstallLocation(displayName, rawFiles, publisher);
                if (!string.IsNullOrEmpty(inferred))
                {
                    Logger.Log($"[INST-INFER] installLocation vazio ? inferido dos leftovers: {inferred}");
                    installLocation = inferred;
                }
            }

            rawReg.UnionWith(ScanLeftoverRegistry(displayName, installLocation, mode));

            // Additional scans: scheduled tasks + env vars
            ScanScheduledTasks(displayName, installLocation, publisher, rawFiles, rawReg);
            ScanUserEnvironmentVars(displayName, installLocation, rawReg);

            var files = rawFiles.Select(f => new ScanEntry { Path = f, Safety = ClassifyFileSafety(displayName, installLocation, f) }).OrderBy(e => e.Path).ToList();
            var reg = rawReg.Select(r => new ScanEntry { Path = r, Safety = ClassifyRegistrySafety(displayName, installLocation, r) }).OrderBy(e => e.Path).ToList();
            return (files, reg);
        }

        public static CleanupSafety ClassifyFileSafety(string displayName, string installLocation, string path)
        {
            if (string.IsNullOrEmpty(path)) return CleanupSafety.Uncertain;

            // Inside install location ? Safe
            if (!string.IsNullOrEmpty(installLocation) &&
                path.StartsWith(installLocation, StringComparison.OrdinalIgnoreCase))
                return CleanupSafety.Safe;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            // Local AppData with exact name match ? Safe
            if (!string.IsNullOrEmpty(localAppData) &&
                path.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase))
            {
                string relative = path[localAppData.Length..].TrimStart('\\');
                if (relative.StartsWith(displayName, StringComparison.OrdinalIgnoreCase) ||
                    relative.Split('\\').Any(p => p.Equals(displayName, StringComparison.OrdinalIgnoreCase)))
                    return CleanupSafety.Safe;
                return CleanupSafety.Moderate;
            }

            // Roaming AppData with exact name match ? Safe / otherwise Moderate
            if (!string.IsNullOrEmpty(roamingAppData) &&
                path.StartsWith(roamingAppData, StringComparison.OrdinalIgnoreCase))
            {
                string relative = path[roamingAppData.Length..].TrimStart('\\');
                if (relative.StartsWith(displayName, StringComparison.OrdinalIgnoreCase) ||
                    relative.Split('\\').Any(p => p.Equals(displayName, StringComparison.OrdinalIgnoreCase)))
                    return CleanupSafety.Safe;
                return CleanupSafety.Moderate;
            }

            // ProgramData ? Uncertain (shared data)
            if (!string.IsNullOrEmpty(programData) &&
                path.StartsWith(programData, StringComparison.OrdinalIgnoreCase))
                return CleanupSafety.Uncertain;

            // Path contains display name ? Moderate
            if (path.IndexOf(displayName, StringComparison.OrdinalIgnoreCase) >= 0)
                return CleanupSafety.Moderate;

            // Unknown ? Uncertain
            return CleanupSafety.Uncertain;
        }

        public static CleanupSafety ClassifyRegistrySafety(string displayName, string installLocation, string path)
        {
            if (string.IsNullOrEmpty(path)) return CleanupSafety.Uncertain;

            // Uninstall key ? Safe
            if (path.Contains("\\Uninstall\\", StringComparison.OrdinalIgnoreCase) &&
                path.IndexOf(displayName, StringComparison.OrdinalIgnoreCase) >= 0)
                return CleanupSafety.Safe;

            // HKCU\Software\AppName ? Safe
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

            // Classes\AppName ? Moderate
            if (path.IndexOf("\\Classes\\", StringComparison.OrdinalIgnoreCase) >= 0 &&
                path.IndexOf(displayName, StringComparison.OrdinalIgnoreCase) >= 0)
                return CleanupSafety.Moderate;

            // Installer components referencing installLocation ? Safe
            if (!string.IsNullOrEmpty(installLocation) &&
                path.Contains("\\Installer\\", StringComparison.OrdinalIgnoreCase))
                return CleanupSafety.Safe;

            // Name appears in path ? Moderate
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

        // KitLugia's own install path � skip these files/folders during deletion
        private static readonly string KitLugiaInstallPath = GetKitLugiaPath();

        private static string GetKitLugiaPath()
        {
            try
            {
                string? loc = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(loc))
                    return Path.GetDirectoryName(loc)?.TrimEnd('\\') ?? "";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return AppContext.BaseDirectory.TrimEnd('\\');
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
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
                // Desktop removed � user profile Desktop folders are too prone to false positives
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

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

        // Shared vendor runtime folders under ProgramData that are never app leftovers
        private static readonly HashSet<string> SharedVendorRuntimeFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            "NGX", "RTXDI", "Streamline"
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
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
            return results.OrderBy(f => f).ToList();
        }

        // -- Scanning ---------------------------------------------

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

        private static List<string> ScanLeftoverFiles(string displayName, string installLocation, string displayIcon, string uninstallString, string publisher = "", DateTime? installDate = null, ScannerMode mode = ScannerMode.Moderate)
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stage = Stopwatch.StartNew();

            // Safety guard: refuse to scan with very short/generic names (protect against Sift4 false positives)
            if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length < 3)
                return results.ToList();

            List<string>? otherInstallLocations = mode == ScannerMode.Advanced ? null : GetAllInstallLocations(excludeName: displayName);

            // InstallLocation � add the dir AND enumerate its contents (exe, dll, etc.)
            if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
            {
                results.Add(installLocation);
                try
                {
                    foreach (var f in Directory.EnumerateFiles(installLocation, "*", System.IO.SearchOption.AllDirectories))
                        results.Add(f);
                    foreach (var d in Directory.EnumerateDirectories(installLocation, "*", System.IO.SearchOption.AllDirectories))
                        results.Add(d);
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
            LogScanStage("  InstallLocation walk", stage);

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
                ScanFolderConfidence(dir, displayName, publisher, installDate, results, otherInstallLocations, depth: 0, maxDepth: maxDepth);
            }
            LogScanStage("  ScanFolderConfidence global", stage);

            // Temp � shallow
            string temp = Path.GetTempPath().TrimEnd('\\');
            if (!string.IsNullOrEmpty(temp) && Directory.Exists(temp))
                ScanFolderConfidence(temp, displayName, publisher, installDate, results, otherInstallLocations, depth: 0, maxDepth: 1);
            LogScanStage("  Temp shallow", stage);

            // Prefetch via sorted executables (BCU pattern)
            ScanPrefetchByExe(installLocation, displayIcon, uninstallString, results);

            // Uninstaller-specific leftovers
            ScanUninstallerSpecific(installLocation, displayIcon, displayName, results);

            // Startup folder entries
            ScanStartupFolders(displayName, results);

            // Start Menu shortcuts (.lnk files)
            ScanStartMenuShortcuts(displayName, publisher, results);
            ScanDesktopShortcuts(displayName, publisher, results);

            // WER reports via sorted executables (BCU pattern)
            ScanWerReports(installLocation, displayName, displayIcon, uninstallString, results);

            LogScanStage("  Prefetch/Uninstaller/Startup/Menus/WER", stage);

            // Empty dir + questionable name detection (BCUninstaller pattern)
            if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
                ScanEmptyAndQuestionableDirs(installLocation, displayName, results);
            foreach (var dir in BuildFileSearchDirs())
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    ScanEmptyAndQuestionableDirs(dir, displayName, results);
            }
            LogScanStage("  EmptyAndQuestionableDirs", stage);

            // BCU TestForSimilarNames: remove results that match another app's name better
            if (results.Count > 0)
            {
                var otherNames = GetAllAppDisplayNames(excludeName: displayName);
                if (otherNames.Count > 0)
                {
                    var toRemove = new List<string>();
                    foreach (var path in results)
                    {
                        string leafName = Path.GetFileNameWithoutExtension(path) ?? Path.GetFileName(path) ?? "";
                        if (string.IsNullOrEmpty(leafName) || leafName.Length < 3) continue;

                        int targetConf = Confidence.Generate(displayName, leafName, publisher);
                        foreach (var other in otherNames)
                        {
                            int otherConf = Confidence.Generate(other, leafName);
                            if (otherConf > targetConf)
                            {
                                toRemove.Add(path);
                                break;
                            }
                        }
                    }
                    foreach (var r in toRemove)
                        results.Remove(r);
                }
            }

            // Apply user-defined exclusions (Revo RegExclude equivalent)
            // GetUserExclusions() loads from the registry first � the in-memory list
            // may be empty on the first scan of a session even when saved exclusions exist.
            var exclusions = GetUserExclusions();
            if (results.Count > 0 && exclusions.Count > 0)
            {
                var toRemove = results.Where(IsExcludedPath).ToList();
                foreach (var r in toRemove)
                    results.Remove(r);
            }

            // Revo-style "ignore items accessed < 24h ago" filter (optional, off by default)
            if (DeepUninstallSettings.IgnoreRecent24H && results.Count > 0)
            {
                var cut = DateTime.UtcNow.AddHours(-24);
                foreach (var p in results.ToList())
                {
                    try
                    {
                        bool recent;
                        if (Directory.Exists(p)) recent = Directory.GetLastAccessTimeUtc(p) > cut;
                        else if (File.Exists(p)) recent = File.GetLastAccessTimeUtc(p) > cut;
                        else recent = false;
                        if (recent) results.Remove(p);
                    }
                    catch { }
                }
            }

            LogScanStage("  TestForSimilarNames/Exclusions", stage);
            return results.ToList();
        }

        /// <summary>
        /// Revo ADCU/ADAU pattern: capture the app's data folders by NAME at uninstall time,
        /// instead of blind-scanning all of AppData after the fact. Only top-level dirs whose
        /// leaf name matches displayName/publisher are considered (no deep walk � cheap and precise).
        /// Folders that no longer exist after the uninstaller ran are naturally skipped by the caller.
        /// </summary>
        private static List<string> CaptureDataFoldersForApp(string displayName, string publisher)
        {
            var captured = new List<string>();
            if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length < 3)
                return captured;

            var roots = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),      // Roaming
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), // Local
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) // ProgramData
            };

            var tokens = new List<string>();
            foreach (var t in new[] { displayName, publisher })
            {
                if (string.IsNullOrWhiteSpace(t)) continue;
                var trimmed = t.Trim();
                if (trimmed.Length < 3) continue;

                // Safety: a generic mega-publisher (Microsoft, Google, ...) must NEVER
                // be used as a name token � "Microsoft" inside a leaf name would match
                // %AppData%\Microsoft, shared by dozens of unrelated apps. Only the
                // app's own display name carries the discriminating power here.
                if (publisher != null &&
                    t.Equals(publisher, StringComparison.OrdinalIgnoreCase) &&
                    GenericPublishers.Contains(trimmed))
                    continue;

                tokens.Add(trimmed);
            }

            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        string leaf = Path.GetFileName(dir) ?? "";
                        if (string.IsNullOrEmpty(leaf) || leaf.Length < 3) continue;
                        if (TokensMatch(leaf, tokens))
                            captured.Add(dir);
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            return captured.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool TokensMatch(string leaf, List<string> tokens)
        {
            foreach (var token in tokens)
            {
                if (leaf.Equals(token, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (leaf.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (token.StartsWith(leaf, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (leaf.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        // Revo "Exclude" equivalent: persistent user-defined skip list (registry-backed),
        // applied both during scanning and deletion. Paths/substrings (case-insensitive).
        private static readonly List<string> _userExclusions = new();
        private static bool _exclusionsLoaded = false;
        private const string ExclusionsRegPath = @"Software\KitLugia\DeepUninstall";
        private const string ExclusionsRegValue = "Exclude";

        public static List<string> GetUserExclusions()
        {
            LoadExclusionsIfNeeded();
            return _userExclusions.ToList();
        }

        public static void AddUserExclusion(string pathOrSubstring)
        {
            if (string.IsNullOrWhiteSpace(pathOrSubstring)) return;
            LoadExclusionsIfNeeded();
            var norm = pathOrSubstring.Trim().TrimEnd('\\');
            if (!_userExclusions.Contains(norm, StringComparer.OrdinalIgnoreCase))
            {
                _userExclusions.Add(norm);
                SaveExclusions();
            }
        }

        public static bool RemoveUserExclusion(string pathOrSubstring)
        {
            LoadExclusionsIfNeeded();
            bool removed = _userExclusions.RemoveAll(e => e.Equals(pathOrSubstring, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) SaveExclusions();
            return removed;
        }

        public static void ClearUserExclusions()
        {
            _userExclusions.Clear();
            SaveExclusions();
        }

        private static void LoadExclusionsIfNeeded()
        {
            if (_exclusionsLoaded) return;
            lock (_userExclusions)
            {
                if (_exclusionsLoaded) return;
                _userExclusions.Clear();
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(ExclusionsRegPath);
                    if (key != null)
                    {
                        string[] values = key.GetValue(ExclusionsRegValue, Array.Empty<string>()) switch
                        {
                            string[] arr => arr,
                            string s => new[] { s },
                            _ => Array.Empty<string>()
                        };
                        foreach (var v in values)
                            if (!string.IsNullOrWhiteSpace(v))
                                _userExclusions.Add(v.Trim().TrimEnd('\\'));
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                _exclusionsLoaded = true;
            }
        }

        private static void SaveExclusions()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(ExclusionsRegPath);
                key?.SetValue(ExclusionsRegValue, _userExclusions.ToArray(), RegistryValueKind.MultiString);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // Revo "Ignore files accessed in the last 24 hours" � used by scanners to skip
        // files/folders that are clearly still in use (recently touched by the OS).
        public static bool IsExcludedPath(string fullPath)
        {
            LoadExclusionsIfNeeded();
            if (_userExclusions.Count == 0) return false;
            foreach (var ex in _userExclusions)
            {
                if (fullPath.Equals(ex, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (fullPath.StartsWith(ex + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (ex.IndexOf('*') >= 0)
                {
                    // Simple wildcard: '*' matches any chars (basic glob support)
                    string pattern = "^" + System.Text.RegularExpressions.Regex.Escape(ex)
                        .Replace("\\*", ".*").Replace("\\?", ".") + "$";
                    if (System.Text.RegularExpressions.Regex.IsMatch(fullPath, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Scans Scheduled Tasks (TaskCache\Tree + Tasks\{GUID} + XML in System32\Tasks)
        /// that reference the displayName, installLocation, or publisher.
        /// Returns file paths (task XML) and registry paths (Tree + Tasks keys).
        /// </summary>
        private static void ScanScheduledTasks(string displayName, string installLocation, string publisher,
            HashSet<string> fileResults, HashSet<string> regResults)
        {
            try
            {
                using var treeKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree");
                if (treeKey == null) return;

                foreach (var taskName in treeKey.GetSubKeyNames())
                {
                    // Skip system tasks
                    if (taskName.StartsWith("Microsoft\\", StringComparison.OrdinalIgnoreCase) ||
                        taskName.StartsWith("\\Microsoft\\", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string leafName = taskName.Split('\\').Last();
                    int conf = Confidence.Generate(displayName, leafName, publisher);
                    if (conf < 70 && !string.IsNullOrEmpty(publisher)) conf = Confidence.Generate(publisher, leafName);
                    if (conf < 70) continue;

                    using var taskSubKey = treeKey.OpenSubKey(taskName);
                    if (taskSubKey == null) continue;

                    // Add Tree key to registry results
                    string treeRegPath = $@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\{taskName}";
                    regResults.Add(treeRegPath);

                    // Get the task GUID to find the Tasks\{GUID} key and the XML file
                    if (taskSubKey.GetValue("Id") is string taskGuid && !string.IsNullOrEmpty(taskGuid))
                    {
                        string tasksRegPath = $@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks\{taskGuid}";
                        regResults.Add(tasksRegPath);

                        // Task XML file in System32\Tasks (also SysWOW64 on 64-bit)
                        string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                        string taskXml = Path.Combine(system32, "Tasks", taskName);
                        if (File.Exists(taskXml)) fileResults.Add(taskXml);

                        // Check SysWOW64 on 64-bit OS
                        if (Environment.Is64BitOperatingSystem)
                        {
                            string syswow64 = system32.Replace("System32", "SysWOW64");
                            string wowTaskXml = Path.Combine(syswow64, "Tasks", taskName);
                            if (File.Exists(wowTaskXml)) fileResults.Add(wowTaskXml);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("ScanScheduledTasks", ex.Message);
            }
        }

        /// <summary>
        /// Scans user environment variables (HKCU\Environment) for entries
        /// matching the app name or install location.
        /// </summary>
        private static void ScanUserEnvironmentVars(string displayName, string installLocation, HashSet<string> regResults)
        {
            try
            {
                using var envKey = Registry.CurrentUser.OpenSubKey("Environment");
                if (envKey == null) return;

                string? pathValue = envKey.GetValue("PATH") as string;

                foreach (var name in envKey.GetValueNames())
                {
                    if (string.IsNullOrEmpty(name) || name == "PATH") continue;

                    if (name.IndexOf(displayName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        regResults.Add($@"HKEY_CURRENT_USER\Environment\{name}");
                        continue;
                    }

                    string? val = envKey.GetValue(name) as string;
                    if (!string.IsNullOrEmpty(val) && !string.IsNullOrEmpty(installLocation) &&
                        val.IndexOf(installLocation, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        regResults.Add($@"HKEY_CURRENT_USER\Environment\{name}");
                    }
                }

                // Check PATH entries referencing the install location
                if (!string.IsNullOrEmpty(pathValue) && !string.IsNullOrEmpty(installLocation))
                {
                    var parts = pathValue.Split(';', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Any(p => p.IndexOf(installLocation, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        regResults.Add(@"HKEY_CURRENT_USER\Environment\PATH");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("ScanUserEnvironmentVars", ex.Message);
            }
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

                // Public shell roots: never delete the shared Public folder or its shell roots
                // (individual items inside are OK - per-app shortcuts are legitimately removable)
                string commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                if (string.IsNullOrEmpty(commonDesktop))
                    commonDesktop = Path.Combine(Path.GetPathRoot(userProfile) ?? "C:\\", "Users", "Public", "Desktop");
                string publicRoot = Directory.GetParent(commonDesktop)?.FullName ?? commonDesktop;
                string[] publicShellRoots =
                {
                    publicRoot,
                    commonDesktop,
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonMusic),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPictures),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonVideos),
                    Path.Combine(publicRoot, "Downloads"),
                };
                foreach (var root in publicShellRoots)
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    if (normalized.Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                if (!normalized.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
                    return false;

                string relative = normalized[userProfile.Length..].TrimStart('\\');
                if (string.IsNullOrEmpty(relative))
                    return true; // exact user profile

                string[] parts = relative.Split('\\');

                // Depth 1 = direct child of user profile (Desktop, AppData, etc.) ? too broad
                if (parts.Length <= 1)
                    return true;

                // Depth 2 under AppData (AppData\Roaming, AppData\Local, AppData\LocalLow) ? too broad
                if (parts.Length == 2 && parts[0].Equals("AppData", StringComparison.OrdinalIgnoreCase))
                    return true;

                // Depth 2 under known shell roots (Desktop\file.lnk is depth 2 = OK � individual files)
                // Only block depth 1 for these
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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

            // SYSTEM hive: min 3 levels (SYSTEM\CurrentControlSet\Services) � 2 is too broad
            if (isSystemHive && keyPath.StartsWith("SYSTEM\\", StringComparison.OrdinalIgnoreCase) && keyDepth < 3)
                return true;

            // SOFTWARE hive: min 2 levels (SOFTWARE\AppName) � 1 is just "SOFTWARE" which is too broad
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
                        // Shared vendor runtimes under ProgramData are never app leftovers
                        // (Uninstall Tool #185: ProgramData\NVIDIA\NGX flagged for deletion)
                        if (parts.Length >= 2 && parts[0].Equals("NVIDIA", StringComparison.OrdinalIgnoreCase) &&
                            SharedVendorRuntimeFolders.Contains(parts[1]))
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
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string localLow = Path.Combine(user, "AppData", "LocalLow");
            string savedGames = Path.Combine(user, "Saved Games");
            string userStartMenu = Path.Combine(roamingAppData, "Microsoft", "Windows", "Start Menu", "Programs");
            string commonStartMenu = Path.Combine(programData, "Microsoft", "Windows", "Start Menu", "Programs");
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
                localLow, documents, savedGames, userStartMenu, commonStartMenu,
                virtualStore, userPrograms, localPrograms, publicPrograms,
                werArchive, werQueue, werLocalArchive, werLocalQueue
            };
        }

        // BCU DirectlyInsideKnownFolder: known user shell folders where direct children are suspicious
        private static readonly HashSet<string> KnownUserShellFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos",
            "Favorites", "Contacts", "Links", "Searches", "Saved Games",
            "OneDrive", "3D Objects", "Recorded Calls", "Camera Roll",
            "Screenshots", "Local", "LocalLow", "Roaming"
        };

        private static void ScanFolderConfidence(string baseDir, string displayName, string publisher, DateTime? installDate, HashSet<string> results, List<string>? otherInstallLocations, int depth, int maxDepth)
        {
            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(baseDir, "*", System.IO.SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                // Access denied while listing a single dir climbs up the search tree �
                // log once with the real reason (rate-limited by Logger) instead of
                // spamming "Exception suppressed" for every inaccessible folder.
                Logger.LogWarning("ScanFolderConfidence", $"Acesso negado ao listar '{baseDir}': {ex.Message}");
                return;
            }

            try
            {
                foreach (var dir in dirs)
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

                    // Cheap name-based gating FIRST: Confidence.Generate is pure
                    // string work, while ProbeFolderBinaries reads FileVersionInfo
                    // from real files. Only spend the probe when the folder name
                    // shares a meaningful token with the app name � renamed folders
                    // (e.g. "@zcodedesktop-updater" for zcode) still match through
                    // HasNameRelation, so content detection is preserved.
                    bool nameMatch = Confidence.Generate(displayName, dirName, publisher) >= 70;
                    if (!nameMatch && !string.IsNullOrEmpty(publisher))
                        nameMatch = Confidence.Generate(publisher, dirName) >= 70;

                    // Single pass reads FileVersionInfo + Authenticode at most once per
                    // executable (the old code re-read every exe twice: once for the
                    // content match, again for the unrelated-executables penalty).
                    (bool VerifiedMatch, bool HasUnrelated) probe;
                    if (nameMatch || HasNameRelation(displayName, dirName))
                        probe = ProbeFolderBinaries(dir, displayName, publisher);
                    else
                        probe = (false, false);
                    bool contentMatch = probe.VerifiedMatch;

                    bool match = nameMatch || contentMatch;

                    if (match)
                    {
                        // BCU DirectlyInsideKnownFolder penalty: items directly inside user shell folders
                        // (Desktop, Documents, Downloads, etc.) are more likely to be false positives
                        string? parentFolder = Path.GetFileName(Path.GetDirectoryName(dir));
                        if (!string.IsNullOrEmpty(parentFolder) && KnownUserShellFolders.Contains(parentFolder))
                        {
                            // Reduce confidence threshold for items directly in known shell folders
                            // Only accept if very strong match (>= 85) or confirmed by content
                            if (!contentMatch && Confidence.Generate(displayName, dirName, publisher) < 85 &&
                                (string.IsNullOrEmpty(publisher) || Confidence.Generate(publisher, dirName) < 85))
                                match = false;
                        }

                        // BCU ExecutablesArePresent penalty: if folder has executables that don't confirm the match
                        if (match && !contentMatch && probe.HasUnrelated)
                            match = false;

                        // BCU ItemNameEqualsCompanyName: if folder name matches publisher but not product name
                        if (match && !contentMatch && !string.IsNullOrEmpty(publisher) &&
                            !publisher.Equals(displayName, StringComparison.OrdinalIgnoreCase) &&
                            dirName.IndexOf(publisher, StringComparison.OrdinalIgnoreCase) >= 0 &&
                            Confidence.Generate(displayName, dirName, publisher) < 85)
                            match = false;

                        if (match)
                        {
                            // Date sanity check: if InstallDate is available and this folder
                            // was created before the install, it's unlikely to be a leftover
                            if (installDate.HasValue)
                            {
                                try
                                {
                                    DateTime dirCreated = Directory.GetCreationTime(dir);
                                    if (dirCreated < installDate.Value.AddDays(-1))
                                        match = false;
                                }
                                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                            }
                        }
                    }

                    if (match)
                    {
                        // Cross-reference: skip if another app uses this dir (BCU DirectoryStillUsed)
                        if (otherInstallLocations != null && otherInstallLocations.Any(loc => dir.StartsWith(loc, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        // Also skip dirs that are known Windows/System dirs
                        if (IsSystemFolder(dir))
                            continue;

                        results.Add(dir);
                    }

                    if (match && depth < maxDepth)
                        ScanFolderConfidence(dir, displayName, publisher, installDate, results, otherInstallLocations, depth + 1, maxDepth);
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        /// <summary>
        /// Single-pass binary probe: reads FileVersionInfo (and, only when a FileVersion
        /// match hasn't been found yet, the Authenticode signature) exactly once per
        /// file in the folder. Returns both the content-match verdict (used as the
        /// independent matching signal) and whether the folder contains executables
        /// that bear no relation to the app (the BCU ExecutablesArePresent penalty).
        /// This replaces two separate passes that each re-read every executable.
        /// </summary>
        private static (bool VerifiedMatch, bool HasUnrelated) ProbeFolderBinaries(string folderPath, string displayName, string publisher)
        {
            if (string.IsNullOrEmpty(publisher) && string.IsNullOrEmpty(displayName))
                return (false, false);

            // Cap how many executables get probed per folder. On huge folders
            // (node_modules, plugin dirs) reading FileVersionInfo for every single
            // exe/dll is the dominant cost of the whole leftover scan; a cap of ~12
            // files is more than enough to reach a verdict in the overwhelming
            // majority of folders and keeps the scan snappy.
            const int MaxProbeFiles = 12;
            int totalExecutables = 0;
            int probed = 0;

            bool hasExecutables = false;
            bool anyFviMatch = false;
            bool verifiedMatch = false;
            int fviFailures = 0;

            try
            {
                foreach (var file in Directory.EnumerateFiles(folderPath, "*", System.IO.SearchOption.TopDirectoryOnly))
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext != ".exe" && ext != ".dll" && ext != ".sys" && ext != ".ocx")
                        continue;

                    hasExecutables = true;
                    totalExecutables++;
                    if (probed >= MaxProbeFiles)
                        continue; // budget reached: rest stays unprobed

                    probed++;

                    bool fviMatch = false;
                    try
                    {
                        var fvi = FileVersionInfo.GetVersionInfo(file);
                        if (!string.IsNullOrEmpty(publisher) && fvi.CompanyName != null &&
                            fvi.CompanyName.IndexOf(publisher, StringComparison.OrdinalIgnoreCase) >= 0)
                            fviMatch = true;
                        else if (!string.IsNullOrEmpty(displayName) && fvi.ProductName != null &&
                                 fvi.ProductName.IndexOf(displayName, StringComparison.OrdinalIgnoreCase) >= 0)
                            fviMatch = true;
                    }
                    catch
                    {
                        // Access-denied / corrupted metadata on a single binary � not fatal.
                        fviFailures++;
                    }

                    if (fviMatch)
                    {
                        anyFviMatch = true;
                        verifiedMatch = true;
                        continue;
                    }

                    // Only run the expensive Authenticode check when the cheap
                    // FileVersionInfo lookup did not already confirm the match.
                    if (!verifiedMatch && !string.IsNullOrEmpty(publisher))
                    {
                        try
                        {
                            var cert = X509CertificateLoader.LoadCertificateFromFile(file);
                            if (cert != null && cert.Subject.IndexOf(publisher, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                verifiedMatch = true;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            if (fviFailures > 0)
                Logger.LogWarning("ProbeFolderBinaries",
                    $"FileVersionInfo falhou em {fviFailures} bin�rio(s) de '{Path.GetFileName(folderPath)}' (acesso negado/metadados corrompidos)");

            // HasUnrelated (the BCU penalty) is only asserted when every executable
            // in the folder was actually probed. When the probe budget cut the scan
            // short, a file we never read could still match � so we decline the
            // penalty instead of risking a false "unrelated" rejection.
            bool fullyProbedFolders = totalExecutables <= MaxProbeFiles;
            return (verifiedMatch, fullyProbedFolders && hasExecutables && !anyFviMatch);
        }

        private static bool HasNameRelation(string displayName, string dirName)
        {
            if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(dirName)) return false;

            // Cheap shared-token check that survives renames like
            // "@zcodedesktop-updater" -> zcode: split the app name into tokens and
            // see if any meaningful one (>= 3 chars, not a generic word) appears in
            // the folder name. Skip the expensive binary probe when there's zero
            // relation, which is the case for the vast majority of AppData folders.
            foreach (var token in displayName.Split(new[] { ' ', '-', '_', '.', '(', ')', '[', ']' },
                                                    StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length < 4) continue;
                if (ForbiddenScanFolderNames.Contains(token)) continue;
                if (dirName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
        }

        private static void ScanStartMenuShortcuts(string displayName, string publisher, HashSet<string> results)
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
                        if (Confidence.Generate(displayName, name, publisher) >= 70)
                            results.Add(f);
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
        }

        private static void ScanDesktopShortcuts(string displayName, string publisher, HashSet<string> results)
        {
            string commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

            if (!string.IsNullOrEmpty(commonDesktop) && Directory.Exists(commonDesktop))
            {
                try
                {
                    foreach (var f in Directory.GetFiles(commonDesktop, "*.lnk"))
                    {
                        string name = Path.GetFileNameWithoutExtension(f);
                        if (string.IsNullOrEmpty(name) || name.Length < 3) continue;
                        if (Confidence.Generate(displayName, name, publisher) >= 70)
                            results.Add(f);
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
        }

        /// <summary>
        /// BCU PrefetchScanner: scans %WINDIR%\Prefetch for .pf files whose executable name
        /// (the part before the dash) matches an executable from the install directory.
        /// </summary>
        private static void ScanPrefetchByExe(string installLocation, string displayIcon, string uninstallString, HashSet<string> results)
        {
            if (string.IsNullOrEmpty(installLocation) || !Directory.Exists(installLocation)) return;
            try
            {
                var exes = BuildSortedExecutables(installLocation, displayIcon, uninstallString);
                if (exes.Count == 0) return;

                var targetExeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var exe in exes)
                {
                    string fileName = Path.GetFileName(exe);
                    if (!string.IsNullOrEmpty(fileName)) targetExeNames.Add(fileName);
                }

                string sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.System).TrimEnd('\\');
                sysRoot = Directory.GetParent(sysRoot)?.FullName ?? sysRoot;
                string prefetchDir = Path.Combine(sysRoot, "Prefetch");
                if (!Directory.Exists(prefetchDir)) return;

                foreach (var pfFile in Directory.GetFiles(prefetchDir, "*.pf"))
                {
                    string fileName = Path.GetFileName(pfFile);
                    int dashIdx = fileName.LastIndexOf('-');
                    if (dashIdx < 0) continue;
                    string appExeName = fileName[..dashIdx];
                    if (!string.IsNullOrEmpty(appExeName) && targetExeNames.Contains(appExeName))
                        results.Add(pfFile);
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        /// <summary>
        /// Scans WER (Windows Error Reporting) ReportArchive and ReportQueue directories
        /// for AppCrash_ entries matching executables from the install directory (BCU pattern).
        /// </summary>
        private static void ScanWerReports(string installLocation, string displayName, string displayIcon, string uninstallString, HashSet<string> results)
        {
            if (string.IsNullOrEmpty(installLocation) || !Directory.Exists(installLocation)) return;
            try
            {
                var exes = BuildSortedExecutables(installLocation, displayIcon, uninstallString);
                if (exes.Count == 0) return;

                var appExeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var exe in exes)
                {
                    string name = Path.GetFileNameWithoutExtension(exe);
                    if (!string.IsNullOrEmpty(name)) appExeNames.Add(name);
                }

                string[] werRoots =
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\WER"),
                };

                const string crashLabel = "AppCrash_";
                foreach (var root in werRoots)
                {
                    foreach (var sub in new[] { "ReportArchive", "ReportQueue" })
                    {
                        string dir = Path.Combine(root, sub);
                        if (!Directory.Exists(dir)) continue;
                        foreach (var reportDir in Directory.GetDirectories(dir))
                        {
                            string dirName = Path.GetFileName(reportDir);
                            int idx = dirName.IndexOf(crashLabel, StringComparison.OrdinalIgnoreCase);
                            if (idx < 0) continue;
                            idx += crashLabel.Length;
                            int end = dirName.IndexOf('_', idx);
                            if (end <= idx) continue;
                            string exeName = dirName[idx..end];
                            if (!string.IsNullOrEmpty(exeName) && appExeNames.Contains(exeName))
                                results.Add(reportDir);
                        }
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // -- Registry Scanning --------------------------------------

        private static readonly bool Is64Bit = Environment.Is64BitOperatingSystem;

        private static List<string> ScanLeftoverRegistry(string displayName, string installLocation = "", ScannerMode mode = ScannerMode.Moderate)
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lockObj = new object();

            void AddLocal(Action<HashSet<string>> scan)
            {
                var local = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                scan(local);
                lock (lockObj) { results.UnionWith(local); }
            }

            // Safe mode: only Uninstall keys + exact HKCU\Software\AppName
            if (mode == ScannerMode.Safe)
            {
                ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", displayName, results, installLocation);
                ScanHiveForNames(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", displayName, results, installLocation);
                if (Is64Bit)
                    ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", displayName, results, installLocation);
                ScanHiveForNames(@"HKEY_CURRENT_USER\SOFTWARE", displayName, results, installLocation);
                return results.ToList();
            }

            string[] commonPublishers = mode == ScannerMode.Advanced
                ? []
                : GenericPublishers.Concat(["Wow6432Node", "Classes", "Clients", "RegisteredApplications", "VirtualBox", "Battle.net"]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            // Pre-compute class/guid hives based on bitness
            string[] classPathsNominal =
            [
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
            ];
            string[] classPathsExtra = Is64Bit
                ? [@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\WOW6432Node\CLSID", @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\WOW6432Node\AppID", @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\WOW6432Node\Interface", @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\WOW6432Node\TypeLib", @"HKEY_CURRENT_USER\SOFTWARE\Classes\WOW6432Node\CLSID", @"HKEY_CURRENT_USER\SOFTWARE\Classes\WOW6432Node\AppID"]
                : [];

            string[] guidHivesNominal =
            [
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\AppID",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Interface",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\TypeLib",
                @"HKEY_CURRENT_USER\SOFTWARE\Classes\CLSID",
                @"HKEY_CURRENT_USER\SOFTWARE\Classes\AppID",
                @"HKEY_CURRENT_USER\SOFTWARE\Classes\Interface",
                @"HKEY_CURRENT_USER\SOFTWARE\Classes\TypeLib",
            ];
            string[] guidHivesExtra = Is64Bit
                ? [@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\WOW6432Node\CLSID", @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\WOW6432Node\AppID", @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\WOW6432Node\Interface", @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\WOW6432Node\TypeLib",
                   @"HKEY_CURRENT_USER\SOFTWARE\Classes\WOW6432Node\CLSID", @"HKEY_CURRENT_USER\SOFTWARE\Classes\WOW6432Node\AppID", @"HKEY_CURRENT_USER\SOFTWARE\Classes\WOW6432Node\Interface", @"HKEY_CURRENT_USER\SOFTWARE\Classes\WOW6432Node\TypeLib",
                   @"HKEY_CURRENT_USER\SOFTWARE\Classes\Directory\shell", @"HKEY_CURRENT_USER\SOFTWARE\Classes\Drive\shell",
                   @"HKEY_CURRENT_USER\SOFTWARE\Classes\Directory\Background\shell", @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Drive\shell",
                   @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Directory\Background\shell",
                   @"HKEY_CURRENT_USER\SOFTWARE\Classes"]
                : [];

            string[] vsPathsNominal =
            [
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\VirtualStore\MACHINE\SOFTWARE",
                @"HKEY_CURRENT_USER\SOFTWARE\Classes\VirtualStore\MACHINE\SOFTWARE",
            ];
            string[] vsPathsExtra = Is64Bit
                ? [@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\VirtualStore\MACHINE\SOFTWARE\WOW6432Node", @"HKEY_CURRENT_USER\SOFTWARE\Classes\VirtualStore\MACHINE\SOFTWARE\WOW6432Node"]
                : [];

            // Each sub-scan below becomes its own parallel action so the total wall
            // time is bounded by the SLOWEST single scan (not the serial sum of two
            // or more heavy walks inside one batch � the MSI batch used to take ~8s
            // alone because ScanMsiUserData + ScanInstallerComponentsByValues ran
            // back-to-back in the same action).
            var batches = new List<(string Label, Action Run)>();

// Batch 1 � Uninstall keys (quick)
            batches.Add(("Uninstall keys", () => AddLocal(r =>
            {
                ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", displayName, r, installLocation);
                ScanHiveForNames(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", displayName, r, installLocation);
                if (Is64Bit)
                    ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", displayName, r, installLocation);
            })));

            // Batch 2 � Software hives (recursive) � each hive its own action
            batches.Add(("HKLM SOFTWARE recur", () => AddLocal(r => ScanSoftwareRecursive(@"HKEY_LOCAL_MACHINE\SOFTWARE", displayName, r, commonPublishers, 0, installLocation))));
            batches.Add(("HKCU SOFTWARE recur", () => AddLocal(r => ScanSoftwareRecursive(@"HKEY_CURRENT_USER\SOFTWARE", displayName, r, ["Microsoft", "Classes", "Wow6432Node", ..commonPublishers], 0, installLocation))));
            if (Is64Bit)
                batches.Add(("WOW6432 SOFTWARE recur", () => AddLocal(r => ScanSoftwareRecursive(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node", displayName, r, ["Microsoft", "Windows", ..commonPublishers], 0, installLocation))));

            // Batch 3 � Classes hives: one action per hive path so they overlap
            foreach (var cp in classPathsNominal.Concat(classPathsExtra))
            {
                string path = cp;
                batches.Add(("Classes hives", () => AddLocal(r => ScanHiveForNames(path, displayName, r, installLocation))));
            }

            // Batch 4 � COM hives
            batches.Add(("COM hives", () => AddLocal(r => ScanComHives(displayName, installLocation, r))));

            // Batch 5 � GUID hives value scan: one action per hive so they overlap
            foreach (var gh in guidHivesNominal.Concat(guidHivesExtra))
            {
                string path = gh;
                batches.Add(("GUID hives byvalue", () => AddLocal(r => ScanHiveByValues(path, installLocation, displayName, r))));
            }

            // Batch 6 � ComByFilePath is the heaviest COM scan (~4.1s) ? own action;
            // the cheap AppPaths/Run/ShellExt native scans stay grouped.
            batches.Add(("ComByFilePath", () => AddLocal(r => ScanComByFilePath(installLocation, displayName, r))));
            batches.Add(("AppPaths/Run/ShellExt", () => AddLocal(r =>
            {
                ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", displayName, r, installLocation);
                ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", displayName, r, installLocation);
                ScanHiveForNames(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", displayName, r, installLocation);
                ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers", displayName, r, installLocation);
                ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved", displayName, r, installLocation);
            })));

            // Batch 7 � MSI + SharedDLLs + InstallerFolders + InstallerComponents
            // ScanMsiUserData (~3.3s) + ScanInstallerComponentsByValues (~3.4s) used to run
            // back-to-back inside one action (~8s). Each walker now runs on its own action
            // so they overlap: total � max(walks) instead of the sum.
            batches.Add(("Installer categories", () => AddLocal(r =>
            {
                foreach (var mp in new[] { @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Installer\Products", @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Installer\Features", @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Installer\Patches" })
                    ScanHiveForNames(mp, displayName, r, installLocation);
            })));
            batches.Add(("MSI UserData", () => AddLocal(r => ScanMsiUserData(displayName, r, installLocation))));
            batches.Add(("SharedDLLs/InstallerFolders/Components", () => AddLocal(r =>
            {
                ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs", displayName, r, installLocation);
                ScanInstallerFolders(installLocation, r);
                ScanInstallerComponentsByValues(installLocation, displayName, r);
            })));

            // Batch 8 � AppCompat + RegisteredApplications + VirtualStore
            batches.Add(("AppCompat/RegApps/VStore", () => AddLocal(r =>
            {
                foreach (var cp in new[] {
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store",
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store",
                })
                    ScanHiveForNames(cp, displayName, r, installLocation);
                ScanRegisteredApplicationsWithFollow(displayName, r);
                foreach (var vp in vsPathsNominal.Concat(vsPathsExtra))
                    ScanSoftwareRecursive(vp, displayName, r, installLocation: installLocation);
            })));

            // Batch 9 � Services/EventLog (native, fast) + each C# walker on its own
            // action so UserAssist (~1.9s) + FirewallRules (~0.56s) overlap.
            batches.Add(("Services/EventLog", () => AddLocal(r =>
            {
                ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services", displayName, r, installLocation);
                ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\EventLog\Application", displayName, r, installLocation);
            })));
            batches.Add(("FirewallRules", () => AddLocal(r => ScanFirewallRules(displayName, r))));
            batches.Add(("UserAssist", () => AddLocal(r => ScanUserAssist(displayName, r))));
            batches.Add(("Debug/Heap/Audio", () => AddLocal(r =>
            {
                ScanDebugTracingByExe(installLocation, r);
                ScanHeapLeakByExe(installLocation, r);
                ScanAudioPolicyConfig(installLocation, r);
            })));

// Batch 10 � HKEY_USERS (can run in parallel with others)
            if (mode != ScannerMode.Safe)
            {
                batches.Add(("HKEY_USERS", () => AddLocal(r =>
                {
                    try
                    {
                        using var usersHive = Registry.Users.OpenSubKey("");
                        if (usersHive == null) return;
                        string[] systemSids = { ".DEFAULT", "S-1-5-18", "S-1-5-19", "S-1-5-20" };
                        // O SID do usuario atual ja e coberto pelo HKCU (mesmo perfil);
                        // escanea-lo de novo so produz duplicatas.
                        string currentSid = "";
                        try { currentSid = WindowsIdentity.GetCurrent().User?.Value ?? ""; }
                        catch { }
                        foreach (var sid in usersHive.GetSubKeyNames())
                        {
                            if (string.IsNullOrEmpty(sid)) continue;
                            if (systemSids.Contains(sid, StringComparer.OrdinalIgnoreCase)) continue;
                            if (sid.Equals(currentSid, StringComparison.OrdinalIgnoreCase)) continue;
                            string uh = $@"HKEY_USERS\{sid}";
                            ScanSoftwareRecursive($@"{uh}\Software", displayName, r, ["Microsoft", "Classes", "Wow6432Node"], 0, installLocation);
                            ScanHiveForNames($@"{uh}\Software\Classes\CLSID", displayName, r, installLocation);
                            ScanHiveForNames($@"{uh}\Software\Classes\AppID", displayName, r, installLocation);
                            ScanHiveForNames($@"{uh}\Software\Microsoft\Windows\CurrentVersion\Uninstall", displayName, r, installLocation);
                            ScanHiveForNames($@"{uh}\Software\Microsoft\Windows\CurrentVersion\Run", displayName, r, installLocation);
                        }
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                })));
            }

            // Wrap each scan with its own stopwatch so a scan run reveals which scan
            // burns the wall time. All actions run in parallel � the total � the
            // slowest single scan, not the serial sum of a monolithic batch.
            var timedActions = new List<Action>(batches.Count);
            foreach (var (label, run) in batches)
            {
                var done = Stopwatch.StartNew();
                timedActions.Add(() =>
                {
                    run();
                    done.Stop();
                    LogScanStage("  [REG] " + label, done);
                });
            }

            Parallel.Invoke(new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, timedActions.ToArray());

            // Cross-reference: remove keys that may belong to another app with similar name
            if (!string.IsNullOrEmpty(displayName))
            {
                var otherLocations = GetAllInstallLocations(excludeName: displayName);
                var keysToRemove = new List<string>();
                foreach (var r in results)
                {
                    string leafName = r.Split('\\').LastOrDefault() ?? "";
                    if (string.IsNullOrEmpty(leafName) || leafName.Length < 3) continue;

                    foreach (var otherLoc in otherLocations)
                    {
                        string otherName = Path.GetFileName(otherLoc.TrimEnd('\\')) ?? "";
                        if (string.IsNullOrEmpty(otherName)) continue;

                        int targetConf = Confidence.Generate(displayName, leafName);
                        int otherConf = Confidence.Generate(otherName, leafName);

                        if (otherConf >= 70 && otherConf > targetConf &&
                            !displayName.Contains(otherName, StringComparison.OrdinalIgnoreCase) &&
                            !otherName.Contains(displayName, StringComparison.OrdinalIgnoreCase))
                        {
                            keysToRemove.Add(r);
                        }
                    }
                }
                foreach (var k in keysToRemove)
                    results.Remove(k);
            }

            // Active Setup scanner: apps register here for per-user setup on first login
            if (mode >= ScannerMode.Moderate)
            {
                ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Active Setup\Installed Components", displayName, results, installLocation);
                if (Is64Bit)
                    ScanHiveForNames(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Active Setup\Installed Components", displayName, results, installLocation);
            }

            // Cross-hive registry linking: for each found key, check equivalent hives
            var linkedResults = new List<string>();
            foreach (var r in results)
                linkedResults.AddRange(GetRelatedKeys(r));
            foreach (var lr in linkedResults)
                results.Add(lr);

            // Apply user-defined exclusions (Revo RegExclude equivalent for registry)
            // GetUserExclusions() loads from the registry first � the in-memory list
            // may be empty on the first scan of a session even when saved exclusions exist.
            var regExclusions = GetUserExclusions();
            if (results.Count > 0 && regExclusions.Count > 0)
            {
                var toRemove = results.Where(IsExcludedPath).ToList();
                foreach (var r in toRemove)
                    results.Remove(r);
            }

            return results.ToList();
        }

        private static void ScanHiveForNames(string hiveKey, string displayName, HashSet<string> results, string installLocation = "")
        {
            try
            {
                var native = NativeRegistry.Scan(hiveKey, displayName, installLocation, null, 0);
                if (native != null)
                {
                    AddValidatedNative(native, displayName, installLocation, results);
                    return;
                }

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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        /// <summary>
        /// Revalida os resultados do native scan (reg_scan_ffi Rust) com a MESMA logica
        /// guardada do C#. O scan Rust casa por valor com confidence mas NAO tem os guards
        /// do KeyHasValueReferencing (separador de path, token adicional no data, extensao
        /// executavel) - casaria falsos positivos de nome-unico: Intel ME "...\ME\Display",
        /// npm "display.js", CLSID "Display" para displayName "Display Driver Uninstaller".
        /// O native retorna so os caminhos (sem mostras do match), entao reabrimos a chave e
        /// aplicamos o mesmo predicado do walker C#: nome >= 70 OU valor referenciando.
        /// </summary>
        private static void AddValidatedNative(List<string> native, string displayName, string installLocation, HashSet<string> results)
        {
            foreach (var path in native)
            {
                try
                {
                    string leaf = path.Split('\\').LastOrDefault() ?? "";
                    if (leaf.Length < 2) continue;
                    if (leaf.StartsWith('.') || leaf.StartsWith('_')) continue;
                    if (SystemFolderNames.Contains(leaf)) continue;

                    bool nameMatch = Confidence.Generate(displayName, leaf) >= 70;
                    bool valueMatch = false;
                    if (!nameMatch && !string.IsNullOrEmpty(installLocation))
                    {
                        var hive = ResolveHive(path, out string subKey);
                        if (hive != null)
                        {
                            using var key = hive.OpenSubKey(subKey, false);
                            if (key != null)
                                valueMatch = KeyHasValueReferencing(key, installLocation, displayName);
                        }
                    }
                    if (nameMatch || valueMatch)
                        results.Add(path);
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
        }

        private static void ScanSoftwareRecursive(string hiveKey, string displayName, HashSet<string> results, string[]? exclusions = null, int depth = 0, string installLocation = "")
        {
            if (depth > 2) return;
            try
            {
                if (depth == 0)
                {
                    var native = NativeRegistry.Scan(hiveKey, displayName, installLocation, exclusions?.ToList(), 1);
                    if (native != null)
                    {
                        AddValidatedNative(native, displayName, installLocation, results);
                        return;
                    }
                }

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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        private static void ScanMsiUserData(string displayName, HashSet<string> results, string installLocation = "")
        {
            try
            {
                using var userData = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData", false);
                if (userData == null) return;
                foreach (var sid in userData.GetSubKeyNames())
                {
                    // "Components" NAO entra no scan por nome/valor: GUIDs hex nunca casam por
                    // nome, e o match por valor casa falsos positivos de nome-unico no native
                    // scan (Rust) - ele nao tem os guards do KeyHasValueReferencing C# (Intel ME
                    // "...\ME\Display", npm "display.js" casavam "Display" em
                    // "Display Driver Uninstaller"). Components fica exclusivamente com
                    // ScanInstallerComponentsByValues (match por path do install, seguro).
                    foreach (var sub in new[] { "Products", "Patches" })
                        ScanHiveForNames($@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\{sid}\{sub}", displayName, results, installLocation);
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        /// <summary>
        /// Scans RegisteredApplications by matching value names and following their value paths
        /// to find target keys. If the target ends with \Capabilities, also adds the parent key (BCU pattern).
        /// </summary>
        private static void ScanRegisteredApplicationsWithFollow(string displayName, HashSet<string> results)
        {
            string[] hiveKeys =
            {
                @"HKEY_LOCAL_MACHINE\SOFTWARE\RegisteredApplications",
                @"HKEY_CURRENT_USER\SOFTWARE\RegisteredApplications",
            };

            foreach (var hiveKey in hiveKeys)
            {
                try
                {
                    var hive = ResolveHive(hiveKey, out string subKey);
                    if (hive == null || string.IsNullOrEmpty(subKey)) continue;
                    using var key = hive.OpenSubKey(subKey, false);
                    if (key == null) continue;

                    foreach (var valName in key.GetValueNames())
                    {
                        if (string.IsNullOrEmpty(valName) || valName.Length < 2) continue;
                        if (Confidence.Generate(displayName, valName) < 70) continue;

                        // Add the value itself
                        results.Add($@"{hiveKey}\{valName}");

                        // Follow the value to find the target path
                        string? targetPath = key.GetValue(valName) as string;
                        if (string.IsNullOrEmpty(targetPath)) continue;
                        targetPath = targetPath.Trim('\\', ' ', '"', '\'');

                        // Resolve the target key
                        string targetFull;
                        if (hiveKey.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase))
                            targetFull = $@"HKEY_LOCAL_MACHINE\{targetPath}";
                        else
                            targetFull = $@"HKEY_CURRENT_USER\{targetPath}";

                        var targetHive = ResolveHive(targetFull, out string targetSub);
                        if (targetHive == null || string.IsNullOrEmpty(targetSub)) continue;
                        using var targetKey = targetHive.OpenSubKey(targetSub, false);
                        if (targetKey == null) continue;

                        results.Add(targetFull);

                        // If target ends with \Capabilities, also add the parent key
                        const string capabilitiesSuffix = @"\Capabilities";
                        if (targetFull.EndsWith(capabilitiesSuffix, StringComparison.OrdinalIgnoreCase))
                        {
                            string parentKey = targetFull[..^capabilitiesSuffix.Length];
                            var parentHive = ResolveHive(parentKey, out string parentSub);
                            if (parentHive != null && !string.IsNullOrEmpty(parentSub))
                            {
                                using var parentRegKey = parentHive.OpenSubKey(parentSub, false);
                                if (parentRegKey != null)
                                    results.Add(parentKey);
                            }
                        }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
        }

        /// <summary>
        /// BCU HeapLeakDetectionScanner: scans 
        /// HKLM\SOFTWARE\Microsoft\RADAR\HeapLeakDetection\DiagnosedApplications
        /// for subkeys matching an executable filename found in the install directory.
        /// </summary>
        private static void ScanHeapLeakByExe(string installLocation, HashSet<string> results)
        {
            if (string.IsNullOrEmpty(installLocation) || !Directory.Exists(installLocation)) return;
            try
            {
                var exeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.GetFiles(installLocation, "*.exe", System.IO.SearchOption.TopDirectoryOnly))
                {
                    string fileName = Path.GetFileName(f);
                    if (!string.IsNullOrEmpty(fileName)) exeFiles.Add(fileName);
                }
                if (exeFiles.Count == 0) return;

                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\RADAR\HeapLeakDetection\DiagnosedApplications", false);
                if (key == null) return;

                foreach (var sub in key.GetSubKeyNames())
                {
                    if (!string.IsNullOrEmpty(sub) && exeFiles.Contains(sub))
                        results.Add($@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\RADAR\HeapLeakDetection\DiagnosedApplications\{sub}");
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        /// <summary>
        /// BCU AudioPolicyConfigScanner: scans 
        /// HKCU\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore
        /// for subkeys whose default value contains the unrooted install path.
        /// </summary>
        private static void ScanAudioPolicyConfig(string installLocation, HashSet<string> results)
        {
            if (string.IsNullOrEmpty(installLocation) || !Directory.Exists(installLocation)) return;
            try
            {
                string? pathRoot = Path.GetPathRoot(installLocation);
                if (string.IsNullOrEmpty(pathRoot)) return;
                string unrootedLocation = installLocation.Replace(pathRoot, string.Empty).Trim();
                if (string.IsNullOrEmpty(unrootedLocation)) return;

                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore", false);
                if (key == null) return;

                foreach (var subName in key.GetSubKeyNames())
                {
                    using var subKey = key.OpenSubKey(subName, false);
                    if (subKey == null) continue;
                    string? defVal = subKey.GetValue(null) as string;
                    if (defVal != null && defVal.IndexOf(unrootedLocation, StringComparison.OrdinalIgnoreCase) >= 0)
                        results.Add($@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore\{subName}");
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        /// <summary>
        /// BCU DebugTracingScanner: scans HKLM\SOFTWARE\Microsoft\Tracing for subkeys
        /// ending in _RASAPI32 or _RASMANCS whose stem (part before the _) matches
        /// an executable found in the install directory.
        /// </summary>
        private static void ScanDebugTracingByExe(string installLocation, HashSet<string> results)
        {
            if (string.IsNullOrEmpty(installLocation) || !Directory.Exists(installLocation)) return;
            try
            {
                var exeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.GetFiles(installLocation, "*.exe", System.IO.SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    if (!string.IsNullOrEmpty(name)) exeNames.Add(name);
                }
                if (exeNames.Count == 0) return;

                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Tracing", false);
                if (key == null) return;

                foreach (var name in key.GetSubKeyNames())
                {
                    if (!name.EndsWith("_RASAPI32", StringComparison.OrdinalIgnoreCase) &&
                        !name.EndsWith("_RASMANCS", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int idx = name.LastIndexOf('_');
                    if (idx <= 0) continue;
                    string stem = name[..idx];
                    if (!string.IsNullOrEmpty(stem) && exeNames.Contains(stem))
                        results.Add($@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Tracing\{name}");
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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

        // -- Cross-reference helpers ---------------------------------

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
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
            return locations.ToList();
        }

        private static List<string> GetAllAppDisplayNames(string? excludeName = null)
        {
            var names = new List<string>();
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
                            if (string.IsNullOrEmpty(dn)) continue;
                            if (!string.IsNullOrEmpty(excludeName) && dn.Equals(excludeName, StringComparison.OrdinalIgnoreCase)) continue;
                            if (dn.Trim().Length >= 3)
                                names.Add(dn.Trim());
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
            return names;
        }

        // -- Cleanup ------------------------------------------------

        /// <summary>
        /// Resolves the install dir when the Uninstall key did not provide InstallLocation.
        /// 1) ExtractInstallDirFromRegistryPaths � looks at registry VALUES that still
        ///    reference the app's exe (CLSID LocalServer32, shell command, URL protocol
        ///    command, DisplayIcon/InstallLocation in Uninstall keys). Works even when the
        ///    install folder was ALREADY DELETED (the common post-uninstall case).
        /// 2) InferInstallDirectory � fallback from leftover folders that still exist.
        /// </summary>
        private static string? ResolveInstallLocation(string displayName, IEnumerable<string> leftoverPaths, string? publisher = null)
        {
            string? fromReg = ExtractInstallDirFromRegistryPaths(displayName);
            if (!string.IsNullOrEmpty(fromReg))
                return fromReg;
            return InferInstallDirectory(displayName, leftoverPaths, publisher);
        }

        /// <summary>
        /// Scans known registry value locations for a path to the app's .exe (or a folder
        /// path containing an app token). Does NOT require the folder to exist: the goal is
        /// to recover the exact install path string the leftover COM entries still point to.
        /// </summary>
        private static string? ExtractInstallDirFromRegistryPaths(string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return null;
            string[] tokens = BuildDisplayTokens(displayName);
            if (tokens.Length == 0) return null;

            var localPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            string? best = null;
            int bestScore = 0;

            void Consider(string? raw)
            {
                if (string.IsNullOrEmpty(raw)) return;
                string s = raw.Trim().Trim('"');
                int exeIdx = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exeIdx <= 0) return;
                // take the path ending at the .exe (strip quotes/args that follow)
                int start = s.LastIndexOf(":\\", exeIdx - 1);
                if (start < 0) return;
                string exe = s.Substring(start - 1, exeIdx + 4 - (start - 1)).Trim('"');
                if (!Path.IsPathRooted(exe)) return;
                string dir = Path.GetDirectoryName(exe) ?? "";
                if (string.IsNullOrEmpty(dir)) return;

                int score = 0;
                string dirName = Path.GetFileName(dir) ?? "";
                // exe file name matches a token (e.g. ZCode.exe) � strongest
                string exeName = Path.GetFileNameWithoutExtension(exe) ?? "";
                bool exeMatch = tokens.Any(tok => exeName.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0);
                if (exeMatch) score += 10;
                // dir leaf matches a token
                bool dirMatch = tokens.Any(tok => dirName.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0);
                if (dirMatch) score += 6;
                // inside a Programs folder
                if (!string.IsNullOrEmpty(localPrograms) && dir.StartsWith(localPrograms + "\\", StringComparison.OrdinalIgnoreCase)) score += 4;
                else if ((!string.IsNullOrEmpty(pf) && dir.StartsWith(pf + "\\", StringComparison.OrdinalIgnoreCase)) ||
                         (!string.IsNullOrEmpty(pf86) && dir.StartsWith(pf86 + "\\", StringComparison.OrdinalIgnoreCase))) score += 2;
                // exclude Windows system dirs (System32/Windows/* are not app installs)
                string upper = dir.ToUpperInvariant();
                if (upper.Contains("\\WINDOWS\\") || upper.StartsWith("C:\\WINDOWS\\") || upper.Contains("\\SYSTEM32")) return;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = dir;
                }
            }

            void ScanComRoot(RegistryKey root, string classesPath)
            {
                try
                {
                    using var classes = root.OpenSubKey(classesPath, false);
                    if (classes == null) return;
                    foreach (var guidName in classes.GetSubKeyNames())
                    {
                        try
                        {
                            using var guid = classes.OpenSubKey(guidName, false);
                            if (guid == null) continue;
                            // LocalServer32/InprocServer32 are SUBKEYS holding the exe in their
                            // default value (LocalServer32 = "C:\...\app.exe")
                            foreach (var serverName in new[] { "LocalServer32", "InprocServer32" })
                            {
                                using var server = guid.OpenSubKey(serverName, false);
                                Consider(server?.GetValue(null) as string);
                            }
                        }
                        catch { /* ignore */ }
                    }
                }
                catch { /* ignore */ }
            }

            // 1) CLSID LocalServer32 � the app's own COM server (survives uninstall)
            ScanComRoot(Registry.LocalMachine, @"SOFTWARE\Classes\CLSID");
            ScanComRoot(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Classes\CLSID");
            ScanComRoot(Registry.CurrentUser, @"SOFTWARE\Classes\CLSID");

            // 2) URL protocol command (Classes\zcode) + context menu shell commands
            string[] shellRoots =
            {
                @"SOFTWARE\Classes",
                @"SOFTWARE\WOW6432Node\Classes"
            };
            foreach (var rootPath in shellRoots)
            {
                try
                {
                    using var classes = Registry.LocalMachine.OpenSubKey(rootPath, false);
                    if (classes == null) continue;
                    foreach (var name in classes.GetSubKeyNames())
                    {
                        if (name.Contains('.')) continue;   // skip progid/extensions
                        try
                        {
                            using var sk = classes.OpenSubKey(name, false);
                            if (sk == null) continue;
                            if (sk.GetValue("URL Protocol") != null)
                            {
                                using var openCmd = sk.OpenSubKey(@"shell\open\command", false);
                                Consider(openCmd?.GetValue(null) as string);
                            }
                            foreach (var verb in sk.GetSubKeyNames())
                            {
                                if (!verb.Contains("shell")) continue;
                                try
                                {
                                    using var vs = sk.OpenSubKey($@"{verb}\command", false);
                                    Consider(vs?.GetValue(null) as string);
                                }
                                catch { /* ignore */ }
                            }
                        }
                        catch { /* ignore */ }
                    }
                }
                catch { /* ignore */ }
            }
            try
            {
                using var classes = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Classes", false);
                if (classes != null)
                {
                    foreach (var name in classes.GetSubKeyNames())
                    {
                        if (name.Contains('.')) continue;
                        try
                        {
                            using var sk = classes.OpenSubKey(name, false);
                            if (sk == null) continue;
                            if (sk.GetValue("URL Protocol") != null)
                            {
                                using var openCmd = sk.OpenSubKey(@"shell\open\command", false);
                                Consider(openCmd?.GetValue(null) as string);
                            }
                        }
                        catch { /* ignore */ }
                    }
                }
            }
            catch { /* ignore */ }

            // 3) Uninstall keys: DisplayIcon / InstallLocation still point to the exe
            string[] uninstallRoots =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            foreach (var rootPath in uninstallRoots)
            {
                try
                {
                    using var root = Registry.LocalMachine.OpenSubKey(rootPath, false);
                    if (root == null) continue;
                    foreach (var name in root.GetSubKeyNames())
                    {
                        try
                        {
                            using var sk = root.OpenSubKey(name, false);
                            if (sk == null) continue;
                            var dn = sk.GetValue("DisplayName") as string;
                            if (string.IsNullOrEmpty(dn) || dn.IndexOf(displayName, StringComparison.OrdinalIgnoreCase) < 0 &&
                                !tokens.Any(tok => dn.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                            Consider(sk.GetValue("InstallLocation") as string);
                            Consider(sk.GetValue("DisplayIcon") as string);
                            Consider(sk.GetValue("UninstallString") as string);
                        }
                        catch { /* ignore */ }
                    }
                }
                catch { /* ignore */ }
            }

            return best;
        }

        /// <summary>
        /// Revo-style fallback: when the registry does not provide an InstallLocation
        /// (common for portable/user-space apps like VS Code forks � Uninstall key lacks
        /// InstallLocation or DisplayName doesn't exact-match the list entry), infer the
        /// install dir from the leftover folders already found by the file scan.
        /// Very targeted: only accept a directory whose leaf name matches a displayName
        /// token (or publisher token), prefer under LocalAppData\Programs / ProgramFiles,
        /// and prefer dirs containing an .exe.
        /// </summary>
        private static string? InferInstallDirectory(string displayName, IEnumerable<string> leftoverPaths, string? publisher = null)
        {
            if (string.IsNullOrEmpty(displayName)) return null;

            var localPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            string[] appTokens = BuildDisplayTokens(displayName, publisher);
            if (appTokens.Length == 0) return null;

            string? best = null;
            int bestScore = 0;

            foreach (var raw in leftoverPaths)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                string p = raw.TrimEnd('\\');
                if (!Directory.Exists(p)) continue;
                try
                {
                    if (IsSystemFolder(p)) continue;
                    string leaf = Path.GetFileName(p);
                    if (string.IsNullOrEmpty(leaf)) continue;
                    if (SystemFolderNames.Contains(leaf)) continue;

                    int score = 0;
                    // 1) leaf name matches (strongest signal, e.g. "ZCode" folder vs "ZCode 3.2.2")
                    int conf = Confidence.Generate(displayName, leaf);
                    bool leafMatch = conf >= 50 || appTokens.Any(tok => leaf.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (leafMatch) score += 3 * Math.Max(1, conf / 33);

                    // 2) leaf matches publisher name (Revo marker heuristic)
                    if (!string.IsNullOrEmpty(publisher) && !GenericPublishers.Contains(publisher) &&
                        leaf.IndexOf(publisher, StringComparison.OrdinalIgnoreCase) >= 0)
                        score += 4;

                    // 3) under a Programs folder � the most likely install root
                    bool underPrograms = !string.IsNullOrEmpty(localPrograms) && p.StartsWith(localPrograms + "\\", StringComparison.OrdinalIgnoreCase);
                    bool underPf = (!string.IsNullOrEmpty(pf) && p.StartsWith(pf + "\\", StringComparison.OrdinalIgnoreCase)) ||
                                   (!string.IsNullOrEmpty(pf86) && p.StartsWith(pf86 + "\\", StringComparison.OrdinalIgnoreCase));
                    if (underPrograms) score += 2;
                    else if (underPf) score += 1;

                    // 4) contains an .exe inside (real application root, not a data folder)
                    bool hasExe = false;
                    try { hasExe = Directory.EnumerateFiles(p, "*.exe", System.IO.SearchOption.TopDirectoryOnly).Any(); }
                    catch { /* ignore */ }
                    if (hasExe) score += 2;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = p;
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            return best;
        }

        private static string[] BuildDisplayTokens(string displayName, string? publisher = null)
        {
            var toks = new List<string>();
            if (string.IsNullOrEmpty(displayName)) return toks.ToArray();

            // Split on non-alphanumeric; keep words >= 3 chars, drop version-ish numeric tokens
            foreach (var w in Regex.Split(displayName, @"[^0-9A-Za-z]+"))
            {
                if (string.IsNullOrEmpty(w) || w.Length < 3) continue;
                bool isNumeric = w.All(c => char.IsDigit(c) || c == '.' || c == ',');
                if (isNumeric) continue;
                toks.Add(w);
            }

            // Version-suffixed display names ("ZCode 3.2.2"): strip trailing version so the
            // token match still finds the folder ("ZCode"), not only a versioned folder.
            var baseName = Regex.Replace(displayName, @"[\s\d.]+$", "");
            if (!string.IsNullOrEmpty(baseName) && baseName.Length >= 3 &&
                !toks.Contains(baseName, StringComparer.OrdinalIgnoreCase))
                toks.Add(baseName);

            return toks.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

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
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
            return null;
        }

        public static DateTime? GetInstallDateFromRegistry(string displayName)
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
                            var raw = sk.GetValue("InstallDate") as string;
                            if (string.IsNullOrEmpty(raw)) continue;
                            if (DateTime.TryParseExact(raw, "yyyyMMdd", null,
                                System.Globalization.DateTimeStyles.None, out var dt))
                                return dt;
                            if (DateTime.TryParse(raw, out var dt2))
                                return dt2;
                        }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
            return null;
        }

        public static void PerformCleanup(List<string> filesToDelete, List<string> registryToDelete, UninstallResult result, string displayName = "", string installLocation = "", CancellationToken ct = default, IProgress<string>? progress = null)
        {
            var logEntries = new List<string>();
            logEntries.Add($"=== KitLugia Deletion Log � {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            logEntries.Add("");

            int totalItems = filesToDelete.Distinct(StringComparer.OrdinalIgnoreCase).Count() + registryToDelete.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int current = 0;

            foreach (var file in filesToDelete.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                progress?.Report($"Limpando arquivos ({++current}/{totalItems})...");
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
                        // Recycle bin first approach (BCUninstaller pattern), configurable (DelToBin)
                        if (DeepUninstallSettings.SendToRecycleBin)
                        {
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
                        else
                        {
                            Directory.Delete(file, true);
                            result.FilesDeleted++;
                            logEntries.Add($"REMOVED  {file} [permanent]");
                        }
                    }
                    else if (File.Exists(file))
                    {
                        if (DeepUninstallSettings.SendToRecycleBin)
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
                        else
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
                progress?.Report($"Limpando registro ({++current}/{totalItems})...");
                SafeDeleteRegistryEntry(reg, displayName, installLocation, result, logEntries, ct);
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            // Persistent undo history (Revo-style): survives reboot
            try
            {
                UninstallHistory.Record(new UninstallHistoryEntry
                {
                    AppName = displayName,
                    FilesDeleted = result.FilesDeleted,
                    RegistryDeleted = result.RegistryDeleted,
                    FilesBackedUp = result.BackupFiles,
                    RegistryBackups = result.BackupRegistryFiles,
                    DeletionLogFile = result.DeletionLogFile
                });
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return null; }
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // -- Value-based Registry Match -----------------------------

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
                        {
                            // Guarda contra falsos positivos de nome-único:
                            // 1. Valores sem separador de caminho ("Display") sao descricoes de classe,
                            //    nao referencias a arquivo — nunca casam como residuo.
                            // 2. Um LEAF que e apenas a PRIMEIRA palavra do displayName
                            //    ("Display" em "Display Driver Uninstaller") NAO e evidencia:
                            //    "display.js" (npm), "...\ME\Display" (Intel) e CLSID "Display"
                            //    seriam falsos positivos sem esse guard.
                            if (!IsPathLikeValue(data))
                                continue;

                                string[] displayTokens = SplitNameTokens(displayName);
                                if (displayTokens.Length >= 2 &&
                                    displayTokens[0].Length >= 4 &&
                                    fileName.Equals(displayTokens[0], StringComparison.OrdinalIgnoreCase))
                                {
                                    // Leaf igual a primeira palavra-do-nome: exige um token adicional
                                    // no DATA ou que o leaf coincida com o nome COMPLETO.
                                    bool strong = displayName.Contains(fileName, StringComparison.OrdinalIgnoreCase) &&
                                                  displayTokens.Length >= 3 &&
                                                  displayTokens.Take(2).All(t => data.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
                                    if (!strong)
                                        continue;
                                    // Um leaf que e apenas a primeira palavra so conta quando aponta
                                    // para um EXECUTAVEL (.exe/.dll/.com/.bat/.cmd/.scr/.ps1):
                                    // "...\Drivers\Display.ico" (Realtek) e "...\display.js" (npm)
                                    // passariam no strong acima por substring ("Drivers" contem "Driver").
                                    string ext = Path.GetExtension(data) ?? "";
                                    bool executableExt = ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                                                         ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                                                         ext.Equals(".com", StringComparison.OrdinalIgnoreCase) ||
                                                         ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
                                                         ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                                                         ext.Equals(".scr", StringComparison.OrdinalIgnoreCase) ||
                                                         ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase);
                                    if (!executableExt)
                                        continue;
                                }

                            return true;
                        }
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return false;
        }

        /// <summary>
        /// Verdadeiro se o valor parece um caminho (tem barra, ou extensão de arquivo,
        /// ou termina com nome de arquivo). Valores nus como "Display" (descrição de
        /// classe COM) sao rejeitados — nao sao referência a arquivo nenhum.
        /// </summary>
        private static bool IsPathLikeValue(string data)
        {
            if (string.IsNullOrEmpty(data)) return false;
            if (data.IndexOf('\\') >= 0 || data.IndexOf('/') >= 0) return true;
            if (Path.HasExtension(data)) return true;
            // Registry-rooted refs como "22:\Software\..." sao paths de MSI (com barra)
            return System.Text.RegularExpressions.Regex.IsMatch(data, @"^\d+:[\\/]");
        }

        /// <summary>
        /// Divide o displayName em tokens (>= 3 chars) ignorando pontuacao comum.
        /// </summary>
        private static string[] SplitNameTokens(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return Array.Empty<string>();
            return System.Text.RegularExpressions.Regex.Split(name, @"[^\w\-]")
                .Where(t => t.Length >= 4)
                .ToArray();
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
                var native = NativeRegistry.Scan(hiveKey, displayName, installLocation, null, 2);
                if (native != null)
                {
                    AddValidatedNative(native, displayName, installLocation, results);
                    return;
                }

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
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // -- Helpers ------------------------------------------------

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
            int exeIdx = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIdx > 0)
            {
                string exePath = cmd[..(exeIdx + 4)];
                string rest = cmd.Length > exeIdx + 4 ? cmd[(exeIdx + 5)..].TrimStart() : "";
                return (exePath, rest);
            }
            int space = cmd.IndexOf(' ');
            if (space > 0) return (cmd[..space], cmd[(space + 1)..].TrimStart());
            return (cmd, "");
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            name = NativeRegex.Replace(name, @"\s+\d+[\d.]*\d$", "");
            name = NativeRegex.Replace(name, @"\s+(Inc|LLC|Ltd|Limited|Corp|Corporation|GmbH|SAS|SRL|SA|Pty|Ltee)\.?$", "");
            name = NativeRegex.Replace(name, @"\s*\([^)]*\)$", "");
            name = NativeRegex.Replace(name, @"[���]", "");
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
                        if (NativeRegex.IsMatch(name, @"^unins\d{3}\.exe$"))
                            knownUninstallers = [..knownUninstallers, name];
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            // If the registry already points to a known uninstaller INSIDE the install dir,
            // prefer the registry version (it may have important switches).
            // Otherwise, if we found one physically, use it � this handles cases where
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
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
            return false;
        }

        // -- System Restore Point ----------------------------------

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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        // -- Registry .reg Backup ---------------------------------

        private static readonly string PathBackupDir = Path.Combine(DeepUninstallSettings.PersistentRoot, "PathBackup");
        private static readonly string RegistryBackupDir = Path.Combine(
            DeepUninstallSettings.PersistentRoot, "RegBackup");

        /// <summary>
        /// Exports a registry key to a .reg file before deletion.
        /// Returns the path to the backup file, or null on failure.
        /// </summary>
        private static string? BackupRegistryKey(string fullRegistryPath, CancellationToken ct = default)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
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

                ExportKeyToReg(sb, fullRegistryPath, key, maxItems: 500, ct: ct);
                File.WriteAllText(backupFile, sb.ToString(), Encoding.Unicode);
                return backupFile;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return null; }
        }

        private static void ExportKeyToReg(StringBuilder sb, string fullPath, RegistryKey key, int maxItems = 500, CancellationToken ct = default)
        {
            if (maxItems <= 0) { sb.AppendLine($"; [TRUNCATED � too many subkeys]"); return; }
            ct.ThrowIfCancellationRequested();

            sb.AppendLine($@"[{fullPath}]");
            int count = 0;
            foreach (var valName in key.GetValueNames())
            {
                if (count++ >= maxItems) { sb.AppendLine($"; [TRUNCATED � too many values]"); break; }
                ct.ThrowIfCancellationRequested();
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

            int remaining = maxItems - count;
            if (remaining <= 0) { sb.AppendLine($"; [TRUNCATED � no room for subkeys]"); return; }

            int subCount = 0;
            foreach (var name in key.GetSubKeyNames())
            {
                if (subCount++ >= remaining) { sb.AppendLine($"; [TRUNCATED � too many subkeys]"); break; }
                ct.ThrowIfCancellationRequested();
                using var sk = key.OpenSubKey(name, false);
                if (sk != null)
                    ExportKeyToReg(sb, $@"{fullPath}\{name}", sk, remaining - subCount, ct);
            }
        }

        /// <summary>
        /// Backs up the current User PATH value to a .pathbak file before modification.
        /// Returns the backup file path, or null on failure.
        /// </summary>
        private static string? BackupPathValue(string appName)
        {
            try
            {
                if (!Directory.Exists(PathBackupDir))
                    Directory.CreateDirectory(PathBackupDir);
                string safeName = string.Join("_", appName.Split(Path.GetInvalidFileNameChars()));
                if (safeName.Length > 100) safeName = safeName[..100];
                string backupFile = Path.Combine(PathBackupDir,
                    $"{safeName}_{DateTime.Now:yyyyMMddHHmmss}.pathbak");
                using var envKey = Registry.CurrentUser.OpenSubKey("Environment", false);
                string? currentPath = envKey?.GetValue("PATH") as string;
                if (currentPath == null) return null;
                File.WriteAllText(backupFile, currentPath, Encoding.Unicode);
                return backupFile;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return null; }
        }

        /// <summary>
        /// Surgically removes any PATH entries matching the install location or app name.
        /// Backs up the original PATH first. Only modifies the USER PATH (not SYSTEM).
        /// Returns true if PATH was modified.
        /// </summary>
        private static bool RemovePathEntrySurgically(string displayName, string installLocation)
        {
            try
            {
                using var envKey = Registry.CurrentUser.OpenSubKey("Environment", true);
                if (envKey == null) return false;
                string? currentPath = envKey.GetValue("PATH") as string;
                if (string.IsNullOrEmpty(currentPath)) return false;

                var parts = currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
                var newParts = new List<string>();
                bool removed = false;

                foreach (var entry in parts)
                {
                    string trimmed = entry.Trim();
                    bool matches = false;

                    // Match by install location
                    if (!string.IsNullOrEmpty(installLocation) &&
                        trimmed.IndexOf(installLocation.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) >= 0)
                        matches = true;

                    // Match by app name in the path entry
                    if (!matches && !string.IsNullOrEmpty(displayName) &&
                        trimmed.IndexOf(displayName, StringComparison.OrdinalIgnoreCase) >= 0)
                        matches = true;

                    // Safety: never remove non-directory entries (like %SYSTEMROOT% or bare drive roots)
                    if (matches)
                    {
                        // Only remove if it looks like a directory path (contains \ or is a root path)
                        if (trimmed.Contains('\\') || trimmed.Contains('/'))
                        {
                            removed = true;
                            continue;
                        }
                    }

                    newParts.Add(entry);
                }

                if (!removed) return false;

                string newPath = string.Join(";", newParts);
                envKey.SetValue("PATH", newPath, RegistryValueKind.ExpandString);
                // Broadcast environment change
                try
                {
                    using var chgKey = Registry.CurrentUser.OpenSubKey("Environment", true);
                    if (chgKey != null)
                        chgKey.SetValue("PATH", newPath, RegistryValueKind.ExpandString);
                    // Notify Windows of env change
                    _ = NativeMethods.SendMessageTimeout(
                        new IntPtr(NativeMethods.HWND_BROADCAST),
                        NativeMethods.WM_SETTINGCHANGE,
                        IntPtr.Zero,
                        "Environment",
                        NativeMethods.SMTO_ABORTIFHUNG,
                        5000,
                        out _);
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                return true;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        /// <summary>
        /// Determines whether a registry path points to a value (not a subkey).
        /// Opens the parent key and checks if the last segment is a value name.
        /// </summary>
        private static bool IsRegistryValuePath(string fullPath)
        {
            try
            {
                int lastSep = fullPath.LastIndexOf('\\');
                if (lastSep < 0) return false;
                string parentPath = fullPath[..lastSep];
                string leafName = fullPath[(lastSep + 1)..];

                var hive = ResolveHive(parentPath, out string subKey);
                if (hive == null || string.IsNullOrEmpty(subKey)) return false;
                using var key = hive.OpenSubKey(subKey, false);
                if (key == null) return false;
                return key.GetValueNames().Contains(leafName, StringComparer.OrdinalIgnoreCase);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        /// <summary>
        /// Backs up a single registry value (not a whole subkey) to a .reg file.
        /// </summary>
        private static string? BackupRegistryValue(string fullPath, CancellationToken ct = default)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(RegistryBackupDir))
                    Directory.CreateDirectory(RegistryBackupDir);

                int lastSep = fullPath.LastIndexOf('\\');
                if (lastSep < 0) return null;
                string parentPath = fullPath[..lastSep];
                string valueName = fullPath[(lastSep + 1)..];

                string safeName = fullPath.Replace('\\', '_').Replace('/', '_').Replace(':', '_').Trim('_');
                if (safeName.Length > 200) safeName = safeName[^200..];
                string backupFile = Path.Combine(RegistryBackupDir,
                    $"{safeName}_{DateTime.Now:yyyyMMddHHmmss}.reg");

                var hive = ResolveHive(parentPath, out string subKey);
                if (hive == null || string.IsNullOrEmpty(subKey)) return null;
                using var key = hive.OpenSubKey(subKey, false);
                if (key == null) return null;

                var val = key.GetValue(valueName);
                if (val == null) return null;
                var kind = key.GetValueKind(valueName);

                var sb = new StringBuilder();
                sb.AppendLine("Windows Registry Editor Version 5.00");
                sb.AppendLine();
                sb.AppendLine($@"[{parentPath}]");
                string escapedName = valueName == "" ? "@" : EscapeRegString(valueName);
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
                sb.AppendLine();
                File.WriteAllText(backupFile, sb.ToString(), Encoding.Unicode);
                return backupFile;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return null; }
        }

        /// <summary>
        /// Safely deletes a registry entry, handling both subkey paths and value paths.
        /// For PATH specifically, performs surgical removal of app entries instead of full deletion.
        /// </summary>
        private static void SafeDeleteRegistryEntry(string reg, string displayName, string installLocation,
            UninstallResult result, List<string> logEntries, CancellationToken ct = default)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                // PATH special handling: surgically remove app entries, never delete the whole variable
                if (reg.Equals(@"HKEY_CURRENT_USER\Environment\PATH", StringComparison.OrdinalIgnoreCase))
                {
                    if (BackupPathValue(displayName) is string bakFile)
                        result.BackupRegistryFiles.Add(bakFile);
                    bool changed = RemovePathEntrySurgically(displayName, installLocation);
                    if (changed)
                    {
                        result.RegistryDeleted++;
                        logEntries.Add($"MODIFIED HKEY_CURRENT_USER\\Environment\\PATH (removed entries for {displayName})");
                    }
                    else
                        logEntries.Add($"SKIPPED  {reg} (no matching PATH entries found)");
                    return;
                }

                var parts = reg.Split(new[] { '\\' }, 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return;
                RegistryKey? hive = ResolveHive(reg, out _);
                if (hive == null) return;
                string keyPath = parts.Length > 2 ? parts[2] : parts[1];

                if (IsRegPathTooBroad(reg, parts, keyPath))
                {
                    logEntries.Add($"SKIPPED  {reg} (too broad)");
                    return;
                }

                if (IsRegistryValuePath(reg))
                {
                    // This is a value, not a subkey � back it up and delete the single value
                    string? backupFile = BackupRegistryValue(reg, ct);
                    if (backupFile != null)
                        result.BackupRegistryFiles.Add(backupFile);

                    int lastSep = reg.LastIndexOf('\\');
                    string parentPath = reg[..lastSep];
                    string valueName = reg[(lastSep + 1)..];
                    var parentHive = ResolveHive(parentPath, out string parentSubKey);
                    if (parentHive != null && !string.IsNullOrEmpty(parentSubKey))
                    {
                        using var parentKey = parentHive.OpenSubKey(parentSubKey, true);
                        if (parentKey != null)
                        {
                            parentKey.DeleteValue(valueName, false);
                            result.RegistryDeleted++;
                            logEntries.Add($"REMOVED  {reg} [value]");
                            return;
                        }
                    }
                }
                else
                {
                    // Service key: stop + unregister service before deleting
                    if (reg.StartsWith(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\", StringComparison.OrdinalIgnoreCase))
                    {
                        string serviceName = keyPath.Split('\\').Last() ?? "";
                        if (!string.IsNullOrEmpty(serviceName))
                            TryStopAndDeleteService(serviceName);
                    }

                    // Subkey: normal backup and deletion (limited to 500 items via ct)
                    string? backupFile = BackupRegistryKey(reg, ct);
                    if (backupFile != null)
                        result.BackupRegistryFiles.Add(backupFile);

                    hive.DeleteSubKeyTree(keyPath, false);
                    result.RegistryDeleted++;
                    logEntries.Add($"REMOVED  {reg} [key]");
                }
            }
            catch (OperationCanceledException)
            {
                logEntries.Add($"TIMEOUT {reg} � operation timed out, skipped");
            }
            catch (Exception ex)
            {
                logEntries.Add($"FAILED   {reg} -> {ex.Message}");
            }
        }

        /// <summary>
        /// Attempts to stop and unregister a Windows service by name.
        /// Uses sc.exe for reliable behavior. Silently handles errors.
        /// </summary>
        private static void TryStopAndDeleteService(string serviceName)
        {
            try
            {
                using var sc = new System.Diagnostics.Process();
                sc.StartInfo.FileName = "sc";
                sc.StartInfo.Arguments = $"stop \"{serviceName}\"";
                sc.StartInfo.CreateNoWindow = true;
                sc.StartInfo.UseShellExecute = false;
                sc.Start();
                sc.WaitForExit(3000);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            try
            {
                using var sc = new System.Diagnostics.Process();
                sc.StartInfo.FileName = "sc";
                sc.StartInfo.Arguments = $"delete \"{serviceName}\"";
                sc.StartInfo.CreateNoWindow = true;
                sc.StartInfo.UseShellExecute = false;
                sc.Start();
                sc.WaitForExit(3000);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        /// <summary>
        /// P/Invoke declarations for broadcasting environment changes to Windows.
        /// </summary>
        private static class NativeMethods
        {
            public const int HWND_BROADCAST = 0xffff;
            public const int WM_SETTINGCHANGE = 0x001a;
            public const int SMTO_ABORTIFHUNG = 0x0002;

            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
            public static extern IntPtr SendMessageTimeout(
                IntPtr hWnd, int Msg, IntPtr wParam, string lParam, int fuFlags, int uTimeout, out IntPtr lpdwResult);
        }

        private static string EscapeRegString(string s)
        {
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r");
        }

        // -- Process Tree Kill (single WMI query) ------------------

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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
                string compressed = NativeRegex.Replace(displayName, @"[\s\-_.]+", "");
                if (compressed.Length >= 3) targetNames.Add(compressed);
            }

            if (!string.IsNullOrEmpty(installLocation))
            {
                try
                {
                    string dir = Path.GetFileName(installLocation.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(dir)) targetNames.Add(dir);
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                        finally { proc.Dispose(); }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
                                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
                                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                            }
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    });
                    wmiTask.Wait(2000);
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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

        // -- SortedExecutables Builder ------------------------------

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
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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

        // -- Cross-Hive Registry Linking ----------------------------

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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return related;
        }

        // -- Expanded COM Scanning ----------------------------------

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

        /// <summary>
        /// Pattern-based COM / shell-handler scanner (Revo-class):
        /// pre-loads CLSID entries (InprocServer32/InprocHandler32/LocalServer32 defaults),
        /// TypeLib (0\win32/win64), Interface ProxyStubClsid32 mapping, plus context-menu
        /// handlers (Directory\shell, Drive\shell, *\shell, Directory\Background\shell)
        /// and custom URL protocols. Catches GUID-keyed COM and right-click entries that
        /// never match by name; does NOT hardcode per-app GUIDs.
        /// </summary>
        private static void ScanComByFilePath(string installLocation, string displayName, HashSet<string> results)
        {
            if (string.IsNullOrEmpty(installLocation)) return;

            var comEntries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // GUID -> filePath
            var interfaceToClsid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // InterfaceGUID -> ProxyClsid

            string normalizedInstall = Path.GetFullPath(installLocation).TrimEnd('\\');

            string[][] classRoots =
            {
                new[] { @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes", "HKLM" },
                new[] { @"HKEY_CURRENT_USER\SOFTWARE\Classes", "HKCU" },
            };
            if (Is64Bit)
            {
                classRoots = [
                    ..classRoots,
                    new[] { @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\WOW6432Node", "HKLM" },
                    new[] { @"HKEY_CURRENT_USER\SOFTWARE\Classes\WOW6432Node", "HKCU" },
                ];
            }

            foreach (var root in classRoots)
            {
                string basePath = root[0];

                // CLSID
                ScanComClsidEntries(basePath, normalizedInstall, results, comEntries);

                // TypeLib
                ScanComTypeLibEntries(basePath, normalizedInstall, results, comEntries);

                // Interface -> ProxyStubClsid32 mapping
                ScanComInterfaceEntries(basePath, results, comEntries, interfaceToClsid);

                // Context-menu (right-click) handlers under shell\...
                ScanContextMenuHandlers(basePath, normalizedInstall, displayName, results);

                // Custom URL protocols (e.g. Classes\zcode\shell\open\command)
                ScanProtocolHandlers(basePath, normalizedInstall, displayName, results);
            }

            // For each matched CLSID, also find related Interface keys via the reverse mapping
            foreach (var kvp in comEntries)
            {
                string clsid = kvp.Key;
                foreach (var iv in interfaceToClsid)
                {
                    if (iv.Value.Equals(clsid, StringComparison.OrdinalIgnoreCase))
                    {
                        // This Interface key's ProxyStubClsid32 points to our matched CLSID
                        // The interface key was already added during ScanComInterfaceEntries if matched
                        // but we can also add the top-level Interface\{guid} key itself
                        foreach (var root in classRoots)
                        {
                            string ifPath = $@"{root[0]}\Interface\{iv.Key}";
                            if (KeyExists(ifPath))
                                results.Add(ifPath);
                        }
                    }
                }
            }
        }

        private static bool KeyExists(string fullPath)
        {
            try
            {
                var hive = ResolveHive(fullPath, out string subKey);
                if (hive == null || string.IsNullOrEmpty(subKey)) return false;
                using var key = hive.OpenSubKey(subKey, false);
                return key != null;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        private static void ScanComClsidEntries(string baseClassesPath, string normalizedInstall, HashSet<string> results, Dictionary<string, string> comEntries)
        {
            try
            {
                using var clsidKey = RegistryToolsOpenKey($@"{baseClassesPath}\CLSID");
                if (clsidKey == null) return;

                foreach (var guid in clsidKey.GetSubKeyNames())
                {
                    if (string.IsNullOrEmpty(guid) || guid.Contains("-0000-") || guid[0] != '{')
                        continue;

                    try
                    {
                        using var guidKey = clsidKey.OpenSubKey(guid, false);
                        if (guidKey == null) continue;

                        string? filePath = null;

                        // InprocServer32 (DLL) - default value is the DLL path
                        using (var inprocKey = guidKey.OpenSubKey("InprocServer32"))
                        {
                            if (inprocKey != null)
                            {
                                string? val = inprocKey.GetValue(null) as string;
                                if (!string.IsNullOrEmpty(val))
                                    filePath = Environment.ExpandEnvironmentVariables(val).TrimEnd('\\');
                            }
                        }

                        // If no InprocServer32, try LocalServer32 (EXE)
                        if (string.IsNullOrEmpty(filePath))
                        {
                            using var localKey = guidKey.OpenSubKey("LocalServer32");
                            if (localKey != null)
                            {
                                string? val = localKey.GetValue(null) as string;
                                if (!string.IsNullOrEmpty(val))
                                {
                                    // LocalServer32 often has command-line args, extract the exe path
                                    string exePath = val.Split(' ')[0].Trim('"');
                                    filePath = Environment.ExpandEnvironmentVariables(exePath);
                                }
                            }
                        }

                        if (string.IsNullOrEmpty(filePath)) continue;

                        // Cheap path-prefix filter (path-based match, Revo-style). We do NOT require
                        // File.Exists here: stale CLSID entries pointing at a deleted exe are exactly
                        // the residuals this scanner must catch.
                        string normalizedFile = Path.GetFullPath(filePath).TrimEnd('\\');
                        if (!normalizedFile.StartsWith(normalizedInstall, StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Found a match - add all relevant COM keys for this GUID
                        comEntries[guid] = normalizedFile;

                        string clsidPath = $@"{baseClassesPath}\CLSID\{guid}";
                        results.Add(clsidPath);

                        // AppID
                        using (var appIdVal = guidKey.OpenSubKey("AppID"))
                        {
                            if (appIdVal != null)
                            {
                                string? appId = appIdVal.GetValue(null) as string;
                                if (!string.IsNullOrEmpty(appId))
                                    TryAddKey($@"{baseClassesPath}\AppID\{appId}", results);
                            }
                        }

                        // ProgID
                        using (var progIdKey = guidKey.OpenSubKey("ProgID"))
                        {
                            string? progId = progIdKey?.GetValue(null) as string;
                            if (!string.IsNullOrEmpty(progId))
                            {
                                TryAddKey($@"{baseClassesPath}\{progId}", results);
                                // Also check VersionIndependentProgID
                                TryAddKey($@"{baseClassesPath}\{progId}\CLSID", results);
                            }
                        }

                        // VersionIndependentProgID
                        using (var indepKey = guidKey.OpenSubKey("VersionIndependentProgID"))
                        {
                            string? indepProgId = indepKey?.GetValue(null) as string;
                            if (!string.IsNullOrEmpty(indepProgId))
                                TryAddKey($@"{baseClassesPath}\{indepProgId}", results);
                        }
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        private static void ScanComTypeLibEntries(string baseClassesPath, string normalizedInstall, HashSet<string> results, Dictionary<string, string> comEntries)
        {
            try
            {
                using var typeLibKey = RegistryToolsOpenKey($@"{baseClassesPath}\TypeLib");
                if (typeLibKey == null) return;

                foreach (var guid in typeLibKey.GetSubKeyNames())
                {
                    if (string.IsNullOrEmpty(guid) || guid.Contains("-0000-") || guid[0] != '{')
                        continue;

                    try
                    {
                        using var guidKey = typeLibKey.OpenSubKey(guid, false);
                        if (guidKey == null) continue;

                        // Get the first version subkey
                        string? version = guidKey.GetSubKeyNames().FirstOrDefault();
                        if (string.IsNullOrEmpty(version)) continue;

                        string? filePath = null;

                        // Try win64 then win32
                        foreach (var arch in new[] { "0\\win64", "0\\win32" })
                        {
                            using var fileKey = guidKey.OpenSubKey($@"{version}\{arch}");
                            if (fileKey != null)
                            {
                                string? val = fileKey.GetValue(null) as string;
                                if (!string.IsNullOrEmpty(val))
                                {
                                    filePath = Environment.ExpandEnvironmentVariables(val);
                                    break;
                                }
                            }
                        }

                        if (string.IsNullOrEmpty(filePath)) continue;

                        string normalizedFile = Path.GetFullPath(filePath).TrimEnd('\\');
                        if (!normalizedFile.StartsWith(normalizedInstall, StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Add the TypeLib key
                        string typeLibPath = $@"{baseClassesPath}\TypeLib\{guid}";
                        results.Add(typeLibPath);

                        // Check if a matching CLSID entry already exists
                        if (!comEntries.ContainsKey(guid))
                            comEntries[guid] = normalizedFile;
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        private static void ScanComInterfaceEntries(string baseClassesPath, HashSet<string> results, Dictionary<string, string> comEntries, Dictionary<string, string> interfaceToClsid)
        {
            try
            {
                using var interfaceKey = RegistryToolsOpenKey($@"{baseClassesPath}\Interface");
                if (interfaceKey == null) return;

                foreach (var ifGuid in interfaceKey.GetSubKeyNames())
                {
                    if (string.IsNullOrEmpty(ifGuid) || ifGuid.Contains("-0000-") || ifGuid[0] != '{')
                        continue;

                    try
                    {
                        using var ifGuidKey = interfaceKey.OpenSubKey(ifGuid, false);
                        if (ifGuidKey == null) continue;

                        using var proxyKey = ifGuidKey.OpenSubKey("ProxyStubClsid32");
                        string? proxyClsid = proxyKey?.GetValue(null) as string;
                        if (string.IsNullOrEmpty(proxyClsid)) continue;

                        // Check if this proxy CLSID matches any of our matched COM entries
                        if (comEntries.ContainsKey(proxyClsid))
                        {
                            // The interface is related to our app's COM entry
                            results.Add(ifGuidKey.Name);
                            interfaceToClsid[ifGuid] = proxyClsid;
                        }
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        /// <summary>
        /// Scans context-menu (right-click) shell handlers: Directory\shell, Drive\shell,
        /// *\shell and Directory\Background\shell. For each verb it checks the "command"
        /// default value; if the referenced exe lives inside the install folder the verb
        /// key is added. Generic path-based matching (Revo-style), no per-app GUIDs.
        /// </summary>
        private static void ScanContextMenuHandlers(string baseClassesPath, string normalizedInstall, string displayName, HashSet<string> results)
        {
            string[] contexts = { "Directory\\shell", "Drive\\shell", "*\\shell", "Directory\\Background\\shell" };

            foreach (var ctx in contexts)
            {
                using var shellKey = RegistryToolsOpenKey($@"{baseClassesPath}\{ctx}");
                if (shellKey == null) continue;

                foreach (var verb in shellKey.GetSubKeyNames())
                {
                    try
                    {
                        using var verbKey = shellKey.OpenSubKey(verb, false);
                        if (verbKey == null) continue;

                        using var commandKey = verbKey.OpenSubKey("command");
                        if (commandKey == null) continue;

                        string? cmd = commandKey.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(cmd) && CommandStringReferencesInstall(cmd, normalizedInstall))
                            results.Add($@"{baseClassesPath}\{ctx}\{verb}");
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
            }
        }

        /// <summary>
        /// Scans custom URL protocols (e.g. Classes\zcode). A matching protocol key is one
        /// whose "URL Protocol" value exists and whose "shell\open\command" default value
        /// references an exe inside the install folder.
        /// </summary>
        private static void ScanProtocolHandlers(string basePath, string normalizedInstall, string displayName, HashSet<string> results)
        {
            using var classesKey = RegistryToolsOpenKey(basePath);
            if (classesKey == null) return;

            // Protocol names look like "zcode" � short, alphanumeric. Guard against scanning
            // the tens of thousands of ProgID/extension keys that live under Classes.
            foreach (var name in classesKey.GetSubKeyNames())
            {
                if (string.IsNullOrEmpty(name) || name.Length > 32 || name.Contains(".") || name.Contains("\\"))
                    continue;

                try
                {
                    using var protoKey = classesKey.OpenSubKey(name, false);
                    if (protoKey == null) continue;

                    // A custom URL protocol declares "URL Protocol" (or its default rel https).
                    bool isUrlProtocol = protoKey.GetValueNames()
                        .Any(v => v.Equals("URL Protocol", StringComparison.OrdinalIgnoreCase));

                    if (!isUrlProtocol) continue;

                    using var commandKey = protoKey.OpenSubKey(@"shell\open\command", false);
                    if (commandKey == null) continue;

                    string? cmd = commandKey.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(cmd) && CommandStringReferencesInstall(cmd, normalizedInstall))
                        results.Add($@"{basePath}\{name}");
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
        }

        /// <summary>
        /// Returns true when a shell command string (e.g. a "command" default value such as
        /// "C:\...\app.exe" --args "%1") references a path inside the given install folder.
        /// First-token path matching is deliberately simple � the prefix check on the
        /// absolute path is what makes it safe against false positives.
        /// </summary>
        private static bool CommandStringReferencesInstall(string? cmd, string normalizedInstall)
        {
            if (string.IsNullOrEmpty(cmd)) return false;

            string expanded = Environment.ExpandEnvironmentVariables(cmd).Trim();
            if (expanded.Length == 0) return false;

            string[] tokens = expanded.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var tok in tokens)
            {
                string p = tok.Trim('"');
                if (p.Length == 0 || p.IndexOf(':') < 0) continue;
                try
                {
                    string full = Path.GetFullPath(p);
                    if (full.StartsWith(normalizedInstall, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { /* malformed path � skip */ }
            }
            return false;
        }

        /// <summary>
        /// Opens a registry key from a full path like "HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID".
        /// Returns null on any error.
        /// </summary>
        private static RegistryKey? RegistryToolsOpenKey(string fullPath)
        {
            try
            {
                var hive = ResolveHive(fullPath, out string subKey);
                if (hive == null || string.IsNullOrEmpty(subKey)) return null;
                return hive.OpenSubKey(subKey, false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return null; }
        }

        /// <summary>
        /// Adds a registry key path to results if it exists.
        /// </summary>
        private static void TryAddKey(string fullPath, HashSet<string> results)
        {
            try
            {
                var hive = ResolveHive(fullPath, out string subKey);
                if (hive == null || string.IsNullOrEmpty(subKey)) return;
                using var key = hive.OpenSubKey(subKey, false);
                if (key != null) results.Add(fullPath);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // -- Empty Directory / Questionable Name Detection ----------

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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // -- Quiet Uninstall String Generation ---------------------

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
                var msiMatch = NativeRegex.Match(uninstallString, @"\{[0-9A-Fa-f\-]{36}\}");
                if (msiMatch.Success)
                    return $"msiexec /x {msiMatch.Value} /qn /norestart";
                // Also check for /I{guid} pattern
                var iMatch = NativeRegex.Match(uninstallString, @"/I\{[0-9A-Fa-f\-]{36}\}");
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
                return $"{trimmed} -s -f1\"{installLocation}\\uninstall.iss\"";

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

        // -- Exit Code Interpretation ------------------------------

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

        // -- Child Process Monitoring ------------------------------

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

                foreach (var (pid, lastCpu, lastSample, _, _) in snapshot)
                {
                    try
                    {
                        using var proc = Process.GetProcessById(pid);
                        if (proc.HasExited) continue;
                        TimeSpan cpu = proc.TotalProcessorTime;
                        if ((now - lastSample).TotalMilliseconds >= thresholdMs &&
                            (cpu - lastCpu).TotalMilliseconds < 100)
                        {
                            // Process hasn't used meaningful CPU in threshold window
                            return false;
                        }
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return true;
        }

        private static List<(int pid, TimeSpan cpu, DateTime sample, DateTime startTime, int sessionId)> GetProcessTreeSnapshot(Process parent)
        {
            var list = new List<(int, TimeSpan, DateTime, DateTime, int)>();
            try
            {
                var now = DateTime.UtcNow;
                list.Add((parent.Id, parent.TotalProcessorTime, now, parent.StartTime, parent.SessionId));

                // Get children by checking processes with a PPID matching our parent
                int parentId = parent.Id;
                DateTime parentStart = parent.StartTime;
                int parentSession = parent.SessionId;
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
                        if (Math.Abs((p.StartTime - parentStart).TotalSeconds) < 120 &&
                            p.SessionId == parentSession)
                        {
                            list.Add((p.Id, p.TotalProcessorTime, now, p.StartTime, p.SessionId));
                        }
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return list;
        }
    }

    // -- Confidence Scoring --------------------------------------

    public static class Confidence
    {
        // Generic words that should never produce a strong match on their own
        private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "launcher", "player", "app", "apps", "service", "services", "client", "helper",
            "manager", "plugin", "addon", "add-on", "extension", "tool", "tools", "update",
            "updater", "setup", "installer", "config", "configuration", "runtime", "engine",
            "core", "helper", "daemon", "agent", "bridge", "connector", "desktop", "portable",
            "sdk", "api", "module", "middleware", "bridge", "driver", "panel", "control",
            "console", "launcher", "loader", "monitor", "task", "process", "wrapper",
            "x86", "x64", "win32", "win64", "windows", "32-bit", "64-bit"
        };

        public static int Generate(string displayName, string folderName)
        {
            if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(folderName)) return 0;

            // Fast native path: confidence_generate_ffi is the exact copy of this
            // implementation compiled in Rust (single FFI call vs 4 regex P/Invokes +
            // managed Sift4). Falls back to the managed code below when unavailable.
            if (UseNative)
            {
                try { return confidence_generate_ffi(displayName, folderName); }
                catch { /* fall through to managed */ }
            }

            // Reject if folderName is a single generic word (Launcher, Player, etc.)
            string folderTrimmed = folderName.Trim().Trim('.', ' ');
            if (GenericWords.Contains(folderTrimmed)) return 0;

            // Reject very short names (< 4 chars) unless exact match
            if (folderTrimmed.Length < 4) return 0;

            // Exact match (after rejecting generic words)
            if (displayName.Equals(folderName, StringComparison.OrdinalIgnoreCase)) return 100;
            if (displayName.StartsWith(folderName, StringComparison.OrdinalIgnoreCase)) return 90;
            if (folderName.StartsWith(displayName, StringComparison.OrdinalIgnoreCase)) return 85;

            string cleanDisplay = CleanName(displayName);
            string cleanFolder = CleanName(folderName);

            if (string.IsNullOrEmpty(cleanDisplay) || string.IsNullOrEmpty(cleanFolder)) return 0;
            if (cleanDisplay.Equals(cleanFolder, StringComparison.OrdinalIgnoreCase)) return 80;

            // BCU-style MatchStringToProductName:
            // Check if one contains the other (dodgy match)
            string displayLower = cleanDisplay.ToLowerInvariant();
            string folderLower = cleanFolder.ToLowerInvariant();
            bool dirToName = folderLower.Contains(displayLower);
            bool nameToDir = displayLower.Contains(folderLower);

            // Sift4 distance
            int dist = Sift4Distance(displayLower, folderLower, 5);
            int maxLen = Math.Max(displayLower.Length, folderLower.Length);
            if (maxLen == 0) return 0;

            if (dirToName || nameToDir)
            {
                // One contains the other - assess how much difference remains
                double ratio = 1.0 - (double)dist / maxLen;
                if (ratio >= 0.8) return 70;
                if (dist < maxLen / 3)
                {
                    // Difference is less than 1/3 of total length - still a reasonable match
                    int score = (int)((1.0 - (double)dist / maxLen) * 65);
                    return Math.Max(score, 50);
                }
                return 50; // dodgy match, below threshold
            }

            // Pure Sift4 distance match
            double siftRatio = 1.0 - (double)dist / maxLen;
            if (siftRatio >= 0.8) return 70;
            if (siftRatio >= 0.6 && dist < maxLen / 3) return 60;

            return 0;
        }

        // BCU-style: trim publisher from product name before matching
        // e.g. "Adobe AIR" with publisher "Adobe" -> trim to "AIR"
        // Prevents folder "Adobe" from falsely matching "Adobe AIR"
        public static int Generate(string displayName, string folderName, string publisher)
        {
            if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(folderName)) return 0;

            // Try with publisher-trimmed name first (BCU MatchStringToProductName pattern)
            if (!string.IsNullOrEmpty(publisher) && publisher.Length > 4)
            {
                string pubLower = publisher.Trim().ToLowerInvariant();
                string nameLower = displayName.Trim().ToLowerInvariant();
                if (nameLower.Contains(pubLower))
                {
                    string trimmed = nameLower.Replace(pubLower, "").Trim();
                    if (trimmed.Length > 4)
                    {
                        int trimmedScore = Generate(trimmed, folderName);
                        if (trimmedScore >= 70) return trimmedScore;
                    }
                }
            }

            // Fall back to standard matching
            return Generate(displayName, folderName);
        }

        private static string CleanName(string name)
        {
            name = NativeRegex.Replace(name, @"\s+(Inc|LLC|Ltd|Limited|Corp|Corporation|GmbH|SAS|SRL|SA|Pty|Ltee)\.?$", "");
            name = NativeRegex.Replace(name, @"\s*\([^)]*\)$", "");
            name = NativeRegex.Replace(name, @"[���]", "");
            name = NativeRegex.Replace(name, @"\s+", " ").Trim();
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

        // -- Rust native backend -------------------------------------
        private const string RustDll = "rust_native.dll";

        [DllImport(RustDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int sift4_distance_ffi(string s1, string s2, int maxOffset);

        [DllImport(RustDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int confidence_generate_ffi(string displayName, string folderName);

        private static readonly bool UseNative;

        static Confidence()
        {
            try
            {
                int test = sift4_distance_ffi("test", "test", 5);
                UseNative = (test == 0);
                if (UseNative)
                    Logger.Log("?? Rust native DLL carregada com sucesso!");
            }
            catch
            {
                UseNative = false;
                Logger.Log("?? Rust native DLL n�o encontrada � usando implementa��o C#");
            }
        }

        public static void Benchmark()
        {
            var pairs = new (string display, string folder)[]
            {
                ("Adobe Reader", "Adobe"),
                ("Google Chrome", "Google"),
                ("Mozilla Firefox", "Firefox"),
                ("Microsoft Office", "Microsoft Office"),
                ("Java Runtime", "Java"),
                ("Steam Client", "Steam"),
                ("Discord", "Discord"),
                ("Spotify Premium", "Spotify"),
                ("VLC Media Player", "VLC"),
                ("7-Zip", "7-Zip"),
                ("Notepad++", "NotepadPP"),
                ("WinRAR", "WinRAR"),
                ("Audacity", "Audacity"),
                ("FileZilla FTP", "FileZilla"),
                ("qBittorrent", "qBittorrent"),
                ("Visual Studio Code", "VSCode"),
                ("Git", "Git"),
                ("Python 3.12", "Python"),
                ("Node.js", "NodeJS"),
                ("Docker Desktop", "Docker"),
                ("Slack Technologies", "Slack"),
                ("Zoom", "Zoom"),
                ("TeamViewer", "TeamViewer"),
                ("Oracle VM VirtualBox", "VirtualBox"),
                ("VMware Workstation", "VMware"),
            };

            const int Iterations = 10000;

            // -- C# benchmark --
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                foreach (var p in pairs)
                {
                    int _ = Confidence.Generate(p.display, p.folder);
                }
            }
            sw.Stop();
            long csMs = sw.ElapsedMilliseconds;

            // -- Rust benchmark --
            if (!UseNative)
            {
                Logger.Log($"\n--- BENCHMARK ---");
                Logger.Log($"C#:     {Iterations} itera��es � {pairs.Length} pares = {csMs} ms");
                Logger.Log($"M�dia:  {csMs / (double)Iterations:F4} ms por itera��o");
                Logger.Log($"Rust:   DLL n�o dispon�vel � n�o foi poss�vel testar");
                return;
            }

            sw.Restart();
            for (int i = 0; i < Iterations; i++)
            {
                foreach (var p in pairs)
                {
                    int _ = confidence_generate_ffi(p.display, p.folder);
                }
            }
            sw.Stop();
            long rustMs = sw.ElapsedMilliseconds;

            Logger.Log($"\n--------------- BENCHMARK ---------------");
            Logger.Log($"Iters: {Iterations} � {pairs.Length} pares = {Iterations * pairs.Length} chamadas");
            Logger.Log($"");
            Logger.Log($"C#  Sift4:    {csMs,8} ms  |  {(double)Iterations * pairs.Length / csMs * 1000,8:F0} op/s");
            Logger.Log($"Rust native:  {rustMs,8} ms  |  {(double)Iterations * pairs.Length / rustMs * 1000,8:F0} op/s");
            Logger.Log($"");
            double ratio = (double)csMs / rustMs;
            Logger.Log($"Speedup:      {ratio:F2}�  (Rust � {(ratio >= 1 ? "mais r�pido" : "mais lento")})");
            Logger.Log($"===========================================");
        }
    }
}
