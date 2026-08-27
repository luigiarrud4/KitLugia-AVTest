// using IWshRuntimeLibrary; // Removed as it was causing build errors and appears unused
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.ServiceProcess;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

// Resolve ambiguidade entre System.IO.File e IWshRuntimeLibrary.File
using File = System.IO.File;
using Task = System.Threading.Tasks.Task;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static partial class SystemTweaks
    {
        // P/Invoke declarations for Power Management API (powrprof.dll)
        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        public static extern UInt32 PowerGetActiveScheme(IntPtr UserRootPowerKey, ref IntPtr ActivePolicyGuid);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        public static extern UInt32 PowerSetActiveScheme(IntPtr UserRootPowerKey, IntPtr Guid);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        public static extern UInt32 PowerWriteACValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, UInt32 AcValueIndex);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        public static extern UInt32 PowerWriteDCValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, UInt32 DcValueIndex);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        public static extern UInt32 PowerReadACValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, ref UInt32 AcValueIndex);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        public static extern UInt32 PowerReadDCValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, ref UInt32 DcValueIndex);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private static Guid? GetActivePowerSchemeGuid()
        {
            IntPtr activeSchemePtr = IntPtr.Zero;
            uint result = PowerGetActiveScheme(IntPtr.Zero, ref activeSchemePtr);
            if (result != 0 || activeSchemePtr == IntPtr.Zero)
                return null;
            try
            {
                return Marshal.PtrToStructure<Guid>(activeSchemePtr);
            }
            finally
            {
                if (activeSchemePtr != IntPtr.Zero)
                    LocalFree(activeSchemePtr);
            }
        }

        // REMOVIDO: Campos não utilizados (_gpuPnpDeviceId, _gpuName) para limpar warnings.

        #region Bloatware Logic
        private static readonly List<(string DisplayName, string PackageName, string StoreId)> _knownBloatware = new()
        {
            // Xbox & Gaming
            ("Xbox Game Bar", "*Microsoft.XboxGamingOverlay*", "9NZKPSTSNW4P"),
            ("Xbox App", "*Microsoft.XboxApp*", "9MV0B5HZVK9Z"),
            ("Xbox Identity Provider", "*Microsoft.XboxIdentityProvider*", "9WZDNCRFJXM"),
            ("Xbox Speech to Text Overlay", "*Microsoft.XboxSpeechToTextOverlay*", "9P4RC1NM5QWV"),
            ("Xbox TCUI", "*Microsoft.Xbox.TCUI*", "9NBLGGH4R2R8"),
            ("Xbox Gaming Overlay", "*Microsoft.XboxGamingOverlay_5.721.10202.0_neutral_*", "9NZKPSTSNW4P"),
            ("Microsoft Gaming App", "*Microsoft.GamingApp*", "9WZDNCRFJ3QV"),
            
            // Microsoft 365 & Office
            ("Microsoft Office Hub", "*Microsoft.MicrosoftOfficeHub*", "9WZDNCRFJ4P2"),
            ("OneNote", "*Microsoft.Office.OneNote*", "9WZDNCRFJ3Q1"),
            ("Microsoft Office Sway", "*Microsoft.Office.Sway*", "9WZDNCRFJ3Q3"),
            ("Microsoft Office Todo List", "*Microsoft.Office.Todo.List*", "9WZDNCRFJ3Q4"),
            
            // System & Utilities
            ("Cortana", "*Microsoft.549981C3F5F10*", "9NFFX4SZZ23L"),
            ("Feedback Hub", "*Microsoft.WindowsFeedbackHub*", "9NBLGHH4R32N"),
            ("Dicas", "*Microsoft.Getstarted*", "9WZDNCRFJ3Q2"),
            ("3D Viewer", "*Microsoft.Microsoft3DViewer*", "9NBLGGH42THS"),
            ("Paint 3D", "*Microsoft.MSPaint*", "9NBLGGH5F2XM"),
            ("Print 3D", "*Microsoft.Print3D*", "9NBLGGH5G2X3"),
            ("Mixed Reality Portal", "*Microsoft.MixedReality.Portal*", "9NBLGGH4QZ2W"),
            ("Get Help", "*Microsoft.GetHelp*", "9NBLGGH0Q7J0"),
            ("Network Speed Test", "*Microsoft.NetworkSpeedTest*", "9NBLGGH0Q7J0"),
            
            // Communication
            ("Mail e Calendário", "*microsoft.windowscommunicationsapps*", "9wzdncrfhvqm"),
            ("Vínculo Móvel (Seu Telefone)", "*Microsoft.YourPhone*", "9NMPJ99VJbwv"),
            ("Skype", "*Microsoft.SkypeApp*", "9WZDNCRDFWBT"),
            ("People", "*Microsoft.People*", "9NBLGGH10PG8"),
            ("Microsoft Teams", "*MicrosoftTeams*", "9WZDNCRFJ3Q9"),
            ("Teams Machine-Wide Installer", "*TeamsMachine-WideInstaller*", "9WZDNCRFJ3Q9"),
            ("Messaging", "*Microsoft.Messaging*", "9WZDNCRFJ3Q5"),
            ("OneConnect", "*Microsoft.OneConnect*", "9WZDNCRFJ3Q6"),
            
            // Media & Entertainment
            ("Groove Music", "*Microsoft.ZuneMusic*", "9WZDNCRFJ3PT"),
            ("Filmes e TV", "*Microsoft.ZuneVideo*", "9WZDNCRFJ3P2"),
            ("Sticky Notes", "*Microsoft.MicrosoftStickyNotes*", "9NBLGGH4QGHW"),
            ("Gravador de Voz", "*Microsoft.WindowsSoundRecorder*", "9WZDNCRFHWKN"),
            ("Disney+", "*Disney.37853FC22B2CE*", "9NBLGGH5Q1VQ"),
            ("Spotify", "*SpotifyAB.SpotifyMusic*", "9NBLGGH4R2Q8"),
            
            // Utilities - IDs corrigidos
            ("Mapas", "*Microsoft.WindowsMaps*", "9WZDNCRFJ1VW"),
            ("Clima", "*Microsoft.BingWeather*", "9WZDNCRFJ3Q1"),
            ("Notícias", "*Microsoft.BingNews*", "9WZDNCRFHVJ1"),
            
            // Windows 11 Specific - IDs corrigidos
            ("Windows Alarms", "*Microsoft.WindowsAlarms*", "9WZDNCRFJ3P8"),
            ("Windows Camera", "*Microsoft.WindowsCamera*", "9WZDNCRFJ3PX"),
            ("Microsoft Solitaire Collection", "*Microsoft.MicrosoftSolitaireCollection*", "9WZDNCRFJ3Q0"),
            ("Windows Calculator", "*Microsoft.WindowsCalculator*", "9WZDNCRFJ3Q7"),
            ("Windows Photos", "*Microsoft.Windows.Photos*", "9WZDNCRFJ3Q8"),
            ("Windows Store", "*Microsoft.WindowsStore*", "9WZDNCRFJ3Q9"),
            ("Windows Whiteboard", "*Microsoft.Whiteboard*", "9MSNXRGSKJ2LH"),
            ("Windows Clock", "*Microsoft.WindowsClock*", "9WZDNCRFJ3QX"),
            ("Windows Terminal", "*Microsoft.WindowsTerminal*", "9N0DX20HK8R1"),
            
            // New 2024 Apps - IDs verificados
            ("Windows Copilot", "*Microsoft.Copilot*", "9P4RC1NM5QWV"),
            ("Microsoft To Do", "*Microsoft.Todos*", "9WZDNCRFJ3R1"),
            ("Microsoft Power Automate", "*Microsoft.PowerAutomateDesktop*", "9NXX1M8R2BN"),
            ("Microsoft Family Safety", "*MicrosoftCorporationII.MicrosoftFamily*", "9NBLGGH0R7JF"),
            ("Microsoft Start", "*Microsoft.Windows.StartMenuExperienceHost*", "9NBLGGH0Q7JF"),
            
            // Third Party Apps (comuns em Windows 11)
            ("Adobe Photoshop Express", "*AdobeSystemsIncorporated.AdobePhotoshopExpress*", "9NBLGGH4R2R8"),
            ("Duolingo", "*Duolingo-LearnLanguagesforFree*", "9NBLGGH0F0G2"),
            ("Pandora", "*PandoraMediaInc*", "9NBLGGH0F0G2"),
            ("Candy Crush", "*CandyCrush*", "9NBLGGH0F0G2"),
            ("Bubble Witch 3 Saga", "*BubbleWitch3Saga*", "9NBLGGH0F0G2"),
            ("Wunderlist", "*Wunderlist*", "9NBLGGH0F0G2"),
            ("Flipboard", "*Flipboard*", "9NBLGGH0F0G2"),
            ("Twitter", "*Twitter*", "9NBLGGH0F0G2"),
            ("Facebook", "*Facebook*", "9NBLGGH0F0G2"),
            ("Minecraft", "*Minecraft*", "9NBLGGH0F0G2"),
            ("Royal Revolt", "*RoyalRevolt*", "9NBLGGH0F0G2"),
            ("Clipchamp", "*clipchamp.clipchamp*", "9NBLGGH0F0G2"),
            ("Dolby", "*Dolby*", "9NBLGGH0F0G2"),
            ("Eclipse Manager", "*EclipseManager*", "9NBLGGH0F0G2"),
            ("Actipro Software", "*ActiproSoftwareLLC*", "9NBLGGH0F0G2"),
            
            // Windows 11 Widgets & Features
            ("Widgets", "*MicrosoftWindows.Client.WebExperience*", "9NBLGGH0F0G2"),
            ("Windows Ink Workspace", "*Microsoft.WindowsInkWorkspace*", "9NBLGGH0F0G2"),
            ("Quick Assist", "*Microsoft.WindowsQuickAssist*", "9NBLGGH0F0G2"),
            ("Windows Security", "*Microsoft.Windows.SecHealthUI*", "9WZDNCRFJ3QW"),
            ("Your Phone", "*Microsoft.YourPhone*", "9NMPJ99VJbwv"),
            
            // Developer Tools (se presentes)
            ("Windows Subsystem for Linux", "*MicrosoftCorporation.WindowsLinux*", "9PKN3CXW1H4W"),
            ("PowerShell", "*Microsoft.PowerShell*", "9MZ1TN974CFS2"),
            ("Windows Terminal Preview", "*Microsoft.WindowsTerminalPreview*", "9N0DX20HK8R1"),
            
            // Microsoft Store Apps
            ("Microsoft Store Purchase App", "*Microsoft.StorePurchaseApp*", "9WZDNCRFJ3QV"),
            ("Microsoft Store", "*Microsoft.WindowsStore*", "9WZDNCRFJ3Q9"),
            ("Microsoft Update Health Tools", "*Microsoft.UpdateHealthTools*", "9WZDNCRFJ3QV"),
            ("Microsoft Intune Management Extension", "*Microsoft.IntuneManagementExtension*", "9WZDNCRFJ3QV"),
            ("Microsoft Edge WebView2 Runtime", "*Microsoft.MicrosoftEdgeWebView2Runtime*", "9WZDNCRFJ3QV"),
            ("Microsoft Edge", "*Microsoft.MicrosoftEdge*", "9WZDNCRFJ3QV"),
            ("Microsoft Edge Update", "*Microsoft.MicrosoftEdgeUpdate*", "9WZDNCRFJ3QV")
        };

        // UWP packages that must NEVER be listed for removal (system-critical components)
        private static readonly HashSet<string> ProtectedUwpPackages = new(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft.WindowsStore",
            "Microsoft.DesktopAppInstaller",
            "Microsoft.Windows.ShellExperienceHost",
            "Microsoft.Windows.StartMenuExperienceHost",
            "Microsoft.AAD.BrokerPlugin",
            "Microsoft.AccountsControl",
            "Microsoft.BioEnrollment",
            "Microsoft.Windows.CloudExperienceHost",
            "Microsoft.Windows.ContentDeliveryManager",
            "Microsoft.AsyncTextService",
            "Microsoft.LockApp",
            "Microsoft.Win32WebViewHost",
            "Microsoft.ECApp",
            "Microsoft.Windows.Apprep.ChxApp",
            "Microsoft.Windows.AssignedAccessLockApp",
            "Microsoft.Windows.CapturePicker",
            "Microsoft.Windows.ParentalControls",
            "Microsoft.Windows.PeopleExperienceHost",
            "Microsoft.Windows.PrintExperienceHost",
            "Microsoft.Windows.PurchaseDialog",
            "Microsoft.Windows.SecHealthUI",
            "Microsoft.Windows.SecondaryTileExperience",
            "Microsoft.Windows.SecureAssessmentBrowser",
            "Microsoft.Windows.AppResolverUX",
            "Microsoft.Windows.NarratorQuickStart",
            "Microsoft.Windows.OOBENetworkCaptivePort",
            "Microsoft.Windows.OOBENetworkConnectionFlow",
            "Microsoft.CredDialogHost",
            "Microsoft.Windows.PinningConfirmationDialog",
            "Microsoft.Windows.PrintQueueActionCenter",
            "Microsoft.XboxGameCallableUI",
            "Windows.CBSPreview",
            "windows.immersivecontrolpanel",
            "Windows.PrintDialog",
            "Microsoft.Windows.XGpuEjectDialog",
            "MicrosoftWindows.UndockedDevKit",
            "MicrosoftWindows.Client.Photon",
            "MicrosoftWindows.WindowsSandbox",
            "MicrosoftWindows.Speech.pt-BR.1",
            "MicrosoftWindows.Client.CBS",
            "MicrosoftWindows.Client.Core",
            "MicrosoftWindows.Client.FileExp",
            "MicrosoftWindows.Client.OOBE",
            "Microsoft.StartExperiencesApp",
            "MicrosoftWindows.57242383.Tasbar",
            "Microsoft.MicrosoftEdgeDevToolsClient",
            "1527c705-839a-4832-9118-54d4Bd6a0c89",
            "E2A4F912-2574-4A75-9BB0-0D023378592B",
            "c5e2524a-ea46-4f67-841f-6a9465d9d515",
            "F46D4000-FD22-4DB4-AC8E-4E1DDDE828FE",
        };

        public static List<BloatwareApp> GetBloatwareAppsStatus()
        {
            var appStatuses = new List<BloatwareApp>();
            try
            {
                var pm = new Windows.Management.Deployment.PackageManager();
                var packages = pm.FindPackagesForUser("");

                foreach (var package in packages)
                {
                    try
                    {
                        string name = package.Id.Name;
                        string fullName = package.Id.FullName;
                        string publisher = package.Id.Publisher ?? "";

                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(fullName)) continue;

                        if (package.IsFramework || package.IsResourcePackage) continue;
                        if (ProtectedUwpPackages.Contains(name)) continue;

                        string displayName = GetUwpDisplayName(package) ?? name;

                        var nameBuilder = new StringBuilder(name);
                        nameBuilder.Replace("Microsoft.", "");
                        nameBuilder.Replace("Windows.", "");
                        nameBuilder.Replace("MicrosoftCorporationII.", "");
                        nameBuilder.Replace("Corporation", "");
                        nameBuilder.Replace("com.", "");
                        nameBuilder.Replace(".app", "");
                        string generatedName = nameBuilder.ToString().Trim();
                        if (string.IsNullOrWhiteSpace(generatedName)) generatedName = name;

                        string friendlyName;
                        if (!string.IsNullOrWhiteSpace(displayName) &&
                            !displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) &&
                            !displayName.StartsWith("@{", StringComparison.OrdinalIgnoreCase))
                        {
                            friendlyName = displayName;
                        }
                        else
                        {
                            friendlyName = generatedName;
                        }

                        string aumid = BuildAumid(package) ?? "";
                        if (!string.IsNullOrEmpty(aumid))
                        {
                            var startApps = GetStartAppsFriendlyNames();
                            if (startApps.TryGetValue(aumid, out var sa) && !string.IsNullOrWhiteSpace(sa))
                                friendlyName = sa;
                        }

                        appStatuses.Add(new BloatwareApp
                        {
                            DisplayName = friendlyName,
                            PackageName = fullName,
                            Aumid = aumid,
                            IsInstalled = true,
                            StoreId = "",
                            Icon = null,
                            Publisher = publisher,
                            Size = "",
                            InstallDate = ""
                        });
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return appStatuses.OrderBy(a => a.DisplayName).ToList();
        }

        private static string? GetUwpDisplayName(Windows.ApplicationModel.Package package)
        {
            try
            {
                string manifestPath = Path.Combine(package.InstalledLocation.Path, "AppxManifest.xml");
                if (File.Exists(manifestPath))
                {
                    var doc = XDocument.Load(manifestPath);
                    var root = doc.Root;
                    if (root == null) return null;
                    var ns = root.GetDefaultNamespace();
                    var displayName = root
                        .Element(ns + "Properties")?
                        .Element(ns + "DisplayName");
                    if (displayName != null && !string.IsNullOrWhiteSpace(displayName.Value))
                    {
                        string val = displayName.Value.Trim();
                        if (!val.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) &&
                            !val.StartsWith("@{", StringComparison.OrdinalIgnoreCase))
                            return val;
                    }
                }
            }
            catch { /* pacote UWP com manifesto inacessível/instalação quebrada — nome do fallback GetStartAppsFriendlyNames cobre */ }
            return null;
        }

        /// <summary>
        /// Constrói o AUMID (App User Model ID) do pacote: FamilyName!AppId.
        /// O AppId vem do elemento &lt;Application Id="..."&gt; do AppxManifest.xml.
        /// </summary>
        private static string? BuildAumid(Windows.ApplicationModel.Package package)
        {
            try
            {
                string family = package.Id.FamilyName;
                if (string.IsNullOrWhiteSpace(family)) return null;

                string manifestPath = Path.Combine(package.InstalledLocation.Path, "AppxManifest.xml");
                if (!File.Exists(manifestPath)) return null;

                var doc = XDocument.Load(manifestPath);
                var root = doc.Root;
                if (root == null) return null;

                var ns = root.GetDefaultNamespace();
                string? appId = root
                    .Element(ns + "Applications")?
                    .Elements(ns + "Application")
                    .Select(a => (string?)a.Attribute("Id"))
                    .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
                if (string.IsNullOrWhiteSpace(appId)) return null;

                return $"{family}!{appId}";
            }
            catch { /* pacote UWP com manifesto inacessível — AUMID cai para null */ return null; }
        }

        private static Dictionary<string, string>? _startAppsFriendlyNames;
        private static readonly object StartAppsLock = new();

        /// <summary>
        /// Cache do nome amigável real (menu Iniciar) por AUMID via Get-StartApps.
        /// </summary>
        private static Dictionary<string, string> GetStartAppsFriendlyNames()
        {
            if (_startAppsFriendlyNames != null) return _startAppsFriendlyNames;
            lock (StartAppsLock)
            {
                if (_startAppsFriendlyNames != null) return _startAppsFriendlyNames;

                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    using var p = new Process
                    {
                        StartInfo = new ProcessStartInfo("powershell.exe",
                            "-NoProfile -NonInteractive -Command \"Get-StartApps | ConvertTo-Json -Compress\"")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }
                    };
                    p.Start();
                    string output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(15000))
                    {
                        try { p.Kill(); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }

                    using var doc = JsonDocument.Parse(output);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            if (item.TryGetProperty("AppID", out var idEl) && idEl.ValueKind == JsonValueKind.String &&
                                item.TryGetProperty("Name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                            {
                                string id = idEl.GetString() ?? "";
                                string nm = nameEl.GetString() ?? "";
                                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(nm))
                                    map[id] = nm;
                            }
                        }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                _startAppsFriendlyNames = map;
                return map;
            }
        }

        public static (bool Success, string Message) RemoveBloatwareApp(string packageFullName)
        {
            try
            {
                DeepUninstaller.KillProcessesForApp(packageFullName);

                var pm = new Windows.Management.Deployment.PackageManager();
                // FIX: duplo bloqueio (Task.Run + GetResult dentro e fora) era redundante.
                // RemovePackageAsync é chamado direto — o método já é síncrono por natureza (chamador espera).
                var removeOp = pm.RemovePackageAsync(packageFullName);
                using var done = new System.Threading.ManualResetEvent(false);
                removeOp.Completed = (op, _) => done.Set();
                _ = System.Threading.ThreadPool.RegisterWaitForSingleObject(done,
                    (_, __) => done.Set(), null, System.Threading.Timeout.Infinite, true);
                done.WaitOne(180_000); // timeout 3min: desinstalação de appx pode ser lenta
                if (removeOp.Status == Windows.Foundation.AsyncStatus.Error)
                    return (false, "Falha ao remover pacote: " + (removeOp.ErrorCode?.Message ?? "erro desconhecido"));

                return (true, "Removido com sucesso");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public static (bool Success, string Message) DeepRemoveBloatwareApp(string packageFullName, string displayName)
        {
            try
            {
                DeepUninstaller.KillProcessesForApp(displayName, extraExeNames: new List<string> { packageFullName.Split('_')[0] });

                string packageNameBase = packageFullName.Split('_')[0];

                // Remove package via native API
                try
                {
                    var pm = new Windows.Management.Deployment.PackageManager();
                    var removeOp2 = pm.RemovePackageAsync(packageFullName);
                    using var done2 = new System.Threading.ManualResetEvent(false);
                    removeOp2.Completed = (op, _) => done2.Set();
                    done2.WaitOne(180_000);
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                // Remove leftover files from LocalAppData
                try
                {
                    string localPackages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
                    if (Directory.Exists(localPackages))
                    {
                        foreach (var dir in Directory.GetDirectories(localPackages, $"{packageNameBase}*"))
                        {
                            try { Directory.Delete(dir, true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                        }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                // Remove leftover files from WindowsApps
                try
                {
                    string winApps = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        "WindowsApps");
                    if (Directory.Exists(winApps))
                    {
                        foreach (var dir in Directory.GetDirectories(winApps, $"*{packageNameBase}*"))
                        {
                            string dirName = Path.GetFileName(dir);
                            bool isProtected = false;
                            foreach (var pkg in ProtectedUwpPackages)
                            {
                                if (dirName.Contains(pkg, StringComparison.OrdinalIgnoreCase))
                                { isProtected = true; break; }
                            }
                            if (dirName.Contains("Microsoft.VCLibs", StringComparison.OrdinalIgnoreCase) ||
                                dirName.Contains("Microsoft.NET", StringComparison.OrdinalIgnoreCase) ||
                                dirName.Contains("Microsoft.UI.Xaml", StringComparison.OrdinalIgnoreCase))
                            { isProtected = true; }

                            if (!isProtected)
                            {
                                try { Directory.Delete(dir, true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                            }
                        }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                // Remove provisioned package via WMI
                try
                {
                    using var searcher = new ManagementObjectSearcher(
                        $"SELECT * FROM Win32_ProvisionedApp WHERE PackageName LIKE '%{packageNameBase.Replace("'", "''")}%'");
                    foreach (ManagementObject app in searcher.Get())
                    {
                        try
                        {
                            using var process = new Process
                            {
                                StartInfo = new ProcessStartInfo
                                {
                                    FileName = "dism",
                                    Arguments = $"/online /remove-provisionedappxpackage /packagename:{app["PackageName"]} /quiet",
                                    UseShellExecute = false,
                                    CreateNoWindow = true
                                }
                            };
                            process.Start();
                            process.WaitForExit(30000);
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                return (true, "Deep Uninstall concluído com sucesso");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public static async System.Threading.Tasks.Task<(bool Success, string Message)> RemoveBloatwareAppAsync(string packageFullName)
        {
            try
            {
                await Task.Run(() => DeepUninstaller.KillProcessesForApp(packageFullName));

                var pm = new Windows.Management.Deployment.PackageManager();
                await pm.RemovePackageAsync(packageFullName);

                return (true, "Removido com sucesso");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public static async System.Threading.Tasks.Task<(bool Success, string Message)> DeepRemoveBloatwareAppAsync(string packageFullName, string displayName)
        {
            try
            {
                await Task.Run(() => DeepUninstaller.KillProcessesForApp(displayName, extraExeNames: new List<string> { packageFullName.Split('_')[0] }));

                string packageNameBase = packageFullName.Split('_')[0];
                var errors = new List<string>();

                // 1) Remove via PackageManager — verifica o RESULTADO real (antes era engolido
                //    e o app sempre reportava sucesso mesmo sem remover nada).
                try
                {
                    var pm = new Windows.Management.Deployment.PackageManager();

                    // RemovePackageAsync (usuário atual) — se falhar, fallback via PowerShell.
                    bool removed = false;
                    string? lastError = null;
                    try
                    {
                        var op = pm.RemovePackageAsync(packageFullName);
                        var dr = await op;
                        if (dr != null && !string.IsNullOrWhiteSpace(dr.ErrorText))
                        {
                            lastError = dr.ErrorText;
                        }
                        else
                        {
                            removed = true;
                        }
                    }
                    catch (Exception ex) { lastError = ex.Message; }

                    if (!removed)
                    {
                        errors.Add($"RemovePackageAsync: {lastError ?? "falha desconhecida"}");

                        // Fallback 2: PowerShell Remove-AppxPackage -AllUsers (robusto p/ apps provisionados)
                        try
                        {
                            string ps = $"Remove-AppxPackage -AllUsers -Package '{packageFullName.Replace("'", "''")}' -ErrorAction Stop";
                            using var proc = new Process
                            {
                                StartInfo = new ProcessStartInfo
                                {
                                    FileName = "powershell.exe",
                                    Arguments = $"-NoProfile -Command \"{ps}\"",
                                    UseShellExecute = false,
                                    CreateNoWindow = true,
                                    RedirectStandardOutput = true,
                                    RedirectStandardError = true
                                }
                            };
                            proc.Start();
                            string err = await proc.StandardError.ReadToEndAsync();
                            await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
                            if (proc.ExitCode != 0)
                                errors.Add($"Remove-AppxPackage: {err.Trim()}");
                        }
                        catch (Exception ex) { errors.Add($"Remove-AppxPackage: {ex.Message}"); }
                    }
                }
                catch (Exception ex) { errors.Add($"PackageManager: {ex.Message}"); }

                await Task.Delay(800);

                // 2) Limpar pasta de dados %LocalAppData%\Packages\<base>*
                try
                {
                    string localPackages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
                    if (Directory.Exists(localPackages))
                    {
                        foreach (var dir in Directory.GetDirectories(localPackages, $"{packageNameBase}*"))
                        {
                            try { Directory.Delete(dir, true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                        }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                // 3) Limpar pasta em WindowsApps
                try
                {
                    string winApps = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        "WindowsApps");
                    if (Directory.Exists(winApps))
                    {
                        foreach (var dir in Directory.GetDirectories(winApps, $"*{packageNameBase}*"))
                        {
                            string dirName = Path.GetFileName(dir);
                            bool isProtected = false;
                            foreach (var pkg in ProtectedUwpPackages)
                            {
                                if (dirName.Contains(pkg, StringComparison.OrdinalIgnoreCase))
                                { isProtected = true; break; }
                            }
                            if (dirName.Contains("Microsoft.VCLibs", StringComparison.OrdinalIgnoreCase) ||
                                dirName.Contains("Microsoft.NET", StringComparison.OrdinalIgnoreCase) ||
                                dirName.Contains("Microsoft.UI.Xaml", StringComparison.OrdinalIgnoreCase))
                            { isProtected = true; }

                            if (!isProtected)
                            {
                                try { Directory.Delete(dir, true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                            }
                        }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                // 4) Remover provisionamento (DISM) — apps pré-instalados voltam se não fizer isso
                try
                {
                    using var searcher = new ManagementObjectSearcher(
                        $"SELECT * FROM Win32_ProvisionedApp WHERE PackageName LIKE '%{packageNameBase.Replace("'", "''")}%'");
                    foreach (ManagementObject app in searcher.Get())
                    {
                        try
                        {
                            using var process = new Process
                            {
                                StartInfo = new ProcessStartInfo
                                {
                                    FileName = "dism",
                                    Arguments = $"/online /remove-provisionedappxpackage /packagename:{app["PackageName"]} /quiet",
                                    UseShellExecute = false,
                                    CreateNoWindow = true,
                                    RedirectStandardError = true
                                }
                            };
                            process.Start();
                            string err = await process.StandardError.ReadToEndAsync();
                            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                // 5) VERIFICAÇÃO REAL: o pacote ainda existe? (FindPackages casa por PACKAGE NAME, não FullName)
                bool stillInstalled = false;
                try
                {
                    var pm = new Windows.Management.Deployment.PackageManager();
                    var found = pm.FindPackages(packageNameBase).FirstOrDefault()
                        ?? pm.FindPackagesForUser("", packageNameBase).FirstOrDefault();
                    stillInstalled = found != null;
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                if (stillInstalled && errors.Count == 0)
                    errors.Add("O pacote ainda está registrado após a remoção");

                if (errors.Count > 0)
                    return (false, string.Join(" | ", errors));

                return (true, "Deep Uninstall concluído com sucesso");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // ReinstallBloatwareApp REMOVIDO — não há mais integração com Microsoft Store
        public static void ReinstallBloatwareApp(string storeId)
        {
            // Método mantido vazio para compatibilidade, sem abrir a Store
        }
        #endregion

        #region Registry Tweaks (UI & General)
        public static (bool Success, string Message, bool IsNowEnabled) ToggleRegistryTweak(string keyPath, string valueName, int enabledValue, int disabledValue, bool isHKLM, string tweakName)
        {
            try
            {
                RegistryKey baseKey = isHKLM ? Registry.LocalMachine : Registry.CurrentUser;
                string subKeyPath = keyPath.Replace(isHKLM ? @"HKEY_LOCAL_MACHINE\" : @"HKEY_CURRENT_USER\", "");

                using (var checkKey = baseKey.OpenSubKey(subKeyPath))
                {
                    object? val = checkKey?.GetValue(valueName);
                    bool isEnabled = val != null && Convert.ToInt32(val) == enabledValue;

                    if (isEnabled) // Reverter
                    {
                        if (keyPath.Contains(@"\Policies\"))
                        {
                            using var key = baseKey.OpenSubKey(subKeyPath, true); key?.DeleteValue(valueName, false);
                        }
                        else
                        {
                            Registry.SetValue(keyPath, valueName, disabledValue, RegistryValueKind.DWord);
                        }
                        return (true, $"{tweakName} revertido.", false);
                    }
                }

                using (var key = baseKey.CreateSubKey(subKeyPath, true)) key.SetValue(valueName, enabledValue, RegistryValueKind.DWord);
                return (true, $"{tweakName} ativado.", true);
            }
            catch (Exception ex) { return (false, ex.Message, false); }
        }

        public static (bool Success, string Message) RevertPolicyTweak(string keyPath, string valueName, bool isHKLM)
        {
            try
            {
                RegistryKey baseKey = isHKLM ? Registry.LocalMachine : Registry.CurrentUser;
                string subKeyPath = keyPath.Replace(isHKLM ? @"HKEY_LOCAL_MACHINE\" : @"HKEY_CURRENT_USER\", "");
                using (var key = baseKey.OpenSubKey(subKeyPath, true))
                {
                    key?.DeleteValue(valueName, false);
                }
                return (true, $"Política '{valueName}' revertida.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao reverter política '{valueName}': {ex.Message}");
            }
        }

        public static void RevertRegistryValue(string k, string v)
        {
            try
            {
                string sub = k.Replace(@"HKEY_LOCAL_MACHINE\", "").Replace(@"HKEY_CURRENT_USER\", "");
                RegistryKey baseKey = k.StartsWith("HKEY_LOCAL") ? Registry.LocalMachine : Registry.CurrentUser;
                using var r = baseKey.OpenSubKey(sub, true); r?.DeleteValue(v, false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static bool IsLastClickInstalled() => (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LastActiveClick", 0) == 1;
        public static void ApplyLastClickTweak() => Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LastActiveClick", 1, RegistryValueKind.DWord);

        public static bool IsBingDisabled() => (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 0) == 1;
        public static void ApplyBingTweak() => Registry.SetValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, RegistryValueKind.DWord);

        public static bool IsWin10ContextEnabled() => Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae252}") != null;
        public static void ApplyWin10ContextTweak(bool enable)
        {
            try
            {
                if (enable)
                {
                    using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae252}\InprocServer32");
                    key.SetValue("", "");
                }
                else
                {
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae252}", false);
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static bool IsMemoryUsageEnabled() => (int?)Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "MemoryUsage", 0) == 2;
        public static (bool Success, string Message) ToggleMemoryUsage()
        {
            try
            {
                if (IsMemoryUsageEnabled())
                {
                    Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "MemoryUsage", 1, RegistryValueKind.DWord);
                    return (true, "MemoryUsage Restaurado (Padrão).");
                }
                else
                {
                    Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "MemoryUsage", 2, RegistryValueKind.DWord);
                    return (true, "MemoryUsage Otimizado (Fsutil).");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }
        public static bool IsHddFixEnabled() => SystemUtils.GetServiceStartMode("SysMain") == "Disabled";
        public static bool IsSegmentHeapEnabled() => (int?)Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Segment Heap", "Enabled", 0) == 1;
        public static bool IsLargeCacheEnabled() => (int?)Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "LargeSystemCache", 0) == 1;

        public static void ApplyAutoCacheTweak()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT L2CacheSize FROM Win32_Processor");
                foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
                {
                    using (obj)
                    {
                        var cacheSize = obj["L2CacheSize"];
                        if (cacheSize != null)
                        {
                            Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "SecondLevelDataCache", Convert.ToInt64(cacheSize), RegistryValueKind.DWord);
                            break;
                        }
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static void ApplyExtremeVisuals()
        {
            try
            {
                byte[] maskValue = new byte[] { 0x90, 0x12, 0x03, 0x80, 0x10, 0x00, 0x00, 0x00 };
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
                {
                    if (key != null)
                    {
                        key.SetValue("UserPreferencesMask", maskValue, RegistryValueKind.Binary);
                        key.SetValue("MenuShowDelay", "0", RegistryValueKind.String);
                        key.SetValue("DragFullWindows", "0", RegistryValueKind.String);
                    }
                }
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", RegistryValueKind.String);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static bool IsExtremeVisualsApplied()
        {
            try
            {
                object? value = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "UserPreferencesMask", null);
                if (value is byte[] current && current?.Length >= 4)
                {
                    byte[] expected = new byte[] { 0x90, 0x12, 0x03, 0x80 };
                    for (int i = 0; i < 4; i++)
                    {
                        if (current[i] != expected[i]) return false;
                    }
                    return true;
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return false;
        }
        #endregion

        #region Performance & System

        public static (bool Success, string Message, long FreedMemory) OptimizeMemory()
        {
            try
            {
                // Collect .NET managed memory first
                GC.Collect();
                GC.WaitForPendingFinalizers();

                // Deep clean using NtSetSystemInformation (Mem Reduct engine)
                var result = MemoryOptimizer.Optimize();
                return (result.Success, result.Message, 0);
            }
            catch (Exception ex)
            {
                return (false, ex.Message, 0);
            }
        }

        public static bool IsVbsEnabled() => (int?)Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 0) == 1;
        public static (bool Success, string Message) ToggleVbs()
        {
            try
            {
                string p = @"SYSTEM\CurrentControlSet\Control\DeviceGuard";
                using var k = Registry.LocalMachine.CreateSubKey(p, true);
                if (k == null) return (false, "Não foi possível acessar a chave do registro.");
                int nv = (int)(k.GetValue("EnableVirtualizationBasedSecurity", 0) ?? 0) == 1 ? 0 : 1;
                k.SetValue("EnableVirtualizationBasedSecurity", nv, RegistryValueKind.DWord);
                return (true, "VBS Alternado. É necessário reiniciar para aplicar as alterações.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static bool IsFastStartupTweakEnabled() => (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 1) == 0;
        public static void ToggleFastStartupTweak()
        {
            if (IsFastStartupTweakEnabled()) RevertRegistryValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec");
            else Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0, RegistryValueKind.DWord);
        }

        public static bool IsFastShutdownEnabled() => (string?)Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "AutoEndTasks", "0") == "1";
        public static void ToggleFastShutdown()
        {
            try
            {
                bool enabled = IsFastShutdownEnabled();
                string val = enabled ? "0" : "1";
                string timeout = enabled ? "5000" : "2000"; // 5s standard vs 2s turbo

                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "AutoEndTasks", val, RegistryValueKind.String);
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "HungAppTimeout", timeout, RegistryValueKind.String);
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WaitToKillAppTimeout", timeout, RegistryValueKind.String);

                // Service Timeout (Aggressive)
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control", true);
                key?.SetValue("WaitToKillServiceTimeout", timeout, RegistryValueKind.String);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        #region Turbo Boot (Task Scheduler)
        private const string TurboTaskName = "KitLugia";

        public static bool IsTurboBootEnabled()
        {
            try
            {
                using var ts = new TaskService();
                var task = ts.GetTask(TurboTaskName);
                if (task == null) return false;
                // Turbo Boot = tarefa KitLugia com prioridade High
                return task.Enabled && task.Definition.Settings.Priority == ProcessPriorityClass.High;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void ToggleTurboBoot(bool enable)
        {
            try
            {
                using var ts = new TaskService();
                var task = ts.GetTask(TurboTaskName);

                if (enable)
                {
                    // Se a tarefa não existe, criá-la agora (alta prioridade)
                    if (task == null)
                    {
                        string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                        if (string.IsNullOrEmpty(exePath))
                        {
                            KitLugia.Core.Logger.LogError("ToggleTurboBoot", "Não foi possível obter o caminho do executável");
                            return;
                        }

                        var td = ts.NewTask();
                        td.RegistrationInfo.Description = "KitLugia Auto-Startup (Admin + Turbo Boot)";
                        td.Principal.RunLevel = TaskRunLevel.Highest;
                        td.Settings.DisallowStartIfOnBatteries = false;
                        td.Settings.StopIfGoingOnBatteries = false;
                        td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
                        td.Settings.StartWhenAvailable = true;
                        td.Settings.Priority = ProcessPriorityClass.High;
                        td.Settings.RestartCount = 2;
                        td.Settings.RestartInterval = TimeSpan.FromMinutes(1);

                        var trigger = new LogonTrigger { Delay = TimeSpan.Zero, Enabled = true };
                        td.Triggers.Add(trigger);
                        td.Actions.Add(new ExecAction(exePath, "--tray", Path.GetDirectoryName(exePath) ?? ""));
                        ts.RootFolder.RegisterTaskDefinition(TurboTaskName, td);
                        KitLugia.Core.Logger.Log("🚀 Turbo Boot: tarefa KitLugia criada com Priority High");
                    }
                    else
                    {
                        // Tarefa existe — apenas bumpa a prioridade
                        var td = task.Definition;
                        td.Settings.Priority = ProcessPriorityClass.High;
                        task.RegisterChanges();
                        KitLugia.Core.Logger.Log("🚀 Turbo Boot: prioridade da tarefa KitLugia elevada para High");
                    }
                }
                else
                {
                    // Desativar Turbo = baixar prioridade (mas mantém a tarefa se ela existir)
                    if (task != null)
                    {
                        task.Definition.Settings.Priority = ProcessPriorityClass.Normal;
                        task.RegisterChanges();
                        KitLugia.Core.Logger.Log("Turbo Boot: prioridade reduzida para Normal");
                    }
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("ToggleTurboBoot", ex.Message);
            }
        }
        #endregion

        public static void ApplyVerboseStatus() => Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "VerboseStatus", 1, RegistryValueKind.DWord);
        public static bool IsPageFileDisabled()
        {
            var val = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "PagingFiles", null) as string[];
            return val == null || val.Length == 0 || string.IsNullOrWhiteSpace(val[0]);
        }

        #region Latency & Timer Tweaks
        public static bool IsTimerResolutionOptimized()
        {
            try
            {
                var val = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 0);
                return val != null && Convert.ToInt32(val) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static (bool Success, string Message) ToggleTimerResolution()
        {
            bool alreadyOptimized = IsTimerResolutionOptimized();
            try
            {
                if (alreadyOptimized)
                {
                    // Reverter Registry
                    using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", true))
                    {
                        key?.DeleteValue("GlobalTimerResolutionRequests", false);
                    }
                    
                    // Reverter BCD
                    SystemUtils.RunExternalProcess("bcdedit", "/deletevalue useplatformclock", true);
                    SystemUtils.RunExternalProcess("bcdedit", "/deletevalue useplatformtick", true);
                    SystemUtils.RunExternalProcess("bcdedit", "/deletevalue disabledynamictick", true);

                    return (true, "Timer/Clock revertido para o padrão do Windows.");
                }
                else
                {
                    // Aplicar Registry
                    Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 1, RegistryValueKind.DWord);
                    
                    // Aplicar BCD
                    SystemUtils.RunExternalProcess("bcdedit", "/set useplatformclock no", true);
                    SystemUtils.RunExternalProcess("bcdedit", "/set useplatformtick no", true);
                    SystemUtils.RunExternalProcess("bcdedit", "/set disabledynamictick yes", true);

                    return (true, "Otimizações de Baixa Latência (HPET/Timer) aplicadas.");
                }
            }
            catch (Exception ex) { return (false, $"Erro ao alternar otimizações: {ex.Message}"); }
        }
        #endregion

        #endregion

        #region GPU & Gaming
        /// <summary>
        /// Obtém lista de nomes de GPUs. NÃO retorna ManagementObject para evitar memory leak.
        /// </summary>
        public static List<string> GetAllGpuNames()
        {
            var names = new List<string>();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
                foreach (ManagementObject obj in searcher.Get())
                {
                    using (obj)
                    {
                        string? name = obj["Name"]?.ToString();
                        if (!string.IsNullOrEmpty(name))
                            names.Add(name);
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return names;
        }

        public record GpuInfo(string Name, string? RegPath, string PnpDeviceId);

        public static List<GpuInfo> GetAllGpuInfo()
        {
            var result = new List<GpuInfo>();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_VideoController");
                foreach (ManagementObject obj in searcher.Get())
                {
                    using (obj)
                    {
                        string? name = obj["Name"]?.ToString();
                        string? pnpId = obj["PNPDeviceID"]?.ToString();
                        if (string.IsNullOrEmpty(name)) continue;

                        string? regPath = FindGpuRegistryPathByPnpId(pnpId);
                        result.Add(new GpuInfo(name, regPath, pnpId ?? ""));
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return result;
        }

        private static string? FindGpuRegistryPathByPnpId(string? pnpDeviceId)
        {
            if (string.IsNullOrEmpty(pnpDeviceId)) return null;
            try
            {
                string videoClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
                string regBase = $@"SYSTEM\CurrentControlSet\Control\Class\{videoClassGuid}";
                using var baseKey = Registry.LocalMachine.OpenSubKey(regBase);
                if (baseKey == null) return null;

                foreach (var subKeyName in baseKey.GetSubKeyNames())
                {
                    if (!Regex.IsMatch(subKeyName, @"^\d{4}$")) continue;
                    using var subKey = baseKey.OpenSubKey(subKeyName);
                    if (subKey == null) continue;

                    // Match primário: DeviceInstance contra PNPDeviceID do WMI
                    string? deviceInstance = subKey.GetValue("DeviceInstance")?.ToString();
                    if (!string.IsNullOrEmpty(deviceInstance) &&
                        deviceInstance.Equals(pnpDeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        return $@"HKEY_LOCAL_MACHINE\{regBase}\{subKeyName}";
                    }
                }

                // Fallback: tenta MatchingDeviceId como prefixo do PNPDeviceID
                foreach (var subKeyName in baseKey.GetSubKeyNames())
                {
                    if (!Regex.IsMatch(subKeyName, @"^\d{4}$")) continue;
                    using var subKey = baseKey.OpenSubKey(subKeyName);
                    if (subKey == null) continue;

                    string? matchingId = subKey.GetValue("MatchingDeviceId")?.ToString();
                    if (!string.IsNullOrEmpty(matchingId) &&
                        pnpDeviceId.StartsWith(matchingId, StringComparison.OrdinalIgnoreCase))
                    {
                        return $@"HKEY_LOCAL_MACHINE\{regBase}\{subKeyName}";
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return null;
        }

        /// <summary>
        /// Obtém lista de GPUs com dispose automático. Use apenas quando necessário.
        /// </summary>
        [Obsolete("Use GetAllGpuNames() para evitar memory leaks")]
        public static List<ManagementObject> GetAllGpus()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
                return searcher.Get().Cast<ManagementObject>().ToList();
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return new List<ManagementObject>(); }
        }

        public static ManagementObject? GetPrimaryGpu()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
                foreach (ManagementObject obj in searcher.Get())
                {
                    string? name = obj["Name"]?.ToString();
                    if (!string.IsNullOrEmpty(name) && !name.Contains("Microsoft Basic Display Adapter"))
                        return obj;
                    obj.Dispose();
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return null;
        }

        public static string? FindGpuRegistryPath(ManagementObject gpu)
        {
            try
            {
                string gpuDescription = gpu["Description"]?.ToString() ?? gpu["Name"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(gpuDescription)) return null;
                return FindGpuRegistryPathByDescription(gpuDescription);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return null; }
        }

        /// <summary>
        /// Encontra o caminho do registro da GPU pelo nome/descrição. Versão segura sem ManagementObject.
        /// </summary>
        public static string? FindGpuRegistryPathByDescription(string gpuDescription)
        {
            try
            {
                if (string.IsNullOrEmpty(gpuDescription)) return null;

                string videoClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
                string regBase = $@"SYSTEM\CurrentControlSet\Control\Class\{videoClassGuid}";

                using var baseKey = Registry.LocalMachine.OpenSubKey(regBase);
                if (baseKey == null) return null;

                // 1. Match exato (case-insensitive)
                foreach (var subKeyName in baseKey.GetSubKeyNames())
                {
                    if (!Regex.IsMatch(subKeyName, @"^\d{4}$")) continue;
                    using var subKey = baseKey.OpenSubKey(subKeyName);
                    if (subKey == null) continue;
                    string? driverDesc = subKey.GetValue("DriverDesc")?.ToString();
                    if (!string.IsNullOrEmpty(driverDesc) &&
                        driverDesc.Equals(gpuDescription, StringComparison.OrdinalIgnoreCase))
                    {
                        return $@"HKEY_LOCAL_MACHINE\{regBase}\{subKeyName}";
                    }
                }

                // 2. Fallback: contém a descrição (case-insensitive)
                foreach (var subKeyName in baseKey.GetSubKeyNames())
                {
                    if (!Regex.IsMatch(subKeyName, @"^\d{4}$")) continue;
                    using var subKey = baseKey.OpenSubKey(subKeyName);
                    if (subKey == null) continue;
                    string? driverDesc = subKey.GetValue("DriverDesc")?.ToString();
                    if (!string.IsNullOrEmpty(driverDesc) &&
                        driverDesc.Contains(gpuDescription, StringComparison.OrdinalIgnoreCase))
                    {
                        return $@"HKEY_LOCAL_MACHINE\{regBase}\{subKeyName}";
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return null; }
            return null;
        }

        public static void ApplyGpuVramTweak(string regPath, int sizeInMb)
        {
            try
            {
                // Extrai o caminho sem o HKEY_LOCAL_MACHINE para o CreateSubKey
                string subPath = regPath.Replace(@"HKEY_LOCAL_MACHINE\", "");

                if (sizeInMb == -1 || sizeInMb == 0)
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(subPath, true))
                    {
                        key?.DeleteValue("DedicatedSegmentSize", false);
                    }
                    Logger.Log($"VRAM Tweak removido em {regPath}");
                }
                else
                {
                    // MUDANÇA AGRESSIVA: CreateSubKey garante que a chave exista se não estiver lá
                    using (var key = Registry.LocalMachine.CreateSubKey(subPath, true))
                    {
                        if (key != null)
                        {
                            key.SetValue("DedicatedSegmentSize", sizeInMb, RegistryValueKind.DWord);
                            Logger.Log($"VRAM Tweak aplicado (AGRESSIVO): {sizeInMb} MB em {regPath}");
                        }
                        else
                        {
                            // Fallback se o CreateSubKey falhar (ex: permissões, embora raro em admin)
                            Registry.SetValue(regPath, "DedicatedSegmentSize", sizeInMb, RegistryValueKind.DWord);
                            Logger.Log($"VRAM Tweak aplicado (SET): {sizeInMb} MB em {regPath}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"ERRO ao aplicar tweak de VRAM: {ex.Message}");
            }
        }

        public static int GetRecommendedVramMb(double totalRamGb)
        {
            if (totalRamGb <= 0) return 512;
            if (totalRamGb < 6) return 256;
            if (totalRamGb < 12) return 512;
            if (totalRamGb < 24) return 1024;
            return 2048;
        }

        public static void ApplyAutomaticVramTweak()
        {
            using var primaryGpu = GetPrimaryGpu();
            if (primaryGpu == null) return;

            string? regPath = FindGpuRegistryPath(primaryGpu);
            if (string.IsNullOrEmpty(regPath)) return;

            double totalRamGB = SystemUtils.GetTotalSystemRamGB();
            int sizeToSet = GetRecommendedVramMb(totalRamGB);

            ApplyGpuVramTweak(regPath, sizeToSet);

            try
            {
                string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows";
                Registry.SetValue(key, "GDIProcessHandleQuota", 10000, RegistryValueKind.DWord);
                Registry.SetValue(key, "USERProcessHandleQuota", 10000, RegistryValueKind.DWord);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static void RevertVramTweaks()
        {
            try
            {
                // 1. Limpeza AGRESSIVA na Classe de Vídeo (limpa todas as subchaves 0000, 0001, etc.)
                string videoClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
                string regBase = $@"SYSTEM\CurrentControlSet\Control\Class\{videoClassGuid}";
                using (var baseKey = Registry.LocalMachine.OpenSubKey(regBase, true))
                {
                    if (baseKey != null)
                    {
                        foreach (var subKeyName in baseKey.GetSubKeyNames())
                        {
                            if (Regex.IsMatch(subKeyName, @"^\d{4}$"))
                            {
                                using (var subKey = baseKey.OpenSubKey(subKeyName, true))
                                {
                                    subKey?.DeleteValue("DedicatedSegmentSize", false);
                                    // Limpa também outros possíveis tweaks antigos conhecidos
                                    subKey?.DeleteValue("IntegratedGpuWddm", false); 
                                }
                            }
                        }
                    }
                }

                // 2. Limpeza do Intel GMM (caso tenha sido aplicado por versões antigas ou "quebradas")
                try
                {
                    using (var intelKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Intel", true))
                    {
                        intelKey?.DeleteSubKeyTree("GMM", false);
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                // 3. Reverte Quotas (GDI/USER)
                using (var winKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows", true))
                {
                    winKey?.DeleteValue("GDIProcessHandleQuota", false);
                    winKey?.DeleteValue("USERProcessHandleQuota", false);
                }

                Logger.Log("Reversão AGRESSIVA de VRAM e Tweaks de GPU concluída.");
            }
            catch (Exception ex)
            {
                Logger.Log($"ERRO na reversão agressiva: {ex.Message}");
            }
        }

        // ============================================================
        // STARTUP TWEAKS
        // ============================================================

        // --- Startup Delay ---
        public static void RevertStartupDelay()
        {
            try
            {
                RevertRegistryValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec");
                Logger.Log("Startup delay revertido para padrão.");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- Fast Startup (Hybrid Boot / Hiberboot) ---
        private const string PowerPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power";

        public static bool IsFastStartupDisabled()
        {
            try
            {
                var val = Registry.GetValue(PowerPath, "HiberbootEnabled", -1);
                return val != null && Convert.ToInt32(val) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void DisableFastStartup()
        {
            try
            {
                Registry.SetValue(PowerPath, "HiberbootEnabled", 0, RegistryValueKind.DWord);
                Logger.Log("Fast Startup (Hiberboot) desativado.");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void EnableFastStartup()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(PowerPath.Replace(@"HKEY_LOCAL_MACHINE\", ""), true);
                key?.DeleteValue("HiberbootEnabled", false);
                Logger.Log("Fast Startup (Hiberboot) restaurado para padrão.");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- Boot Menu Timeout ---
        public static bool IsBootMenuTimeoutZero()
        {
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "bcdedit",
                        Arguments = "/enum {current}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                p.Start();
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                var match = Regex.Match(output, @"timeout\s+(\d+)", RegexOptions.IgnoreCase);
                return match.Success && match.Groups[1].Value == "0";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void SetBootMenuTimeoutZero()
        {
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "bcdedit",
                        Arguments = "/timeout 0",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        Verb = "runas"
                    }
                };
                p.Start();
                p.WaitForExit(5000);
                Logger.Log("Boot menu timeout definido para 0s.");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RevertBootMenuTimeout()
        {
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "bcdedit",
                        Arguments = "/timeout 30",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        Verb = "runas"
                    }
                };
                p.Start();
                p.WaitForExit(5000);
                Logger.Log("Boot menu timeout restaurado para 30s.");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- NumLock on Startup ---
        public static bool IsNumLockOnStartupEnabled()
        {
            try
            {
                string path = @"HKEY_CURRENT_USER\Control Panel\Keyboard";
                var val = Registry.GetValue(path, "InitialKeyboardIndicators", "0");
                string? s = val?.ToString();
                return s == "2" || s == "2147483650" || s == "80000002";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void EnableNumLockOnStartup()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Keyboard", "InitialKeyboardIndicators", "2", RegistryValueKind.String);
                Logger.Log("NumLock on Startup ativado.");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void DisableNumLockOnStartup()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Keyboard", "InitialKeyboardIndicators", "0", RegistryValueKind.String);
                Logger.Log("NumLock on Startup desativado.");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // ============================================================
        // GPU SCHEDULING TWEAKS (FrameQueue, Preemption, Idle, Latency)
        // ============================================================

        private const string GpuSchedulerPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Scheduler";
        private const string GpuPowerPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Power";

        // --- FrameQueueMode: 0 = Low Latency ---
        public static bool IsFrameQueueModeSet() => (int?)Registry.GetValue(GpuSchedulerPath, "FrameQueueMode", 0) == 0;
        public static void ApplyLowLatencyFrameQueue()
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(GpuSchedulerPath.Replace(@"HKEY_LOCAL_MACHINE\", ""));
                key?.SetValue("FrameQueueMode", 0, RegistryValueKind.DWord);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RevertFrameQueue()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(GpuSchedulerPath.Replace(@"HKEY_LOCAL_MACHINE\", ""), true);
                key?.DeleteValue("FrameQueueMode", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- EnablePreemption: 1 = Allow GPU preemption ---
        public static bool IsPreemptionEnabled() => (int?)Registry.GetValue(GpuSchedulerPath, "EnablePreemption", 0) == 1;
        public static void EnableGpuPreemption()
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(GpuSchedulerPath.Replace(@"HKEY_LOCAL_MACHINE\", ""));
                key?.SetValue("EnablePreemption", 1, RegistryValueKind.DWord);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RevertGpuPreemption()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(GpuSchedulerPath.Replace(@"HKEY_LOCAL_MACHINE\", ""), true);
                key?.DeleteValue("EnablePreemption", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- DisableGpuIdle: 1 = GPU never idles ---
        public static bool IsGpuIdleDisabled() => (int?)Registry.GetValue(GpuSchedulerPath, "DisableGpuIdle", 0) == 1;
        public static void DisableGpuIdleSchedule()
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(GpuSchedulerPath.Replace(@"HKEY_LOCAL_MACHINE\", ""));
                key?.SetValue("DisableGpuIdle", 1, RegistryValueKind.DWord);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RevertGpuIdleSchedule()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(GpuSchedulerPath.Replace(@"HKEY_LOCAL_MACHINE\", ""), true);
                key?.DeleteValue("DisableGpuIdle", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- GPU Power Latency: 1 = Priority to latency ---
        public static bool IsGpuPowerLatencySet() => (int?)Registry.GetValue(GpuPowerPath, "Latency", 0) == 1;
        public static void SetGpuPowerLatency()
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(GpuPowerPath.Replace(@"HKEY_LOCAL_MACHINE\", ""));
                key?.SetValue("Latency", 1, RegistryValueKind.DWord);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RevertGpuPowerLatency()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(GpuPowerPath.Replace(@"HKEY_LOCAL_MACHINE\", ""), true);
                key?.DeleteValue("Latency", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // ============================================================
        // HAGS (Hardware Accelerated GPU Scheduling)
        // ============================================================
        private const string GpuDriversPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

        public static bool IsHagsEnabled() => (int?)Registry.GetValue(GpuDriversPath, "HwSchMode", 0) == 2;
        public static void EnableHags()
        {
            try
            {
                Registry.SetValue(GpuDriversPath, "HwSchMode", 2, RegistryValueKind.DWord);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RevertHags()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(GpuDriversPath.Replace(@"HKEY_LOCAL_MACHINE\", ""), true);
                key?.DeleteValue("HwSchMode", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // ============================================================
        // TDR Delay (Timeout Detection & Recovery)
        // ============================================================
        public static bool IsTdrDelayIncreased() => (int?)Registry.GetValue(GpuDriversPath, "TdrDelay", 0) >= 10;
        public static void IncreaseTdrDelay()
        {
            try
            {
                Registry.SetValue(GpuDriversPath, "TdrDelay", 10, RegistryValueKind.DWord);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RevertTdrDelay()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(GpuDriversPath.Replace(@"HKEY_LOCAL_MACHINE\", ""), true);
                key?.DeleteValue("TdrDelay", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // ============================================================
        // IoPageLockLimit (memória travada para I/O de GPU/disco)
        // ============================================================
        private const string MemManagementPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

        public static bool IsIoPageLockLimitSet() => (int?)Registry.GetValue(MemManagementPath, "IoPageLockLimit", 0) >= 8192;
        public static void SetIoPageLockLimit()
        {
            try
            {
                Registry.SetValue(MemManagementPath, "IoPageLockLimit", 8192, RegistryValueKind.DWord);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RevertIoPageLockLimit()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(MemManagementPath.Replace(@"HKEY_LOCAL_MACHINE\", ""), true);
                key?.DeleteValue("IoPageLockLimit", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // ============================================================
        // Input Queue Size (Mouse & Keyboard)
        // ============================================================
        private const string MouclassPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\mouclass\Parameters";
        private const string KbdclassPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\kbdclass\Parameters";

        public static bool IsInputQueueSizeIncreased()
        {
            try
            {
                int mouse = (int?)Registry.GetValue(MouclassPath, "MouseDataQueueSize", 0) ?? 0;
                int kbd = (int?)Registry.GetValue(KbdclassPath, "KeyboardDataQueueSize", 0) ?? 0;
                return mouse >= 200 || kbd >= 200;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void IncreaseInputQueueSize()
        {
            try
            {
                Registry.SetValue(MouclassPath, "MouseDataQueueSize", 200, RegistryValueKind.DWord);
                Registry.SetValue(KbdclassPath, "KeyboardDataQueueSize", 200, RegistryValueKind.DWord);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RevertInputQueueSize()
        {
            try
            {
                using var mk = Registry.LocalMachine.OpenSubKey(MouclassPath.Replace(@"HKEY_LOCAL_MACHINE\", ""), true);
                mk?.DeleteValue("MouseDataQueueSize", false);
                using var kk = Registry.LocalMachine.OpenSubKey(KbdclassPath.Replace(@"HKEY_LOCAL_MACHINE\", ""), true);
                kk?.DeleteValue("KeyboardDataQueueSize", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static bool IsGamingOptimized() => (int?)Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority", 0) == 8;
        public static void ApplyGamingOptimizations()
        {
            try
            {
                string p = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
                Registry.SetValue(p, "GPU Priority", 8, RegistryValueKind.DWord);
                Registry.SetValue(p, "Priority", 6, RegistryValueKind.DWord);
                Registry.SetValue(p, "Scheduling Category", "High", RegistryValueKind.String);
                Registry.SetValue(p, "SFIO Priority", "High", RegistryValueKind.String);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static void RevertGamingOptimizations()
        {
            try
            {
                string p = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
                Registry.SetValue(p, "GPU Priority", 8, RegistryValueKind.DWord);
                Registry.SetValue(p, "Priority", 2, RegistryValueKind.DWord);
                Registry.SetValue(p, "Scheduling Category", "Medium", RegistryValueKind.String);
                Registry.SetValue(p, "SFIO Priority", "Normal", RegistryValueKind.String);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static bool IsMpoDisabled() => (int?)Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 0) == 5;
        public static (bool Success, string Message) ToggleMpo()
        {
            try
            {
                if (IsMpoDisabled())
                {
                    using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\Dwm", true);
                    k?.DeleteValue("OverlayTestMode", false);
                    return (true, "MPO Reativado. Reinicie para aplicar.");
                }
                else
                {
                    Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5, RegistryValueKind.DWord);
                    return (true, "MPO Desativado. Reinicie para aplicar.");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static bool IsGpuMsiEnabled(string pnpDeviceId)
        {
            if (string.IsNullOrEmpty(pnpDeviceId)) return false;
            try
            {
                string keyPath = $@"SYSTEM\CurrentControlSet\Enum\{pnpDeviceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                using var key = Registry.LocalMachine.OpenSubKey(keyPath);
                return key != null && (int?)key.GetValue("MSISupported") == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static (bool Success, string Message) ToggleGpuMsiMode(string pnpDeviceId)
        {
            if (string.IsNullOrEmpty(pnpDeviceId)) return (false, "PNPDeviceID da GPU não encontrado.");
            try
            {
                string keyPath = $@"SYSTEM\CurrentControlSet\Enum\{pnpDeviceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                using var key = Registry.LocalMachine.CreateSubKey(keyPath, true);
                if (key == null) return (false, "Não foi possível acessar a chave do registro da GPU.");

                int current = (int?)key.GetValue("MSISupported", 0) ?? 0;
                int newValue = (current == 1) ? 0 : 1;
                key.SetValue("MSISupported", newValue, RegistryValueKind.DWord);
                return (true, $"Modo MSI {(newValue == 1 ? "ATIVADO" : "DESATIVADO")}. É necessário reiniciar para aplicar.");
            }
            catch (Exception ex) { return (false, $"Erro: {ex.Message}"); }
        }

        public static bool IsGameDvrEnabled() => (int?)Registry.GetValue(@"HKEY_CURRENT_USER\System\GameConfigStore", "GameDVR_Enabled", 1) == 1;
        public static void ToggleGameDvr(bool enable)
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\System\GameConfigStore", "GameDVR_Enabled", enable ? 1 : 0);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        #endregion

        #region Hardware-Aware Tweaks (L3 Cache, NVIDIA PowerMizer, NVMe, GPU Interrupt Priority)

        // ============================================================
        // THIRD LEVEL DATA CACHE (L3) — Auto-detect from CPU
        // ============================================================
        private const string MemMgmtPath2 = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

        public static bool IsThirdLevelCacheSet()
        {
            try
            {
                var val = Registry.GetValue(MemMgmtPath2, "ThirdLevelDataCache", 0);
                return val != null && Convert.ToInt32(val) > 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void ApplyThirdLevelCacheTweak()
        {
            try
            {
                // Read actual L3 cache from CPU via WMI
                int l3CacheKb = 0;
                using var searcher = new ManagementObjectSearcher("SELECT L3CacheSize FROM Win32_Processor");
                foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
                {
                    using (obj)
                    {
                        if (obj["L3CacheSize"] != null)
                        {
                            l3CacheKb = Convert.ToInt32(obj["L3CacheSize"]);
                            break;
                        }
                    }
                }

                if (l3CacheKb > 0)
                {
                    Registry.SetValue(MemMgmtPath2, "ThirdLevelDataCache", l3CacheKb, RegistryValueKind.DWord);
                    Logger.Log($"L3 Cache Tweak aplicado: {l3CacheKb} KB (auto-detectado do CPU)");
                }
                else
                {
                    Logger.Log("L3 Cache: não detectado via WMI, pulando.");
                }
            }
            catch (Exception ex) { Logger.LogError("ApplyThirdLevelCacheTweak", ex.Message); }
        }

        public static void RevertThirdLevelCacheTweak()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(MemMgmtPath2.Replace(@"HKEY_LOCAL_MACHINE\", ""), true);
                key?.DeleteValue("ThirdLevelDataCache", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static (bool Success, string Message) ToggleThirdLevelCache()
        {
            if (IsThirdLevelCacheSet())
            {
                RevertThirdLevelCacheTweak();
                return (true, "L3 Cache restaurado para padrão.");
            }
            ApplyThirdLevelCacheTweak();
            var val = Registry.GetValue(MemMgmtPath2, "ThirdLevelDataCache", 0);
            int kb = val != null ? Convert.ToInt32(val) : 0;
            return (kb > 0, kb > 0 ? $"L3 Cache configurado: {kb} KB" : "L3 Cache não detectado no CPU.");
        }

        // ============================================================
        // NVIDIA PowerMizer — Force Maximum Performance
        // ============================================================
        private static readonly string[] NvidiaPowerMizerPaths = new[]
        {
            @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000",
            @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0001",
            @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0002",
            @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0003"
        };

        public static bool IsNvidiaPowerMizerMaxPerformance()
        {
            try
            {
                foreach (var path in NvidiaPowerMizerPaths)
                {
                    var val = Registry.GetValue($@"HKEY_LOCAL_MACHINE\{path.Substring(5)}", "PerfLevelSrc", -1);
                    if (val != null && Convert.ToInt32(val) == 0x2222) return true;
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return false;
        }

        public static void ApplyNvidiaPowerMizerMaxPerformance()
        {
            try
            {
                int applied = 0;
                foreach (var path in NvidiaPowerMizerPaths)
                {
                    string subPath = path.Replace("HKLM\\", "");
                    using var key = Registry.LocalMachine.OpenSubKey(subPath, true);
                    if (key == null) continue;

                    // Only apply to NVIDIA adapters (check ProviderName)
                    var provider = key.GetValue("ProviderName")?.ToString() ?? "";
                    if (!provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) continue;

                    // PerfLevelSrc=0x2222: Force performance level from registry
                    key.SetValue("PerfLevelSrc", 0x2222, RegistryValueKind.DWord);
                    // PowerMizerEnable=1: Enable PowerMizer control
                    key.SetValue("PowerMizerEnable", 1, RegistryValueKind.DWord);
                    // PowerMizerLevel=1: Maximum performance
                    key.SetValue("PowerMizerLevel", 1, RegistryValueKind.DWord);
                    // PowerMizerDefault=1: Maximum performance on battery
                    key.SetValue("PowerMizerDefault", 1, RegistryValueKind.DWord);
                    // PowerMizerDefaultAC=1: Maximum performance on AC
                    key.SetValue("PowerMizerDefaultAC", 1, RegistryValueKind.DWord);
                    applied++;
                }
                Logger.Log($"NVIDIA PowerMizer Max Performance aplicado em {applied} GPU(s). Reinício necessário.");
            }
            catch (Exception ex) { Logger.LogError("ApplyNvidiaPowerMizerMaxPerformance", ex.Message); }
        }

        public static void RevertNvidiaPowerMizer()
        {
            try
            {
                foreach (var path in NvidiaPowerMizerPaths)
                {
                    string subPath = path.Replace("HKLM\\", "");
                    using var key = Registry.LocalMachine.OpenSubKey(subPath, true);
                    if (key == null) continue;
                    key?.DeleteValue("PerfLevelSrc", false);
                    key?.DeleteValue("PowerMizerEnable", false);
                    key?.DeleteValue("PowerMizerLevel", false);
                    key?.DeleteValue("PowerMizerDefault", false);
                    key?.DeleteValue("PowerMizerDefaultAC", false);
                }
                Logger.Log("NVIDIA PowerMizer restaurado para padrão.");
            }
            catch (Exception ex) { Logger.LogError("RevertNvidiaPowerMizer", ex.Message); }
        }

        public static (bool Success, string Message) ToggleNvidiaPowerMizer()
        {
            if (IsNvidiaPowerMizerMaxPerformance())
            {
                RevertNvidiaPowerMizer();
                return (true, "NVIDIA PowerMizer restaurado para padrão.");
            }
            ApplyNvidiaPowerMizerMaxPerformance();
            return (IsNvidiaPowerMizerMaxPerformance(), "NVIDIA PowerMizer configurado para máximo desempenho. Reinício necessário.");
        }

        // ============================================================
        // NVMe Latency & Handoff Tweaks
        // ============================================================
        private static readonly string[] NvMeServicePaths = new[]
        {
            @"HKLM\SYSTEM\CurrentControlSet\Services\stornvme\Parameters",
            @"HKLM\SYSTEM\CurrentControlSet\Services\nvme\Parameters"
        };

        public static bool IsNvMeLatencyOptimized()
        {
            try
            {
                // Check D3Handoff for NVMe (enables fast resume from D3 power state)
                foreach (var path in NvMeServicePaths)
                {
                    var val = Registry.GetValue($"{path}", "D3Handoff", -1);
                    if (val != null && Convert.ToInt32(val) == 1) return true;
                }
                return false;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void ApplyNvMeLatencyTweaks()
        {
            try
            {
                // D3Handoff=1: Enable fast D3 power state transitions for NVMe drives
                foreach (var path in NvMeServicePaths)
                {
                    try
                    {
                        string subPath = path.Replace("HKLM\\", "");
                        using var key = Registry.LocalMachine.CreateSubKey(subPath, true);
                        key?.SetValue("D3Handoff", 1, RegistryValueKind.DWord);
                    }
                    catch { }
                }

                // Also set device-specific NVMe power management via power settings
                // NVMe devices use the StorNVMe power management
                try
                {
                    string nvmePowerPath = @"HKLM\SYSTEM\CurrentControlSet\Enum\PCI";
                    // Set AllowIdle1InD3 for NVMe devices (prevents deep sleep latency)
                    var pciKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum", true);
                    if (pciKey != null)
                    {
                        foreach (var enumSub in pciKey.GetSubKeyNames())
                        {
                            if (!enumSub.StartsWith("PCI", StringComparison.OrdinalIgnoreCase)) continue;
                            try
                            {
                                using var devKey = pciKey.OpenSubKey($@"{enumSub}\Device Parameters", true);
                                if (devKey?.GetValue("AllowIdle1InD3") != null)
                                {
                                    devKey.SetValue("AllowIdle1InD3", 0, RegistryValueKind.DWord);
                                    Logger.Log($"NVMe: AllowIdle1InD3=0 em {enumSub}");
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                Logger.Log("NVMe Latency Tweaks aplicados (D3Handoff=1, AllowIdle1InD3=0)");
            }
            catch (Exception ex) { Logger.LogError("ApplyNvMeLatencyTweaks", ex.Message); }
        }

        public static void RevertNvMeLatencyTweaks()
        {
            try
            {
                foreach (var path in NvMeServicePaths)
                {
                    string subPath = path.Replace("HKLM\\", "");
                    using var key = Registry.LocalMachine.OpenSubKey(subPath, true);
                    key?.DeleteValue("D3Handoff", false);
                }
                Logger.Log("NVMe Latency Tweaks restaurados.");
            }
            catch (Exception ex) { Logger.LogError("RevertNvMeLatencyTweaks", ex.Message); }
        }

        public static (bool Success, string Message) ToggleNvMeLatency()
        {
            if (IsNvMeLatencyOptimized())
            {
                RevertNvMeLatencyTweaks();
                return (true, "NVMe Latency restaurado para padrão.");
            }
            ApplyNvMeLatencyTweaks();
            return (true, "NVMe Latency otimizado (D3Handoff=1). Reinício pode ser necessário.");
        }

        // ============================================================
        // GPU DPC & Interrupt Priority
        // ============================================================
        public static bool IsGpuDpcLatencyLow()
        {
            try
            {
                var val = Registry.GetValue(@"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl", "IRQ8Priority", 0);
                return val != null && Convert.ToInt32(val) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void ApplyGpuDpcLatencyTweaks()
        {
            try
            {
                // IRQ8Priority=1: Real-time priority for system clock interrupt
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\PriorityControl", "IRQ8Priority", 1, RegistryValueKind.DWord);
                Logger.Log("GPU DPC Latency: IRQ8Priority=1 aplicado.");
            }
            catch (Exception ex) { Logger.LogError("ApplyGpuDpcLatencyTweaks", ex.Message); }
        }

        public static void RevertGpuDpcLatencyTweaks()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\PriorityControl", true);
                key?.DeleteValue("IRQ8Priority", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static (bool Success, string Message) ToggleGpuDpcLatency()
        {
            if (IsGpuDpcLatencyLow())
            {
                RevertGpuDpcLatencyTweaks();
                return (true, "GPU DPC Latency restaurado.");
            }
            ApplyGpuDpcLatencyTweaks();
            return (true, "GPU DPC Latency otimizado (IRQ8Priority=1).");
        }

        // ============================================================
        // Memory Prioritization & Large System Cache
        // ============================================================
        public static bool IsMemoryPrioritizationDse()
        {
            try
            {
                var val = Registry.GetValue(MemMgmtPath2, "LargeSystemCache", 0);
                return val != null && Convert.ToInt32(val) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void ApplyMemoryPrioritizationDse()
        {
            try
            {
                // LargeSystemCache=1: Prioritize system file cache (improves server/heavy I/O)
                Registry.SetValue(MemMgmtPath2, "LargeSystemCache", 1, RegistryValueKind.DWord);
                // DisablePagingExecutive=1: Keep kernel in RAM (requires reboot, uses more RAM but faster)
                Registry.SetValue(MemMgmtPath2, "DisablePagingExecutive", 1, RegistryValueKind.DWord);
                Logger.Log("Memory Prioritization aplicado: LargeSystemCache=1, DisablePagingExecutive=1");
            }
            catch (Exception ex) { Logger.LogError("ApplyMemoryPrioritizationDse", ex.Message); }
        }

        public static void RevertMemoryPrioritizationDse()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(MemMgmtPath2.Replace(@"HKEY_LOCAL_MACHINE\", ""), true);
                key?.DeleteValue("LargeSystemCache", false);
                key?.DeleteValue("DisablePagingExecutive", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static (bool Success, string Message) ToggleMemoryPrioritizationDse()
        {
            if (IsMemoryPrioritizationDse())
            {
                RevertMemoryPrioritizationDse();
                return (true, "Memory Prioritization restaurado.");
            }
            ApplyMemoryPrioritizationDse();
            return (true, "Memory Prioritization aplicado. Reinício necessário.");
        }

        // ============================================================
        // Boot Configuration (bcdedit) — Fast Boot Optimization
        // ============================================================
        public static bool IsBootLogEnabled()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "bcdedit",
                    Arguments = "/enum {current}",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return false;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                return output.Contains("bootlog", StringComparison.OrdinalIgnoreCase) && output.Contains("Yes", StringComparison.OrdinalIgnoreCase);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static (bool Success, string Message) ToggleBootLog(bool enable)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "bcdedit",
                    Arguments = enable ? "/set {current} bootlog Yes" : "/set {current} bootlog No",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit();
                return (true, enable ? "Boot log ativado." : "Boot log desativado.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static bool IsNoGuiBootEnabled()
            {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "bcdedit",
                    Arguments = "/enum {current}",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return false;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                return output.Contains("noguirolstatus Yes", StringComparison.OrdinalIgnoreCase);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static (bool Success, string Message) ToggleNoGuiBoot(bool enable)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "bcdedit",
                    Arguments = enable ? "/set {current} nointegritychecks Yes" : "/set {current} nointegritychecks No",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit();
                return (true, enable ? "No-integrity-checks boot ativado (cuidado!)." : "No-integrity-checks boot desativado.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        // ============================================================
        // Detect NVMe Drives for Information
        // ============================================================
        public static List<string> DetectNvMeDrives()
        {
            var drives = new List<string>();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive WHERE InterfaceType = 'NVMe'");
                foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
                {
                    using (obj)
                    {
                        string model = obj["Model"]?.ToString() ?? "Unknown NVMe";
                        drives.Add(model);
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return drives;
        }

        public static bool HasNvMeDrive()
        {
            return DetectNvMeDrives().Count > 0;
        }

        // ============================================================
        // Detect GPU Vendor for Conditional Tweaks
        // ============================================================
        public enum GpuVendor { Unknown, NVIDIA, AMD, Intel }

        public static GpuVendor DetectPrimaryGpuVendor()
        {
            var names = GetAllGpuNames();
            var primary = names.FirstOrDefault(n => !n.Contains("Microsoft Basic") && !n.Contains("Parsec"));
            if (string.IsNullOrEmpty(primary)) return GpuVendor.Unknown;
            if (primary.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return GpuVendor.NVIDIA;
            if (primary.Contains("AMD", StringComparison.OrdinalIgnoreCase) || primary.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) return GpuVendor.AMD;
            if (primary.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Intel;
            return GpuVendor.Unknown;
        }

        // ============================================================
        // AMD-Specific GPU Tweaks
        // ============================================================
        public static bool IsAmdAntiLagEnabled()
        {
            try
            {
                var val = Registry.GetValue(@"HKLM\SOFTWARE\AMD\CN\GameFilters", "AntiLag", -1);
                return val != null && Convert.ToInt32(val) == 1;
            }
            catch { return false; }
        }

        public static (bool Success, string Message) ToggleAmdAntiLag()
        {
            try
            {
                bool current = IsAmdAntiLagEnabled();
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\AMD\CN\GameFilters", "AntiLag", current ? 0 : 1, RegistryValueKind.DWord);
                return (true, current ? "AMD Anti-Lag desativado." : "AMD Anti-Lag ativado.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        // ============================================================
        // Intel-Specific GPU Tweaks (Xe/Integrated)
        // ============================================================
        public static bool IsIntelDynamicTuningEnabled()
        {
            try
            {
                var val = Registry.GetValue(@"HKLM\SOFTWARE\Intel\Display\igfxcui\profiles\video\Global", "EnableDynamicTuning", -1);
                return val != null && Convert.ToInt32(val) == 1;
            }
            catch { return false; }
        }

        public static (bool Success, string Message) ToggleIntelDynamicTuning()
        {
            try
            {
                bool current = IsIntelDynamicTuningEnabled();
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Intel\Display\igfxcui\profiles\video\Global", "EnableDynamicTuning", current ? 0 : 1, RegistryValueKind.DWord);
                return (true, current ? "Intel Dynamic Tuning desativado." : "Intel Dynamic Tuning ativado.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        #endregion

        #region Network & Driver
        /// <summary>
        /// Obtém lista de adaptadores de rede ativos. IMPORTANTE: Caller deve descartar os ManagementObject.
        /// </summary>
        [Obsolete("Use apenas quando necessario - lembre-se de dar dispose nos objetos retornados")]
        public static List<ManagementObject> GetActiveNetworkAdapters()
        {
            try
            {
                var query = "SELECT * FROM Win32_NetworkAdapter WHERE NetConnectionStatus = 2";
                using var searcher = new ManagementObjectSearcher(query);
                return searcher.Get().Cast<ManagementObject>().ToList();
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return new List<ManagementObject>(); }
        }

        public static void SetDnsServers(string provider, string? primaryDns, string? secondaryDns)
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = 'TRUE'");
            foreach (ManagementObject adapter in searcher.Get().Cast<ManagementObject>())
            {
                try
                {
                    using (adapter)
                    {
                        var param = adapter.GetMethodParameters("SetDNSServerSearchOrder");
                        param["DNSServerSearchOrder"] = (primaryDns == null) ? null : new string[] { primaryDns, secondaryDns! };
                        adapter.InvokeMethod("SetDNSServerSearchOrder", param, null);
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
        }

        public static string? FindNetworkAdapterRegistryPath(string adapterGuid)
        {
            if (string.IsNullOrEmpty(adapterGuid)) return null;
            try
            {
                string netClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}";
                string basePath = $@"SYSTEM\CurrentControlSet\Control\Class\{netClassGuid}";
                using var classKey = Registry.LocalMachine.OpenSubKey(basePath);
                if (classKey == null) return null;
                foreach (var subKeyName in classKey.GetSubKeyNames())
                {
                    using var subKey = classKey.OpenSubKey(subKeyName);
                    if (subKey?.GetValue("NetCfgInstanceId")?.ToString()?.Equals(adapterGuid, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return $"HKEY_LOCAL_MACHINE\\{basePath}\\{subKeyName}";
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return null;
        }

        public static bool AreNetworkDriverOptimizationsApplied(string regPath)
        {
            if (string.IsNullOrEmpty(regPath)) return false;
            try
            {

                string cleanPath = regPath.Replace("HKEY_LOCAL_MACHINE\\", "");
                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = localMachine.OpenSubKey(cleanPath, false);
                if (key == null)
                    return false;

                var value = key.GetValue("*InterruptModeration")?.ToString();
                return value == "0";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void ToggleNetworkDriverOptimizations(string regPath)
        {
            if (string.IsNullOrEmpty(regPath)) return;
            bool isApplied = AreNetworkDriverOptimizationsApplied(regPath);

            // Típico: 2 tweaks de rede fixos
            var tweaks = new Dictionary<string, string>(2) { { "*InterruptModeration", "0" }, { "EnergyEfficientEthernet", "0" } };
            string cleanPath = regPath.Replace("HKEY_LOCAL_MACHINE\\", "");
            try
            {
                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = localMachine.OpenSubKey(cleanPath, true);
                if (key != null)
                {
                    foreach (var tweak in tweaks)
                    {
                        key.SetValue(tweak.Key, tweak.Value, RegistryValueKind.String);
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static bool IsTcpIpLatencyTweakApplied()
        {
            string? regPath = GetActiveInterfaceRegPath();
            if (string.IsNullOrEmpty(regPath)) return false;
            try
            {

                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = localMachine.OpenSubKey(regPath, false);
                if (key == null)
                    return false;

                var value = key.GetValue("TcpAckFrequency");
                return value != null && value is int intValue && intValue == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static (bool Success, string Message) ToggleTcpIpLatencyTweak()
        {
            string? regPath = GetActiveInterfaceRegPath();
            if (string.IsNullOrEmpty(regPath)) return (false, "Adaptador de rede ativo não encontrado.");

            string cleanRegPath = regPath.Replace("HKEY_LOCAL_MACHINE\\", "");
            bool isApplied = IsTcpIpLatencyTweakApplied();
            try
            {

                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = localMachine.OpenSubKey(cleanRegPath, true);
                if (key == null)
                    return (false, "Não foi possível acessar registry do adaptador.");

                if (isApplied)
                {
                    key.DeleteValue("TcpAckFrequency", false);
                    key.DeleteValue("TCPNoDelay", false);
                    return (true, "Otimização de latência de rede desativada.");
                }
                else
                {
                    key.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                    key.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);
                    return (true, "Otimização de latência de rede ativada.");
                }
            }
            catch (Exception ex) { return (false, $"Erro: {ex.Message}"); }
        }

        private static string? GetActiveInterfaceRegPath()
        {
            try
            {
                var activeInterface = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(i => i.OperationalStatus == OperationalStatus.Up &&
                                          (i.NetworkInterfaceType == NetworkInterfaceType.Ethernet || i.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) &&
                                          i.GetIPProperties().GatewayAddresses.Any());
                return activeInterface != null ? $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{activeInterface.Id}" : null;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return null; }
        }
        #endregion

        #region Network Diagnostics & Troubleshooting
        public class NetworkDiagnosticResult
        {
            public string TestName { get; set; } = "";
            public bool Success { get; set; }
            public string Details { get; set; } = "";
            public string Recommendation { get; set; } = "";
        }

        public static List<NetworkDiagnosticResult> RunNetworkDiagnostics()
        {
            var results = new List<NetworkDiagnosticResult>();
            
            try
            {
                // 1. Testar conectividade básica
                results.Add(TestConnectivity());
                
                // 2. Verificar configuração de IP
                results.Add(CheckIPConfiguration());
                
                // 3. Testar resolução DNS
                results.Add(TestDNSResolution());
                
                // 4. Verificar adaptadores de rede
                results.Add(CheckNetworkAdapters());
                
                // 5. Testar conexões TCP
                results.Add(CheckTCPConnections());
                
                // 6. Verificar tabela de roteamento
                results.Add(CheckRoutingTable());
                
                // 7. Testar latência e velocidade
                results.Add(TestLatencyAndSpeed());
                
                // 8. Verificar serviços de rede
                results.Add(CheckNetworkServices());
                
                // 9. Limpar cache DNS se necessário
                results.Add(ClearDNSCacheIfNeeded());
            }
            catch (Exception ex)
            {
                results.Add(new NetworkDiagnosticResult
                {
                    TestName = "Erro Geral",
                    Success = false,
                    Details = $"Erro ao executar diagnósticos: {ex.Message}",
                    Recommendation = "Verifique se o aplicativo está sendo executado como administrador"
                });
            }
            
            return results;
        }

        private static NetworkDiagnosticResult TestConnectivity()
        {
            try
            {
                bool googleOk = false, cfOk = false;
                using (var ping = new Ping())
                {
                    try { googleOk = ping.Send("8.8.8.8", 3000)?.Status == IPStatus.Success; } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    try { cfOk = ping.Send("1.1.1.1", 3000)?.Status == IPStatus.Success; } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
                if (googleOk && cfOk)
                    return new NetworkDiagnosticResult { TestName = "Teste de Conectividade", Success = true, Details = "Conectividade com internet está funcionando (Google DNS e Cloudflare DNS)", Recommendation = "Sua conexão com internet está normal" };
                else
                    return new NetworkDiagnosticResult { TestName = "Teste de Conectividade", Success = false, Details = $"Falha ao conectar: Google DNS: {googleOk}, Cloudflare DNS: {cfOk}", Recommendation = "Verifique cabo de rede, roteador ou entre em contato com seu provedor" };
            }
            catch (Exception ex)
            {
                return new NetworkDiagnosticResult { TestName = "Teste de Conectividade", Success = false, Details = $"Erro ao testar conectividade: {ex.Message}", Recommendation = "Execute como administrador e verifique firewall" };
            }
        }

        private static NetworkDiagnosticResult CheckIPConfiguration()
        {
            try
            {
                var upInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(i => i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .ToList();
                bool hasConfig = upInterfaces.Any(i => i.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
                return new NetworkDiagnosticResult
                {
                    TestName = "Configuração de IP",
                    Success = hasConfig,
                    Details = hasConfig ? $"Configuração de IP obtida ({upInterfaces.Count} interfaces ativas)" : "Nenhuma configuração IPv4 encontrada",
                    Recommendation = hasConfig ? "Verifique se os endereços IP estão corretos para sua rede" : "Verifique se os adaptadores de rede estão funcionando"
                };
            }
            catch (Exception ex)
            {
                return new NetworkDiagnosticResult { TestName = "Configuração de IP", Success = false, Details = $"Erro ao verificar IP: {ex.Message}", Recommendation = "Reinicie os adaptadores de rede" };
            }
        }

        private static NetworkDiagnosticResult TestDNSResolution()
        {
            try
            {
                bool googleOk = false, msOk = false;
                try { googleOk = System.Net.Dns.GetHostAddresses("google.com").Any(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { msOk = System.Net.Dns.GetHostAddresses("microsoft.com").Any(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                return new NetworkDiagnosticResult
                {
                    TestName = "Resolução DNS",
                    Success = googleOk || msOk,
                    Details = googleOk && msOk ? "Resolução DNS está funcionando" : googleOk ? "DNS funciona apenas para google.com" : msOk ? "DNS funciona apenas para microsoft.com" : "Falha na resolução DNS",
                    Recommendation = googleOk || msOk ? "DNS está operacional" : "Limpe o cache DNS ou altere para servidores DNS públicos (8.8.8.8, 1.1.1.1)"
                };
            }
            catch (Exception ex)
            {
                return new NetworkDiagnosticResult { TestName = "Resolução DNS", Success = false, Details = $"Erro ao testar DNS: {ex.Message}", Recommendation = "Configure manualmente os servidores DNS" };
            }
        }

        private static NetworkDiagnosticResult CheckNetworkAdapters()
        {
            try
            {
                var adapters = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(i => i.NetworkInterfaceType != NetworkInterfaceType.Loopback && i.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .ToList();
                int upCount = adapters.Count(i => i.OperationalStatus == OperationalStatus.Up);
                return new NetworkDiagnosticResult
                {
                    TestName = "Adaptadores de Rede",
                    Success = upCount > 0,
                    Details = $"{adapters.Count} adaptadores encontrados, {upCount} ativos",
                    Recommendation = upCount > 0 ? "Adaptadores funcionando normalmente" : "Verifique se os adaptadores estão ativados"
                };
            }
            catch (Exception ex)
            {
                return new NetworkDiagnosticResult { TestName = "Adaptadores de Rede", Success = false, Details = $"Erro ao verificar adaptadores: {ex.Message}", Recommendation = "Atualize os drivers de rede" };
            }
        }

        private static NetworkDiagnosticResult CheckTCPConnections()
        {
            try
            {
                var tcpConnections = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
                int established = tcpConnections.Count(c => c.State == TcpState.Established);
                return new NetworkDiagnosticResult
                {
                    TestName = "Conexões TCP",
                    Success = true,
                    Details = $"{established} conexões TCP estabelecidas de {tcpConnections.Length} total",
                    Recommendation = "Conexões TCP monitoradas com sucesso"
                };
            }
            catch (Exception ex)
            {
                return new NetworkDiagnosticResult { TestName = "Conexões TCP", Success = false, Details = $"Erro ao verificar conexões: {ex.Message}", Recommendation = "Verifique firewall e configurações de rede" };
            }
        }

        private static NetworkDiagnosticResult CheckRoutingTable()
        {
            try
            {
                int routeCount = 0;
                using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_IP4RouteTable"))
                {
                    routeCount = searcher.Get().Count;
                }
                return new NetworkDiagnosticResult
                {
                    TestName = "Tabela de Roteamento",
                    Success = routeCount > 0,
                    Details = routeCount > 0 ? $"Tabela de roteamento obtida ({routeCount} rotas)" : "Nenhuma rota encontrada",
                    Recommendation = routeCount > 0 ? "Rotas de rede estão configuradas" : "Verifique configuração de gateway padrão"
                };
            }
            catch (Exception ex)
            {
                return new NetworkDiagnosticResult { TestName = "Tabela de Roteamento", Success = false, Details = $"Erro ao verificar rotas: {ex.Message}", Recommendation = "Reinicie o serviço de rede" };
            }
        }

        private static NetworkDiagnosticResult TestLatencyAndSpeed()
        {
            try
            {
                long pingMs = -1;
                using (var ping = new Ping())
                {
                    var reply = ping.Send("8.8.8.8", 3000);
                    if (reply?.Status == IPStatus.Success) pingMs = reply.RoundtripTime;
                }
                bool tcpOk = false;
                using (var tcp = new System.Net.Sockets.TcpClient())
                {
                    try { tcp.Connect("8.8.8.8", 53); tcpOk = true; } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
                return new NetworkDiagnosticResult
                {
                    TestName = "Teste de Latência",
                    Success = pingMs >= 0 || tcpOk,
                    Details = pingMs >= 0 ? $"Latência ICMP: {pingMs}ms" : tcpOk ? "Conexão TCP: OK" : "Falha no teste de latência",
                    Recommendation = pingMs >= 0 && pingMs < 150 ? "Latência dentro do normal" : pingMs >= 150 ? "Latência elevada — verifique qualidade da conexão" : "Verifique qualidade da conexão e congestionamento"
                };
            }
            catch (Exception ex)
            {
                return new NetworkDiagnosticResult { TestName = "Teste de Latência", Success = false, Details = $"Erro ao testar latência: {ex.Message}", Recommendation = "Teste com outro servidor ou verifique firewall" };
            }
        }

        private static NetworkDiagnosticResult CheckNetworkServices()
        {
            try
            {
                string[] serviceNames = { "NetMan", "Netlogon", "LanmanServer", "LanmanWorkstation" };
                int running = 0;
                foreach (var svc in serviceNames)
                {
                    using (var sc = new ServiceController(svc)) { try { if (sc.Status == ServiceControllerStatus.Running) running++; } catch { Logger.LogWarning("Unknown", "Exception suppressed"); } }
                }
                return new NetworkDiagnosticResult
                {
                    TestName = "Serviços de Rede",
                    Success = running > 0,
                    Details = $"{running}/{serviceNames.Length} serviços de rede em execução",
                    Recommendation = running > 2 ? "Serviços de rede funcionando" : "Alguns serviços podem precisar ser reiniciados"
                };
            }
            catch (Exception ex)
            {
                return new NetworkDiagnosticResult { TestName = "Serviços de Rede", Success = false, Details = $"Erro ao verificar serviços: {ex.Message}", Recommendation = "Execute como administrador" };
            }
        }

        private static NetworkDiagnosticResult ClearDNSCacheIfNeeded()
        {
            try
            {
                using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ipconfig", "/flushdns") { CreateNoWindow = true, UseShellExecute = false }))
                {
                    p?.WaitForExit(5000);
                }
                return new NetworkDiagnosticResult { TestName = "Cache DNS", Success = true, Details = "Cache DNS limpo com sucesso", Recommendation = "Cache DNS foi limpo para resolver problemas de navegação" };
            }
            catch (Exception ex)
            {
                return new NetworkDiagnosticResult { TestName = "Cache DNS", Success = false, Details = $"Erro ao limpar cache DNS: {ex.Message}", Recommendation = "Execute como administrador para limpar o cache" };
            }
        }

        public static (bool Success, string Message) RepairNetworkIssues()
        {
            try
            {
                var results = new List<string>();
                
                // 1. Resetar adaptadores de rede via WMI
                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapter WHERE NetEnabled=True AND AdapterTypeID=0"))
                    {
                        var adapters = searcher.Get().Cast<ManagementObject>().ToList();
                        foreach (var adapter in adapters)
                        {
                            try { adapter.InvokeMethod("Disable", null); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                        }
                        System.Threading.Thread.Sleep(2000);
                        foreach (var adapter in adapters)
                        {
                            try { adapter.InvokeMethod("Enable", null); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                        }
                    }
                    results.Add("Adaptadores de rede resetados");
                }
                catch { results.Add("Falha ao resetar adaptadores (continue)");
                }
                
                // 2. Limpar cache DNS
                ClearDNSCacheIfNeeded();
                results.Add("Cache DNS limpo");
                
                // 3. Resetar Winsock
                using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("netsh", "winsock reset") { CreateNoWindow = true, UseShellExecute = false })) p?.WaitForExit(5000);
                results.Add("Winsock resetado");
                
                // 4. Resetar proxy WinHTTP
                using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("netsh", "winhttp reset proxy") { CreateNoWindow = true, UseShellExecute = false })) p?.WaitForExit(5000);
                results.Add("Configuração de proxy resetada");
                
                // 5. Reiniciar serviços de rede
                foreach (string svc in new[] { "Netlogon", "LanmanWorkstation" })
                {
                    try { using (var sc = new ServiceController(svc)) { if (sc.Status == ServiceControllerStatus.Running) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10)); } sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10)); } } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
                results.Add("Serviços de rede reiniciados");
                
                return (true, $"Reparação concluída: {string.Join(", ", results)}");
            }
            catch (Exception ex)
            {
                return (false, $"Erro na reparação: {ex.Message}");
            }
        }

        public static (bool Success, string Message) OptimizeNetworkForGaming()
        {
            try
            {
                var optimizations = new List<string>();

                // 1. Desativar Nagle's Algorithm (TCPNoDelay) via Registry em todas as interfaces
                try
                {
                    string interfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
                    using (var parent = Registry.LocalMachine.OpenSubKey(interfacesKey))
                    {
                        if (parent != null)
                        {
                            foreach (var sub in parent.GetSubKeyNames())
                            {
                                using (var iface = parent.OpenSubKey(sub, true))
                                {
                                    if (iface != null)
                                    {
                                        iface.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                                        iface.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);
                                    }
                                }
                            }
                        }
                    }
                    optimizations.Add("Nagle's Algorithm desativado");
                }
                catch { optimizations.Add("Falha ao desativar Nagle (continue)"); }

                // 2. Otimizar QoS
                try
                {
                    Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Psched", "BestEffortLimit", 0, RegistryValueKind.DWord);
                    optimizations.Add("QoS otimizado");
                }
                catch { optimizations.Add("Falha ao otimizar QoS (continue)"); }

                // 3. Desativar autotuning (netsh — sem API gerenciada equivalente)
                using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("netsh", "interface tcp set global autotuninglevel=restricted") { CreateNoWindow = true, UseShellExecute = false })) p?.WaitForExit(5000);
                optimizations.Add("Auto-tuning restrito");

                return (true, $"Otimizações aplicadas: {string.Join(", ", optimizations)}");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao otimizar rede: {ex.Message}");
            }
        }
        #endregion

        #region Power & Events
        public static (bool Success, string Message, string? NewGuid) ImportAndActivatePowerPlan(string resourceName)
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "KitLugia_Plan.pow");
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return (false, "Arquivo de plano de energia não encontrado nos recursos do projeto.", null);
                    using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write)) { stream.CopyTo(fs); }
                }

                string output = SystemUtils.RunExternalProcess("powercfg", $"/import \"{tempFile}\"", true);
                var match = new Regex(@"[a-fA-F0-9]{8}-([a-fA-F0-9]{4}-){3}[a-fA-F0-9]{12}").Match(output);
                if (match.Success)
                {
                    SystemUtils.RunExternalProcess("powercfg", $"/setactive {match.Value}", true);
                    return (true, "Plano de energia importado e ativado com sucesso.", match.Value);
                }
                return (false, "Não foi possível extrair o GUID do plano de energia importado.", null);
            }
            catch (Exception ex) { return (false, $"Erro ao importar plano de energia: {ex.Message}", null); }
            finally { if (File.Exists(tempFile)) File.Delete(tempFile); }
        }

        public static List<PerformanceEvent> GetPerformanceEvents(int startId, int midId, int endId)
        {
            var events = new List<PerformanceEvent>();
            try
            {
                string query = $"*[System/Level<=4 and System/Provider[@Name='Microsoft-Windows-Diagnostics-Performance'] and System/EventID >= {startId} and System/EventID <= {endId}]";
                var logQuery = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName, query);

                using (var reader = new EventLogReader(logQuery))
                {
                    for (EventRecord record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
                    {
                        try
                        {
                            var xml = XDocument.Parse(record.ToXml());
                            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
                            var eventData = xml.Descendants(ns + "EventData").FirstOrDefault();
                            if (eventData == null) continue;
                            string GetValue(string name) => eventData.Elements(ns + "Data").FirstOrDefault(e => e.Attribute("Name")?.Value == name)?.Value ?? string.Empty;
                            string itemName = GetValue("BootPostBootTime") != string.Empty ? "Tempo Total de Boot" : (GetValue("FileName") != string.Empty ? GetValue("FileName") : "Item Desconhecido");
                            long.TryParse(GetValue("BootTime") ?? GetValue("ShutdownTime") ?? GetValue("MainPathBootTime") ?? "0", out long timeTaken);
                            events.Add(new PerformanceEvent(record.Id, itemName, timeTaken, "Boot/Shutdown", record.TimeCreated));
                        }
                        catch { /* Ignora erro de parsing */ }
                    }
                }
            }
            catch { /* Ignora erro ao ler log */ }
            return events;
        }
        #endregion

        #region Startup (TaskScheduler Wrapper)
        public static List<StartupAppDetails> GetStartupAppsWithDetails(bool bypassElevationCheck)
        {
            return StartupManager.GetStartupAppsWithDetails(bypassElevationCheck);
        }

        public static void SetStartupItemState(string name, bool enable, bool silentMode = false)
        {
            StartupManager.SetStartupItemState(name, enable, silentMode);
        }

        public static void CreateElevatedStartupTask(string name, string path, string? args)
        {
            StartupManager.CreateElevatedStartupTask(name, path, args);
        }

        public static bool CreateDelayedStartupTask(string name, string path, string? args)
        {
            return StartupManager.CreateDelayedStartupTask(name, path, args).Success;
        }

        public static void ResetEthernetSettings()
        {
            try
            {
                SystemUtils.RunExternalProcess("netsh", "int ip reset", true);
                SystemUtils.RunExternalProcess("netsh", "winsock reset", true);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static void AutoTuneNetworkAdapter()
        {
            try
            {
                SystemUtils.RunExternalProcess("netsh", "int tcp set global autotuninglevel=normal", true);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        /// <summary>
        /// Otimiza adaptador de rede para gaming (NIC settings).
        /// Desabilita Interrupt Moderation, Power Savings e configura buffers.
        /// Baseado em guías 2024-2026.
        /// Atualiza registry diretamente para garantir verificação correta.
        /// </summary>
        public static (bool Success, string Message) OptimizeNetworkAdapterForGaming()
        {
            try
            {
                // Obter adaptadores de rede físicos
                var adapters = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-Command \"Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and $_.Virtual -eq $false } | Select-Object -ExpandProperty Name\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });

                if (adapters != null)
                {
                    string output = adapters.StandardOutput.ReadToEnd();
                    adapters.WaitForExit();

                    string[] adapterNames = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                    int optimizedCount = 0;
                    foreach (var adapterName in adapterNames)
                    {
                        if (string.IsNullOrWhiteSpace(adapterName)) continue;

                        try
                        {
                            // Desabilitar Interrupt Moderation (reduz latência)
                            SystemUtils.RunExternalProcess("netsh", $"int ip set interface \"{adapterName}\" interruptmoderation=disabled", true);

                            // Configurar RSS no adaptador
                            SystemUtils.RunExternalProcess("netsh", $"int ip set interface \"{adapterName}\" rss=enabled", true);

                            optimizedCount++;
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }


                    // Define *InterruptModeration=0 em todas as interfaces TCP com IP configurado

                    using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using var tcpipKey = localMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", false);
                    if (tcpipKey != null)
                    {
                        using var interfacesKey = tcpipKey.OpenSubKey("Interfaces", false);
                        if (interfacesKey != null)
                        {
                            foreach (string subKeyName in interfacesKey.GetSubKeyNames())
                            {
                                using var subKey = interfacesKey.OpenSubKey(subKeyName, false);
                                if (subKey == null)
                                    continue;

                                var ipAddress = subKey.GetValue("IPAddress");
                                var dhcpIpAddress = subKey.GetValue("DhcpIPAddress");

                                if (ipAddress != null || dhcpIpAddress != null)
                                {
                                    // Esta interface tem IP configurado, definir *InterruptModeration=0
                                    using var writeKey = tcpipKey.OpenSubKey($@"Interfaces\{subKeyName}", true);
                                    writeKey?.SetValue("*InterruptModeration", "0", RegistryValueKind.String);
                                }
                            }
                        }
                    }

                    return (true, $"{optimizedCount} adaptador(es) otimizado(s) para gaming.");
                }

                return (false, "Não foi possível obter lista de adaptadores.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao otimizar adaptador: {ex.Message}");
            }
        }

        /// <summary>
        /// Reverte configurações do adaptador para padrão.
        /// Atualiza registry diretamente para garantir verificação correta.
        /// </summary>
        public static (bool Success, string Message) RevertNetworkAdapterSettings()
        {
            try
            {
                var adapters = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-Command \"Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and $_.Virtual -eq $false } | Select-Object -ExpandProperty Name\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                });

                if (adapters != null)
                {
                    string output = adapters.StandardOutput.ReadToEnd();
                    adapters.WaitForExit();

                    string[] adapterNames = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                    int revertedCount = 0;
                    foreach (var adapterName in adapterNames)
                    {
                        if (string.IsNullOrWhiteSpace(adapterName)) continue;

                        try
                        {
                            // Habilitar Interrupt Moderation (padrão)
                            SystemUtils.RunExternalProcess("netsh", $"int ip set interface \"{adapterName}\" interruptmoderation=enabled", true);

                            // RSS (padrão)
                            SystemUtils.RunExternalProcess("netsh", $"int ip set interface \"{adapterName}\" rss=enabled", true);

                            revertedCount++;
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }


                    // Remove *InterruptModeration de todas as interfaces TCP (reverte para padrão)

                    using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using var tcpipKey = localMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", false);
                    if (tcpipKey != null)
                    {
                        using var interfacesKey = tcpipKey.OpenSubKey("Interfaces", false);
                        if (interfacesKey != null)
                        {
                            foreach (string subKeyName in interfacesKey.GetSubKeyNames())
                            {
                                using var subKey = interfacesKey.OpenSubKey(subKeyName, false);
                                if (subKey == null)
                                    continue;

                                var ipAddress = subKey.GetValue("IPAddress");
                                var dhcpIpAddress = subKey.GetValue("DhcpIPAddress");

                                if (ipAddress != null || dhcpIpAddress != null)
                                {
                                    // Esta interface tem IP configurado, remover *InterruptModeration (reverte para padrão)
                                    using var writeKey = tcpipKey.OpenSubKey($@"Interfaces\{subKeyName}", true);
                                    writeKey?.DeleteValue("*InterruptModeration", false);
                                }
                            }
                        }
                    }

                    return (true, $"{revertedCount} adaptador(es) revertido(s) para padrão.");
                }

                return (false, "Não foi possível obter lista de adaptadores.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao reverter adaptador: {ex.Message}");
            }
        }

        /// <summary>
        /// Desabilita Nagle's Algorithm para reduzir latência em jogos.
        /// TcpAckFrequency=1, TCPNoDelay=1, TcpDelAckTicks=0
        /// Aplica a TODAS as interfaces TCP com IP configurado.
        /// </summary>
        public static (bool Success, string Message) DisableNagleAlgorithm()
        {
            try
            {

                var isAdmin = new System.Security.Principal.WindowsPrincipal(
                    System.Security.Principal.WindowsIdentity.GetCurrent())
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

                if (!isAdmin)
                {
                    return (false, "O aplicativo não está rodando como Administrador. Execute como Admin.");
                }

                string interfacesPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";


                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var tcpipKey = localMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", false);
                if (tcpipKey == null)
                {
                    return (false, $"Não foi possível acessar Tcpip\\Parameters. Path: {interfacesPath}");
                }

                using var interfacesKey = tcpipKey.OpenSubKey("Interfaces", false);
                if (interfacesKey == null)
                {
                    return (false, $"Não foi possível acessar Interfaces. Path: {interfacesPath}");
                }

                int appliedCount = 0;
                foreach (string interfaceGuid in interfacesKey.GetSubKeyNames())
                {
                    try
                    {
                        using var interfaceKey = interfacesKey.OpenSubKey(interfaceGuid, false);
                        if (interfaceKey != null)
                        {
                            var ipAddress = interfaceKey.GetValue("IPAddress");
                            var dhcpIPAddress = interfaceKey.GetValue("DhcpIPAddress");

                            // Verificar se tem IP configurado (não é loopback)
                            if ((ipAddress != null && ipAddress.ToString()!.Length > 0) ||
                                (dhcpIPAddress != null && dhcpIPAddress.ToString()!.Length > 0))
                            {
                                // Aplicar tweaks nesta interface
                                try
                                {
                                    using var tcpKey = tcpipKey.OpenSubKey($@"Interfaces\{interfaceGuid}", true);
                                    if (tcpKey != null)
                                    {
                                        tcpKey.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                                        tcpKey.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);
                                        tcpKey.SetValue("TcpDelAckTicks", 0, RegistryValueKind.DWord);
                                        appliedCount++;
                                    }
                                    else
                                    {

                                        return (false, $"Não foi possível abrir interface {interfaceGuid} com permissão de escrita.");
                                    }
                                }
                                catch (UnauthorizedAccessException ex)
                                {
                                    return (false, $"Permissão negada ao escrever em {interfaceGuid}: {ex.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        return (false, $"Erro ao processar interface {interfaceGuid}: {ex.Message}");
                    }
                }

                if (appliedCount > 0)
                {
                    return (true, $"Nagle's Algorithm desabilitado em {appliedCount} interface(s) TCP.");
                }
                else
                {
                    return (false, "Não foi possível encontrar interface TCP com IP configurado.");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao desabilitar Nagle's Algorithm: {ex.Message}");
            }
        }

        /// <summary>
        /// Reverte Nagle's Algorithm para padrão.
        /// Remove de TODAS as interfaces TCP com IP configurado.
        /// </summary>
        public static (bool Success, string Message) RevertNagleAlgorithm()
        {
            try
            {

                var isAdmin = new System.Security.Principal.WindowsPrincipal(
                    System.Security.Principal.WindowsIdentity.GetCurrent())
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

                if (!isAdmin)
                {
                    return (false, "O aplicativo não está rodando como Administrador. Execute como Admin.");
                }

                string interfacesPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";


                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var tcpipKey = localMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", false);
                if (tcpipKey == null)
                {
                    return (false, $"Não foi possível acessar Tcpip\\Parameters. Path: {interfacesPath}");
                }

                using var interfacesKey = tcpipKey.OpenSubKey("Interfaces", false);
                if (interfacesKey == null)
                {
                    return (false, $"Não foi possível acessar Interfaces. Path: {interfacesPath}");
                }

                int revertedCount = 0;
                foreach (string interfaceGuid in interfacesKey.GetSubKeyNames())
                {
                    try
                    {
                        using var interfaceKey = interfacesKey.OpenSubKey(interfaceGuid, false);
                        if (interfaceKey != null)
                        {
                            var ipAddress = interfaceKey.GetValue("IPAddress");
                            var dhcpIPAddress = interfaceKey.GetValue("DhcpIPAddress");

                            // Verificar se tem IP configurado (não é loopback)
                            if ((ipAddress != null && ipAddress.ToString()!.Length > 0) ||
                                (dhcpIPAddress != null && dhcpIPAddress.ToString()!.Length > 0))
                            {
                                // Remover tweaks desta interface
                                try
                                {
                                    using var tcpKey = tcpipKey.OpenSubKey($@"Interfaces\{interfaceGuid}", true);
                                    if (tcpKey != null)
                                    {
                                        tcpKey.DeleteValue("TcpAckFrequency", false);
                                        tcpKey.DeleteValue("TCPNoDelay", false);
                                        tcpKey.DeleteValue("TcpDelAckTicks", false);
                                        revertedCount++;
                                    }
                                    else
                                    {

                                        return (false, $"Não foi possível abrir interface {interfaceGuid} com permissão de escrita.");
                                    }
                                }
                                catch (UnauthorizedAccessException ex)
                                {
                                    return (false, $"Permissão negada ao escrever em {interfaceGuid}: {ex.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        return (false, $"Erro ao processar interface {interfaceGuid}: {ex.Message}");
                    }
                }

                if (revertedCount > 0)
                {
                    return (true, $"Nagle's Algorithm revertido em {revertedCount} interface(s) TCP.");
                }
                else
                {
                    return (false, "Não foi possível encontrar interface TCP com IP configurado.");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao reverter Nagle's Algorithm: {ex.Message}");
            }
        }
        #endregion

        #region Novas Otimizações 2025-2026 (Baseadas em Pesquisa)
        
        // 1. Startup Delay Optimization (Baseado em Spyboy 2025)
        public static void OptimizeStartupDelay()
        {
            try
            {
                using var startupKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer", true);
                if (startupKey != null)
                {
                    using var serializeKey = startupKey.CreateSubKey("Serialize");
                    serializeKey.SetValue("StartupDelayInMSec", 0, RegistryValueKind.DWord);
                }
                Logger.Log("Startup delay otimizado: 0ms");
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao otimizar startup delay: {ex.Message}");
            }
        }

        // 2. Shutdown Speed Optimization (Baseado em Spyboy 2025 + KitLugia Aggressive Mods)
        public static void OptimizeShutdownSpeed()
        {
            try
            {
                // Sistema Geral
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control", 
                    "WaitToKillServiceTimeout", 2000, RegistryValueKind.DWord);
                
                // Aplicativos de Usuário (Desktop)
                var desktopKey = @"HKEY_CURRENT_USER\Control Panel\Desktop";
                Registry.SetValue(desktopKey, "AutoEndTasks", "1", RegistryValueKind.String);
                Registry.SetValue(desktopKey, "WaitToKillAppTimeout", "2000", RegistryValueKind.String);
                Registry.SetValue(desktopKey, "HungAppTimeout", "1000", RegistryValueKind.String);

                Logger.Log("Shutdown speed otimizado: 2s Serviços, 2s Apps, 1s Travados, Auto-End ativado.");
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao otimizar shutdown speed: {ex.Message}");
            }
        }

        // 3. System Responsiveness (Baseado em Spyboy 2025)
        public static void OptimizeSystemResponsiveness()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                    "SystemResponsiveness", 10, RegistryValueKind.DWord);
                Logger.Log("System responsiveness otimizado: 10");
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao otimizar system responsiveness: {ex.Message}");
            }
        }

        // 4. Menu Show Delay (Baseado em Spyboy 2025)
        public static void OptimizeMenuDelay()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop",
                    "MenuShowDelay", 100, RegistryValueKind.String);
                Logger.Log("Menu delay otimizado: 100ms");
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao otimizar menu delay: {ex.Message}");
            }
        }

        // 5. Network Throttling Disable (Baseado em Spyboy 2025)
        public static void DisableNetworkThrottling()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                    "NetworkThrottlingIndex", 0xFFFFFFFF, RegistryValueKind.DWord);
                Logger.Log("Network throttling desativado: máximo desempenho");
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao desativar network throttling: {ex.Message}");
            }
        }

        // 6. Windows 11 24H2 Energy Saver API Integration
        public static void OptimizeEnergySaver()
        {
            try
            {
                // Novo GUID para Energy Saver Status (Windows 11 24H2)
                using var powerKey = Registry.CurrentUser.OpenSubKey(@"Control Panel\Power", true);
                if (powerKey != null) powerKey.SetValue("EnergySaverPolicy", 1, RegistryValueKind.DWord);
                Logger.Log("Energy Saver otimizado para performance");
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao otimizar energy saver: {ex.Message}");
            }
        }

        // 7. SHA-3 Support Verification (Windows 11 24H2)
        public static bool IsSHA3Supported()
        {
            try
            {
                using var cngKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography\Defaults\Provider Types");
                var sha3Support = cngKey?.GetValue("SHA3") != null;
                Logger.Log($"SHA-3 support: {sha3Support}");
                return sha3Support;
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao verificar SHA-3 support: {ex.Message}");
                return false;
            }
        }

        // 8. Wi-Fi 7 Optimization (Windows 11 24H2)

        // 9. Bluetooth LE Audio Optimization (Windows 11 24H2)
        public static void OptimizeBluetoothLE()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Bluetooth\Audio",
                    "LEAudioOptimization", 1, RegistryValueKind.DWord);
                Logger.Log("Bluetooth LE Audio otimizado para assistive devices");
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao otimizar Bluetooth LE: {ex.Message}");
            }
        }

        // 10. Windows Protected Print Mode (Windows 11 24H2)
        public static void EnableProtectedPrintMode()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows NT\Printers",
                    "ProtectedPrint", 1, RegistryValueKind.DWord);
                Logger.Log("Windows Protected Print Mode ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao ativar protected print mode: {ex.Message}");
            }
        }

        // 11. App Control for Business (Windows 11 24H2)
        public static void ConfigureAppControl()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppControl",
                    "BusinessMode", 1, RegistryValueKind.DWord);
                Logger.Log("App Control for Business configurado");
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao configurar App Control: {ex.Message}");
            }
        }

        // 12. Rust Kernel Optimization (Windows 11 24H2)

        // 13. Personal Data Encryption for Folders (Windows 11 24H2)

        // 14. LAPS Integration (Windows 11 24H2)

        // 15. Sudo for Windows (Windows 11 24H2)
        public static void EnableSudo()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System",
                    "EnableSudo", 1, RegistryValueKind.DWord);
                Logger.Log("Sudo for Windows ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao ativar Sudo: {ex.Message}");
            }
        }

        // Métodos de verificação para as novas otimizações
        public static bool IsStartupDelayOptimized()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", 
                    "StartupDelayInMSec", 0);
                return Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static bool IsShutdownSpeedOptimized()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control", 
                    "WaitToKillServiceTimeout", 5000);
                return Convert.ToInt32(value) == 2000;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static bool IsNetworkThrottlingDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                    "NetworkThrottlingIndex", 10);
                long valueLong = Convert.ToInt64(value);

                return valueLong == 0xFFFFFFFF || valueLong != 10;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }


        public static bool IsBluetoothLEOptimized()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Bluetooth\Audio", 
                    "LEAudioOptimization", 0);
                return Convert.ToInt32(value) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static bool IsProtectedPrintModeEnabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows NT\Printers", 
                    "ProtectedPrint", 0);
                return Convert.ToInt32(value) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }


        public static bool IsSudoEnabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", 
                    "EnableSudo", 0);
                return Convert.ToInt32(value) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        #endregion
        // =========================================================
        // SEÇÃO: SLIDE ENGINE (Ultra-Low Latency & FPS Optimization)
        // =========================================================
        
        public static (bool Success, string Message) OptimizeInputLatency()
        {
            try
            {
                // Keyboard & Mouse Thread Priority (kbdclass & mouclass -> 31)
                using var kbdKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", true) ?? Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", true);
                kbdKey.SetValue("ThreadPriority", 31, RegistryValueKind.DWord);

                using var mouKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\mouclass\Parameters", true) ?? Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\mouclass\Parameters", true);
                mouKey.SetValue("ThreadPriority", 31, RegistryValueKind.DWord);

                // Keyboard Latency & Speed
                using var cpKbdKey = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard", true) ?? Registry.CurrentUser.CreateSubKey(@"Control Panel\Keyboard", true);
                cpKbdKey.SetValue("KeyboardDelay", "0", RegistryValueKind.String);
                cpKbdKey.SetValue("KeyboardSpeed", "31", RegistryValueKind.String);

                // Mouse Response
                using var cpMouKey = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse", true) ?? Registry.CurrentUser.CreateSubKey(@"Control Panel\Mouse", true);
                cpMouKey.SetValue("MouseHoverTime", "8", RegistryValueKind.String);
                cpMouKey.SetValue("MouseSpeed", "0", RegistryValueKind.String);
                cpMouKey.SetValue("MouseThreshold1", "0", RegistryValueKind.String);
                cpMouKey.SetValue("MouseThreshold2", "0", RegistryValueKind.String);
                cpMouKey.SetValue("MouseTrails", "0", RegistryValueKind.String);
                cpMouKey.SetValue("SnapToDefaultButton", "0", RegistryValueKind.String);

                // Controller Polling Rate
                using var ctrlKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Input\Settings\ControllerProcessor\CursorSpeed", true) ?? Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Input\Settings\ControllerProcessor\CursorSpeed", true);
                ctrlKey.SetValue("CursorSensitivity", 10000, RegistryValueKind.DWord);
                ctrlKey.SetValue("CursorUpdateInterval", 1, RegistryValueKind.DWord);

                using var magKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Input\Settings\ControllerProcessor\CursorMagnetism", true) ?? Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Input\Settings\ControllerProcessor\CursorMagnetism", true);
                magKey.SetValue("VelocityInDIPSPerSecond", 360, RegistryValueKind.DWord);
                magKey.SetValue("MagnetismUpdateIntervalInMilliseconds", 16, RegistryValueKind.DWord);

                Logger.Log("SLIDE: Input Latency otimizado (Pri 31, Polling Rate).");
                return (true, "Latência de entrada minimizada com sucesso.");
            }
            catch (Exception ex)
            {
                Logger.LogError("OptimizeInputLatency", ex.Message);
                return (false, "Falha ao definir propriedades de latência de entrada.");
            }
        }

        public static (bool Success, string Message) DisableUsbPowerSaving()
        {
            try
            {
                int modifiedCount = 0;
                using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum", true);
                if (enumKey != null)
                {
                    // Busca varredura recursiva simplificada pelos VIDs
                    foreach (string mainBranch in enumKey.GetSubKeyNames()) // ACPI, USB, USBSTOR, etc.
                    {
                        using var branchKey = enumKey.OpenSubKey(mainBranch, true);
                        if (branchKey == null) continue;
                        
                        foreach (string deviceId in branchKey.GetSubKeyNames())
                        {
                            if (deviceId.Contains("VID_", StringComparison.OrdinalIgnoreCase))
                            {
                                using var deviceKey = branchKey.OpenSubKey(deviceId, true);
                                if (deviceKey == null) continue;

                                foreach (string instanceId in deviceKey.GetSubKeyNames())
                                {
                                    using var devParamsKey = deviceKey.OpenSubKey($@"{instanceId}\Device Parameters", true);
                                    if (devParamsKey != null)
                                    {
                                        devParamsKey.SetValue("EnhancedPowerManagementEnabled", 0, RegistryValueKind.DWord);
                                        devParamsKey.SetValue("AllowIdleIrpInD3", 0, RegistryValueKind.DWord);
                                        devParamsKey.SetValue("DeviceSelectiveSuspended", 0, RegistryValueKind.DWord);
                                        devParamsKey.SetValue("SelectiveSuspendEnabled", new byte[] { 0x00 }, RegistryValueKind.Binary);
                                        devParamsKey.SetValue("SelectiveSuspendOn", 0, RegistryValueKind.DWord);
                                        devParamsKey.SetValue("fid_D1Latency", 0, RegistryValueKind.DWord);
                                        devParamsKey.SetValue("fid_D2Latency", 0, RegistryValueKind.DWord);
                                        devParamsKey.SetValue("fid_D3Latency", 0, RegistryValueKind.DWord);

                                        using var wdfKey = devParamsKey.OpenSubKey("WDF", true) ?? devParamsKey.CreateSubKey("WDF", true);
                                        if (wdfKey != null) wdfKey.SetValue("IdleInWorkingState", 0, RegistryValueKind.DWord);
                                        
                                        modifiedCount++;
                                    }
                                }
                            }
                        }
                    }
                }

                Logger.Log($"SLIDE: USB Power Saving desativado em {modifiedCount} dispositivos.");
                return (true, "Gerenciamento de energia USB (Selective Suspend) erradicado.");
            }
            catch (Exception ex)
            {
                Logger.LogError("DisableUsbPowerSaving", ex.Message);
                return (false, "Falha ao buscar e desativar economia de energia USB. Verifique elevação.");
            }
        }

        public static (bool Success, string Message) OptimizeGamingLatency()
        {
            try
            {
                // Win32PrioritySeparation
                using var prioKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\PriorityControl", true);
                if (prioKey != null) prioKey.SetValue("Win32PrioritySeparation", 38, RegistryValueKind.DWord); // 0x26 em Decimal
                
                // Network Throttling / Responsiveness
                using var sysProfKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", true);
                if (sysProfKey != null)
                {
                    sysProfKey.SetValue("NetworkThrottlingIndex", unchecked((int)4294967295), RegistryValueKind.DWord);
                    sysProfKey.SetValue("SystemResponsiveness", 0, RegistryValueKind.DWord);
                }

                using var gamesProfKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", true);
                if (gamesProfKey != null)
                {
                    gamesProfKey.SetValue("GPU Priority", 8, RegistryValueKind.DWord);
                    gamesProfKey.SetValue("Priority", 6, RegistryValueKind.DWord);
                    gamesProfKey.SetValue("Scheduling Category", "High", RegistryValueKind.String);
                    gamesProfKey.SetValue("SFIO Priority", "High", RegistryValueKind.String);
                }

                // DWM e Efeitos
                using var dwmKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM", true) ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\DWM", true);
                dwmKey.SetValue("Composition", 1, RegistryValueKind.DWord);
                dwmKey.SetValue("Animations", 0, RegistryValueKind.DWord);
                dwmKey.SetValue("EnableAeroPeek", 0, RegistryValueKind.DWord);
                dwmKey.SetValue("OverlayTestMode", 5, RegistryValueKind.DWord);

                // Disable Game DVR
                using var dvrCUKey = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore", true) ?? Registry.CurrentUser.CreateSubKey(@"System\GameConfigStore", true);
                dvrCUKey.SetValue("GameDVR_Enabled", 0, RegistryValueKind.DWord);

                using var appCapKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", true) ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", true);
                appCapKey.SetValue("AppCaptureEnabled", 0, RegistryValueKind.DWord);

                using var dvrLMKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\GameDVR", true) ?? Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\GameDVR", true);
                dvrLMKey.SetValue("AllowgameDVR", 0, RegistryValueKind.DWord);

                Logger.Log("SLIDE: Parâmetros de Latência de Jogo (Nagle, GPU, GameDVR) aplicados.");
                return (true, "Sistema ajustado para máxima prioridade de quadros (FPS).");
            }
            catch (Exception ex)
            {
                Logger.LogError("OptimizeGamingLatency", ex.Message);
                return (false, "Falha ao definir limites de latência do sistema.");
            }
        }

        // =========================================================
        // SEÇÃO: SLIDE ENGINE - REVERSÕES E CHECAGENS DE ESTADO
        // =========================================================

        public static bool IsInputLatencyOptimized()
        {
            try
            {
                using var kbdKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", false);
                if (kbdKey != null)
                {
                    var val = kbdKey.GetValue("ThreadPriority");
                    return val != null && Convert.ToInt32(val) >= 30; // Considerando 31 como otimizado
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return false;
        }

        public static (bool Success, string Message) RevertInputLatency()
        {
            try
            {
                using var kbdKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", true);
                kbdKey?.SetValue("ThreadPriority", 16, RegistryValueKind.DWord);

                using var mouKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\mouclass\Parameters", true);
                mouKey?.SetValue("ThreadPriority", 16, RegistryValueKind.DWord);

                using var cpKbdKey = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard", true);
                cpKbdKey?.SetValue("KeyboardDelay", "1", RegistryValueKind.String);
                cpKbdKey?.SetValue("KeyboardSpeed", "31", RegistryValueKind.String);

                using var cpMouKey = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse", true);
                cpMouKey?.SetValue("MouseHoverTime", "400", RegistryValueKind.String);
                cpMouKey?.SetValue("MouseSpeed", "1", RegistryValueKind.String);

                using var ctrlKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Input\Settings\ControllerProcessor\CursorSpeed", true);
                ctrlKey?.SetValue("CursorUpdateInterval", 10, RegistryValueKind.DWord);

                Logger.Log("SLIDE: Latência de entrada revertida aos padrões.");
                return (true, "Latência de entrada restaurada para os padrões do Windows.");
            }
            catch (Exception ex)
            {
                Logger.LogError("RevertInputLatency", ex.Message);
                return (false, "Falha ao reverter latência de entrada.");
            }
        }

        public static bool IsUsbPowerSavingDisabled()
        {
            try
            {
                using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB", false);
                if (enumKey != null)
                {
                    foreach (string deviceId in enumKey.GetSubKeyNames())
                    {
                        using var deviceKey = enumKey.OpenSubKey(deviceId, false);
                        if (deviceKey == null) continue;
                        foreach (string instanceId in deviceKey.GetSubKeyNames())
                        {
                            using var devParamsKey = deviceKey.OpenSubKey($@"{instanceId}\Device Parameters", false);
                            if (devParamsKey != null)
                            {
                                var val = devParamsKey.GetValue("EnhancedPowerManagementEnabled");
                                if (val != null) return Convert.ToInt32(val) == 0;
                            }
                        }
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return false; // Não modificado ou erro
        }

        public static (bool Success, string Message) RevertUsbPowerSaving()
        {
            try
            {
                int modifiedCount = 0;
                using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum", true);
                if (enumKey != null)
                {
                    foreach (string mainBranch in enumKey.GetSubKeyNames())
                    {
                        using var branchKey = enumKey.OpenSubKey(mainBranch, true);
                        if (branchKey == null) continue;
                        foreach (string deviceId in branchKey.GetSubKeyNames())
                        {
                            if (deviceId.Contains("VID_", StringComparison.OrdinalIgnoreCase))
                            {
                                using var deviceKey = branchKey.OpenSubKey(deviceId, true);
                                if (deviceKey == null) continue;
                                foreach (string instanceId in deviceKey.GetSubKeyNames())
                                {
                                    using var devParamsKey = deviceKey.OpenSubKey($@"{instanceId}\Device Parameters", true);
                                    if (devParamsKey != null)
                                    {
                                        devParamsKey.SetValue("EnhancedPowerManagementEnabled", 1, RegistryValueKind.DWord);
                                        devParamsKey.SetValue("DeviceSelectiveSuspended", 1, RegistryValueKind.DWord);
                                        devParamsKey.SetValue("SelectiveSuspendEnabled", new byte[] { 0x01 }, RegistryValueKind.Binary);
                                        modifiedCount++;
                                    }
                                }
                            }
                        }
                    }
                }
                Logger.Log($"SLIDE: USB Power Saving restaurado em {modifiedCount} dispositivos.");
                return (true, "Economia de energia USB restaurada para o padrão.");
            }
            catch (Exception ex)
            {
                Logger.LogError("RevertUsbPowerSaving", ex.Message);
                return (false, "Falha ao restaurar economia de energia USB.");
            }
        }

        public static (bool Success, string Message) DisablePcieLinkStatePowerManagement()
        {
            try
            {
                var activeSchemeGuid = GetActivePowerSchemeGuid();
                if (activeSchemeGuid == null)
                    return (false, "Não foi possível obter o GUID do power scheme ativo.");

                Guid schemeGuid = activeSchemeGuid.Value;
                Guid pcieSubGroup = new Guid("501a4d13-42af-4429-9fd1-a8218c268e20");
                Guid pcieSetting = new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5");

                uint result = PowerWriteACValueIndex(IntPtr.Zero, ref schemeGuid, ref pcieSubGroup, ref pcieSetting, 0);
                if (result != 0) return (false, $"Falha ao definir ACSettingIndex: erro {result}");

                result = PowerWriteDCValueIndex(IntPtr.Zero, ref schemeGuid, ref pcieSubGroup, ref pcieSetting, 0);
                if (result != 0) return (false, $"Falha ao definir DCSettingIndex: erro {result}");

                IntPtr schemePtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
                try
                {
                    Marshal.StructureToPtr(schemeGuid, schemePtr, false);
                    result = PowerSetActiveScheme(IntPtr.Zero, schemePtr);
                    if (result != 0) return (false, $"Falha ao aplicar power scheme: erro {result}");
                }
                finally
                {
                    Marshal.FreeHGlobal(schemePtr);
                }

                Logger.Log("SLIDE: PCIe Link State Power Management desativado via Power Management API (ASPM=0/None)");
                return (true, "PCIe Link State Power Management desativado. Link PCIe sempre ativo para menor latência.");
            }
            catch (Exception ex)
            {
                Logger.LogError("DisablePcieLinkStatePowerManagement", ex.Message);
                return (false, $"Falha ao desativar PCIe Link State Power Management: {ex.Message}");
            }
        }

        public static (bool Success, string Message) EnablePcieLinkStatePowerManagement()
        {
            try
            {
                var activeSchemeGuid = GetActivePowerSchemeGuid();
                if (activeSchemeGuid == null)
                    return (false, "Não foi possível obter o GUID do power scheme ativo.");

                Guid schemeGuid = activeSchemeGuid.Value;
                Guid pcieSubGroup = new Guid("501a4d13-42af-4429-9fd1-a8218c268e20");
                Guid pcieSetting = new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5");

                uint result = PowerWriteACValueIndex(IntPtr.Zero, ref schemeGuid, ref pcieSubGroup, ref pcieSetting, 1);
                if (result != 0) return (false, $"Falha ao definir ACSettingIndex: erro {result}");

                result = PowerWriteDCValueIndex(IntPtr.Zero, ref schemeGuid, ref pcieSubGroup, ref pcieSetting, 1);
                if (result != 0) return (false, $"Falha ao definir DCSettingIndex: erro {result}");

                IntPtr schemePtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
                try
                {
                    Marshal.StructureToPtr(schemeGuid, schemePtr, false);
                    result = PowerSetActiveScheme(IntPtr.Zero, schemePtr);
                    if (result != 0) return (false, $"Falha ao aplicar power scheme: erro {result}");
                }
                finally
                {
                    Marshal.FreeHGlobal(schemePtr);
                }

                Logger.Log("SLIDE: PCIe Link State Power Management reativado via Power Management API (ASPM=1/Moderate)");
                return (true, "PCIe Link State Power Management reativado para economia de energia.");
            }
            catch (Exception ex)
            {
                Logger.LogError("EnablePcieLinkStatePowerManagement", ex.Message);
                return (false, $"Falha ao reativar PCIe Link State Power Management: {ex.Message}");
            }
        }

        public static bool IsPcieLinkStatePowerManagementDisabled()
        {
            try
            {
                var activeSchemeGuid = GetActivePowerSchemeGuid();
                if (activeSchemeGuid == null) return false;

                Guid schemeGuid = activeSchemeGuid.Value;
                Guid pcieSubGroup = new Guid("501a4d13-42af-4429-9fd1-a8218c268e20");
                Guid pcieSetting = new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5");

                uint acValue = 0;
                uint dcValue = 0;
                uint acResult = PowerReadACValueIndex(IntPtr.Zero, ref schemeGuid, ref pcieSubGroup, ref pcieSetting, ref acValue);
                uint dcResult = PowerReadDCValueIndex(IntPtr.Zero, ref schemeGuid, ref pcieSubGroup, ref pcieSetting, ref dcValue);

                return (acResult == 0 && acValue == 0) && (dcResult == 0 && dcValue == 0);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static (bool Success, string Message) DisableHardDiskDisplayTimeout()
        {
            try
            {
                var activeSchemeGuid = GetActivePowerSchemeGuid();
                if (activeSchemeGuid == null)
                    return (false, "Não foi possível obter o GUID do power scheme ativo.");

                Guid schemeGuid = activeSchemeGuid.Value;
                Guid diskSubGroup = new Guid("0012ee47-9041-4b5d-9b77-535fba8b1442");
                Guid diskSetting = new Guid("6738e2c4-e8a5-4a42-b16a-e040e769756e");

                uint result = PowerWriteACValueIndex(IntPtr.Zero, ref schemeGuid, ref diskSubGroup, ref diskSetting, 0);
                if (result != 0) return (false, $"Falha ao definir Disk ACSettingIndex: erro {result}");

                result = PowerWriteDCValueIndex(IntPtr.Zero, ref schemeGuid, ref diskSubGroup, ref diskSetting, 0);
                if (result != 0) return (false, $"Falha ao definir Disk DCSettingIndex: erro {result}");

                Guid monitorSubGroup = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
                Guid monitorSetting = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

                result = PowerWriteACValueIndex(IntPtr.Zero, ref schemeGuid, ref monitorSubGroup, ref monitorSetting, 0);
                if (result != 0) return (false, $"Falha ao definir Monitor ACSettingIndex: erro {result}");

                result = PowerWriteDCValueIndex(IntPtr.Zero, ref schemeGuid, ref monitorSubGroup, ref monitorSetting, 0);
                if (result != 0) return (false, $"Falha ao definir Monitor DCSettingIndex: erro {result}");

                IntPtr schemePtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
                try
                {
                    Marshal.StructureToPtr(schemeGuid, schemePtr, false);
                    result = PowerSetActiveScheme(IntPtr.Zero, schemePtr);
                    if (result != 0) return (false, $"Falha ao aplicar power scheme: erro {result}");
                }
                finally
                {
                    Marshal.FreeHGlobal(schemePtr);
                }

                Logger.Log("SLIDE: Hard Disk e Display timeout desativados via Power Management API (0=Never)");
                return (true, "Timeout de disco e tela desativados. Nunca desligam durante uso.");
            }
            catch (Exception ex)
            {
                Logger.LogError("DisableHardDiskDisplayTimeout", ex.Message);
                return (false, $"Falha ao desativar timeout de disco e tela: {ex.Message}");
            }
        }

        public static (bool Success, string Message) EnableHardDiskDisplayTimeout()
        {
            try
            {
                var activeSchemeGuid = GetActivePowerSchemeGuid();
                if (activeSchemeGuid == null)
                    return (false, "Não foi possível obter o GUID do power scheme ativo.");

                Guid schemeGuid = activeSchemeGuid.Value;
                Guid diskSubGroup = new Guid("0012ee47-9041-4b5d-9b77-535fba8b1442");
                Guid diskSetting = new Guid("6738e2c4-e8a5-4a42-b16a-e040e769756e");

                uint result = PowerWriteACValueIndex(IntPtr.Zero, ref schemeGuid, ref diskSubGroup, ref diskSetting, 900);
                if (result != 0) return (false, $"Falha ao definir Disk ACSettingIndex: erro {result}");

                result = PowerWriteDCValueIndex(IntPtr.Zero, ref schemeGuid, ref diskSubGroup, ref diskSetting, 900);
                if (result != 0) return (false, $"Falha ao definir Disk DCSettingIndex: erro {result}");

                Guid monitorSubGroup = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
                Guid monitorSetting = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

                result = PowerWriteACValueIndex(IntPtr.Zero, ref schemeGuid, ref monitorSubGroup, ref monitorSetting, 900);
                if (result != 0) return (false, $"Falha ao definir Monitor ACSettingIndex: erro {result}");

                result = PowerWriteDCValueIndex(IntPtr.Zero, ref schemeGuid, ref monitorSubGroup, ref monitorSetting, 900);
                if (result != 0) return (false, $"Falha ao definir Monitor DCSettingIndex: erro {result}");

                IntPtr schemePtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
                try
                {
                    Marshal.StructureToPtr(schemeGuid, schemePtr, false);
                    result = PowerSetActiveScheme(IntPtr.Zero, schemePtr);
                    if (result != 0) return (false, $"Falha ao aplicar power scheme: erro {result}");
                }
                finally
                {
                    Marshal.FreeHGlobal(schemePtr);
                }

                Logger.Log("SLIDE: Hard Disk e Display timeout reativados via Power Management API (15 minutos)");
                return (true, "Timeout de disco e tela reativados para 15 minutos.");
            }
            catch (Exception ex)
            {
                Logger.LogError("EnableHardDiskDisplayTimeout", ex.Message);
                return (false, $"Falha ao reativar timeout de disco e tela: {ex.Message}");
            }
        }

        public static bool IsHardDiskDisplayTimeoutDisabled()
        {
            try
            {
                var activeSchemeGuid = GetActivePowerSchemeGuid();
                if (activeSchemeGuid == null) return false;

                Guid schemeGuid = activeSchemeGuid.Value;
                Guid diskSubGroup = new Guid("0012ee47-9041-4b5d-9b77-535fba8b1442");
                Guid diskSetting = new Guid("6738e2c4-e8a5-4a42-b16a-e040e769756e");
                Guid monitorSubGroup = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
                Guid monitorSetting = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

                uint diskAc = 0, diskDc = 0, monAc = 0, monDc = 0;
                bool diskAcOk = PowerReadACValueIndex(IntPtr.Zero, ref schemeGuid, ref diskSubGroup, ref diskSetting, ref diskAc) == 0;
                bool diskDcOk = PowerReadDCValueIndex(IntPtr.Zero, ref schemeGuid, ref diskSubGroup, ref diskSetting, ref diskDc) == 0;
                bool monAcOk = PowerReadACValueIndex(IntPtr.Zero, ref schemeGuid, ref monitorSubGroup, ref monitorSetting, ref monAc) == 0;
                bool monDcOk = PowerReadDCValueIndex(IntPtr.Zero, ref schemeGuid, ref monitorSubGroup, ref monitorSetting, ref monDc) == 0;

                return diskAcOk && diskAc == 0 && diskDcOk && diskDc == 0
                    && monAcOk && monAc == 0 && monDcOk && monDc == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static (bool Success, string Message) ApplyBitsumHighestPerformanceSettings()
        {
            try
            {
                // Aplica todas as configurações do Bitsum Highest Performance via Power Management API nativa
                // Baseado no arquivo BitsumHighestPerformance.pow
                
                // Obtém GUID do power scheme ativo via API nativa
                IntPtr activeSchemePtr = IntPtr.Zero;
                uint result = PowerGetActiveScheme(IntPtr.Zero, ref activeSchemePtr);
                if (result != 0 || activeSchemePtr == IntPtr.Zero)
                {
                    return (false, "Não foi possível obter o GUID do power scheme ativo.");
                }
                
                Guid activeSchemeGuid = Marshal.PtrToStructure<Guid>(activeSchemePtr);
                
                // 1. PCIe Express ASPM - Desligado (0)
                Guid pcieSubGroup = new Guid("501a4d13-42af-4429-9fd1-a8218c268e20");
                Guid pcieSetting = new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5");
                result = PowerWriteACValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref pcieSubGroup, ref pcieSetting, 0);
                if (result != 0) return (false, $"Falha ao definir PCIe ASPM AC: erro {result}");
                result = PowerWriteDCValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref pcieSubGroup, ref pcieSetting, 0);
                if (result != 0) return (false, $"Falha ao definir PCIe ASPM DC: erro {result}");
                
                // 2. Disco rígido - Never (0)
                Guid diskSubGroup = new Guid("0012ee47-9041-4b5d-9b77-535fba8b1442");
                Guid diskSetting = new Guid("6738e2c4-e8a5-4a42-b16a-e040e769756e");
                result = PowerWriteACValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref diskSubGroup, ref diskSetting, 0);
                if (result != 0) return (false, $"Falha ao definir Disk AC: erro {result}");
                result = PowerWriteDCValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref diskSubGroup, ref diskSetting, 0);
                if (result != 0) return (false, $"Falha ao definir Disk DC: erro {result}");
                
                // 3. Monitor - Never (0) AC, 3 minutos (180) DC
                Guid monitorSubGroup = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
                Guid monitorSetting = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
                result = PowerWriteACValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref monitorSubGroup, ref monitorSetting, 0);
                if (result != 0) return (false, $"Falha ao definir Monitor AC: erro {result}");
                result = PowerWriteDCValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref monitorSubGroup, ref monitorSetting, 180);
                if (result != 0) return (false, $"Falha ao definir Monitor DC: erro {result}");
                
                // 4. Suspender - Never (0) AC, 10 minutos (600) DC
                Guid sleepSubGroup = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");
                Guid sleepSetting = new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
                result = PowerWriteACValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref sleepSubGroup, ref sleepSetting, 0);
                if (result != 0) return (false, $"Falha ao definir Sleep AC: erro {result}");
                result = PowerWriteDCValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref sleepSubGroup, ref sleepSetting, 600);
                if (result != 0) return (false, $"Falha ao definir Sleep DC: erro {result}");
                
                // 5. Hibernar - Never (0)
                Guid hibernateSetting = new Guid("9d7815a6-7ee4-497e-8888-515a05f02364");
                result = PowerWriteACValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref sleepSubGroup, ref hibernateSetting, 0);
                if (result != 0) return (false, $"Falha ao definir Hibernate AC: erro {result}");
                result = PowerWriteDCValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref sleepSubGroup, ref hibernateSetting, 0);
                if (result != 0) return (false, $"Falha ao definir Hibernate DC: erro {result}");
                
                // 6. Processador - Estado de desempenho mínimo 100%
                Guid processorSubGroup = new Guid("54533251-82be-4824-96c1-47b60b740d00");
                Guid procMinSetting = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c");
                result = PowerWriteACValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref processorSubGroup, ref procMinSetting, 100);
                if (result != 0) return (false, $"Falha ao definir Processor Min AC: erro {result}");
                result = PowerWriteDCValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref processorSubGroup, ref procMinSetting, 100);
                if (result != 0) return (false, $"Falha ao definir Processor Min DC: erro {result}");
                
                // 7. Processador - Estado de desempenho máximo 100%
                Guid procMaxSetting = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec");
                result = PowerWriteACValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref processorSubGroup, ref procMaxSetting, 100);
                if (result != 0) return (false, $"Falha ao definir Processor Max AC: erro {result}");
                result = PowerWriteDCValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref processorSubGroup, ref procMaxSetting, 100);
                if (result != 0) return (false, $"Falha ao definir Processor Max DC: erro {result}");
                
                // 8. Brilho adaptável - Desligado (0)
                Guid adaptBrightSetting = new Guid("fbd9aa66-9553-4097-ba44-ed6e9d65eab8");
                result = PowerWriteACValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref monitorSubGroup, ref adaptBrightSetting, 0);
                if (result != 0) return (false, $"Falha ao definir Adaptive Brightness AC: erro {result}");
                result = PowerWriteDCValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref monitorSubGroup, ref adaptBrightSetting, 0);
                if (result != 0) return (false, $"Falha ao definir Adaptive Brightness DC: erro {result}");
                
                // Aplica todas as alterações chamando PowerSetActiveScheme
                result = PowerSetActiveScheme(IntPtr.Zero, activeSchemePtr);
                if (result != 0)
                {
                    return (false, $"Falha ao aplicar power scheme: erro {result}");
                }
                
                Logger.Log("SLIDE: Bitsum Highest Performance settings aplicados via Power Management API");
                return (true, "Configurações do Bitsum Highest Performance aplicadas com sucesso.");
            }
            catch (Exception ex)
            {
                Logger.LogError("ApplyBitsumHighestPerformanceSettings", ex.Message);
                return (false, $"Falha ao aplicar configurações Bitsum: {ex.Message}");
            }
        }

        public static (bool Success, string Message) ApplyUltimatePerformanceSettings()
        {
            try
            {
                // Aplica configurações combinadas do Bitsum Highest Performance + Driver Booster
                // Plano "Ultimate Performance" - Combina o melhor dos dois
                // Bitsum: Mais equilibrado para DC (bateria)
                // Driver Booster: Mesmas configurações principais
                
                // Obtém GUID do power scheme ativo via API nativa
                IntPtr activeSchemePtr = IntPtr.Zero;
                uint result = PowerGetActiveScheme(IntPtr.Zero, ref activeSchemePtr);
                if (result != 0 || activeSchemePtr == IntPtr.Zero)
                {
                    return (false, "Não foi possível obter o GUID do power scheme ativo.");
                }
                
                Guid activeSchemeGuid = Marshal.PtrToStructure<Guid>(activeSchemePtr);
                
                // 1. PCIe Express ASPM - Desligado (0) - AMBOS
                Guid pcieSubGroup = new Guid("501a4d13-42af-4429-9fd1-a8218c268e20");
                Guid pcieSetting = new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5");
                result = PowerWriteACValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref pcieSubGroup, ref pcieSetting, 0);
                if (result != 0) return (false, $"Falha ao definir PCIe ASPM AC: erro {result}");
                result = PowerWriteDCValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref pcieSubGroup, ref pcieSetting, 0);
                if (result != 0) return (false, $"Falha ao definir PCIe ASPM DC: erro {result}");
                
                // 2. Disco rígido - Never (0) - AMBOS
                Guid diskSubGroup = new Guid("0012ee47-9041-4b5d-9b77-535fba8b1442");
                Guid diskSetting = new Guid("6738e2c4-e8a5-4a42-b16a-e040e769756e");
                result = PowerWriteACValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref diskSubGroup, ref diskSetting, 0);
                if (result != 0) return (false, $"Falha ao definir Disk AC: erro {result}");
                result = PowerWriteDCValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref diskSubGroup, ref diskSetting, 0);
                if (result != 0) return (false, $"Falha ao definir Disk DC: erro {result}");
                
                // 3. Monitor - Never (0) AC, 5 minutos (300) DC - EQUILIBRADO (meio termo)
                Guid monitorSubGroup = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
                Guid monitorSetting = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
                result = PowerWriteACValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref monitorSubGroup, ref monitorSetting, 0);
                if (result != 0) return (false, $"Falha ao definir Monitor AC: erro {result}");
                result = PowerWriteDCValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref monitorSubGroup, ref monitorSetting, 300);
                if (result != 0) return (false, $"Falha ao definir Monitor DC: erro {result}");
                
                // 4. Suspender - Never (0) AC, 15 minutos (900) DC - EQUILIBRADO (meio termo)
                Guid sleepSubGroup = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");
                Guid sleepSetting = new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
                result = PowerWriteACValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref sleepSubGroup, ref sleepSetting, 0);
                if (result != 0) return (false, $"Falha ao definir Sleep AC: erro {result}");
                result = PowerWriteDCValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref sleepSubGroup, ref sleepSetting, 900);
                if (result != 0) return (false, $"Falha ao definir Sleep DC: erro {result}");
                
                // 5. Hibernar - Never (0) - AMBOS
                Guid hibernateSetting = new Guid("9d7815a6-7ee4-497e-8888-515a05f02364");
                result = PowerWriteACValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref sleepSubGroup, ref hibernateSetting, 0);
                if (result != 0) return (false, $"Falha ao definir Hibernate AC: erro {result}");
                result = PowerWriteDCValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref sleepSubGroup, ref hibernateSetting, 0);
                if (result != 0) return (false, $"Falha ao definir Hibernate DC: erro {result}");
                
                // 6. Processador - Estado de desempenho mínimo 100% - AMBOS
                Guid processorSubGroup = new Guid("54533251-82be-4824-96c1-47b60b740d00");
                Guid procMinSetting = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c");
                result = PowerWriteACValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref processorSubGroup, ref procMinSetting, 100);
                if (result != 0) return (false, $"Falha ao definir Processor Min AC: erro {result}");
                result = PowerWriteDCValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref processorSubGroup, ref procMinSetting, 100);
                if (result != 0) return (false, $"Falha ao definir Processor Min DC: erro {result}");
                
                // 7. Processador - Estado de desempenho máximo 100% - AMBOS
                Guid procMaxSetting = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec");
                result = PowerWriteACValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref processorSubGroup, ref procMaxSetting, 100);
                if (result != 0) return (false, $"Falha ao definir Processor Max AC: erro {result}");
                result = PowerWriteDCValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref processorSubGroup, ref procMaxSetting, 100);
                if (result != 0) return (false, $"Falha ao definir Processor Max DC: erro {result}");
                
                // 8. Brilho adaptável - Desligado (0) - AMBOS
                Guid adaptBrightSetting = new Guid("fbd9aa66-9553-4097-ba44-ed6e9d65eab8");
                result = PowerWriteACValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref monitorSubGroup, ref adaptBrightSetting, 0);
                if (result != 0) return (false, $"Falha ao definir Adaptive Brightness AC: erro {result}");
                result = PowerWriteDCValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref monitorSubGroup, ref adaptBrightSetting, 0);
                if (result != 0) return (false, $"Falha ao definir Adaptive Brightness DC: erro {result}");
                
                // 9. Limite de rebaixamento por ociosidade - 40% (0x28) - AMBOS
                Guid idleDemoteSetting = new Guid("4b92d758-5a24-4851-a470-815d78aee119");
                result = PowerWriteACValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref processorSubGroup, ref idleDemoteSetting, 40);
                if (result != 0) return (false, $"Falha ao definir Idle Demote AC: erro {result}");
                result = PowerWriteDCValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref processorSubGroup, ref idleDemoteSetting, 40);
                if (result != 0) return (false, $"Falha ao definir Idle Demote DC: erro {result}");
                
                // 10. Limite de promoção por ociosidade - 60% (0x3c) - AMBOS
                Guid idlePromoteSetting = new Guid("7b224883-b3cc-4d79-819f-8374152cbe7c");
                result = PowerWriteACValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref processorSubGroup, ref idlePromoteSetting, 60);
                if (result != 0) return (false, $"Falha ao definir Idle Promote AC: erro {result}");
                result = PowerWriteDCValueIndex(IntPtr.Zero, ref activeSchemeGuid, ref processorSubGroup, ref idlePromoteSetting, 60);
                if (result != 0) return (false, $"Falha ao definir Idle Promote DC: erro {result}");
                
                // Aplica todas as alterações chamando PowerSetActiveScheme
                result = PowerSetActiveScheme(IntPtr.Zero, activeSchemePtr);
                if (result != 0)
                {
                    return (false, $"Falha ao aplicar power scheme: erro {result}");
                }
                
                Logger.Log("SLIDE: Ultimate Performance settings aplicados via Power Management API (Bitsum + Driver Booster)");
                return (true, "Configurações Ultimate Performance aplicadas (combinação Bitsum + Driver Booster).");
            }
            catch (Exception ex)
            {
                Logger.LogError("ApplyUltimatePerformanceSettings", ex.Message);
                return (false, $"Falha ao aplicar configurações Ultimate: {ex.Message}");
            }
        }

        public static bool IsGamingLatencyOptimized()
        {
            try
            {
                using var prioKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\PriorityControl", false);
                if (prioKey != null)
                {
                    var val = prioKey.GetValue("Win32PrioritySeparation");
                    return val != null && Convert.ToInt32(val) == 38;
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return false;
        }

        public static (bool Success, string Message) RevertGamingLatency()
        {
            try
            {
                using var prioKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\PriorityControl", true);
                prioKey?.SetValue("Win32PrioritySeparation", 2, RegistryValueKind.DWord); // 2 is default

                using var sysProfKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", true);
                if (sysProfKey != null)
                {
                    sysProfKey.SetValue("NetworkThrottlingIndex", 10, RegistryValueKind.DWord);
                    sysProfKey.SetValue("SystemResponsiveness", 20, RegistryValueKind.DWord);
                }

                using var gamesProfKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", true);
                if (gamesProfKey != null)
                {
                    gamesProfKey.SetValue("GPU Priority", 8, RegistryValueKind.DWord);
                    gamesProfKey.SetValue("Priority", 2, RegistryValueKind.DWord);
                    gamesProfKey.SetValue("Scheduling Category", "Medium", RegistryValueKind.String);
                    gamesProfKey.SetValue("SFIO Priority", "Normal", RegistryValueKind.String);
                }

                using var dwmKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM", true);
                if (dwmKey != null)
                {
                    dwmKey.SetValue("Animations", 1, RegistryValueKind.DWord);
                    dwmKey.SetValue("EnableAeroPeek", 1, RegistryValueKind.DWord);
                    dwmKey.DeleteValue("OverlayTestMode", false);
                }

                using var dvrCUKey = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore", true);
                dvrCUKey?.SetValue("GameDVR_Enabled", 1, RegistryValueKind.DWord);

                using var appCapKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", true);
                appCapKey?.SetValue("AppCaptureEnabled", 1, RegistryValueKind.DWord);

                Logger.Log("SLIDE: Latência de jogo e rede revertidos aos padrões.");
                return (true, "Valores de rede, CPU e DWM restaurados (GameDVR ativado).");
            }
            catch (Exception ex)
            {
                Logger.LogError("RevertGamingLatency", ex.Message);
                return (false, "Falha ao reverter parâmetros de latência de jogo.");
            }
        }

        #region Gaming Latency Profile - Khorvie Style Optimizations

        public static int GetWin32PrioritySeparation()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 2);
                return value is int intVal ? intVal : 2;
            }
            catch (Exception ex)
            {
                Logger.LogError("GetWin32PrioritySeparation", ex.Message);
                return 2;
            }
        }

        public static (bool Success, string Message) SetWin32PrioritySeparation(int value)
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", value, RegistryValueKind.DWord);
                string hexValue = $"0x{value:X2}";
                Logger.Log($"Gaming Latency: Win32PrioritySeparation definido para {hexValue} ({value})");
                return (true, $"Win32PrioritySeparation definido para {hexValue}. Reinicie para aplicar.");
            }
            catch (Exception ex)
            {
                Logger.LogError("SetWin32PrioritySeparation", ex.Message);
                return (false, $"Falha ao definir Win32PrioritySeparation: {ex.Message}");
            }
        }

        public static (bool Success, string Message) DisableCoreParking()
        {
            try
            {
                string[] subKeys = {
                    @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c8-3b32988b1dd4\0cc5b647-c1df-4637-891a-dec35c318583",
                    @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c8-3b32988b1dd4\ea4be0c1-7c65-46f8-8c17-f298766665d9"
                };

                foreach (var key in subKeys)
                {
                    Registry.SetValue($@"HKEY_LOCAL_MACHINE\{key}", "ValueMax", 0, RegistryValueKind.DWord);
                    Registry.SetValue($@"HKEY_LOCAL_MACHINE\{key}", "ValueMin", 0, RegistryValueKind.DWord);
                }

                Logger.Log("Gaming Latency: Core Parking desativado (ValueMax=0, ValueMin=0)");
                return (true, "Core Parking desativado. Todos os cores permanecem ativos.");
            }
            catch (Exception ex)
            {
                Logger.LogError("DisableCoreParking", ex.Message);
                return (false, $"Falha ao desativar Core Parking: {ex.Message}");
            }
        }

        /// <summary>
        /// Unpark CPU usando powercfg (similar ao PowerSettingsExplorer)
        /// Desmarca as caixinhas de Processor idle demote/promote threshold
        /// GUIDs:
        /// - Processor idle demote threshold: 4b92d758-5a24-4851-a470-815d78aee119
        /// - Processor idle promote threshold: 7b224883-b3cc-4d79-819f-8374152cbe7c
        /// Subgroup: 54533251-82be-4824-96c1-47b60b740d00 (Processor power management)
        /// </summary>
        public static (bool Success, string Message) UnparkCpuPowerConfig()
        {
            try
            {
                // 1. Obter o GUID do power scheme atual (mais direto e confiável)
                var psi = new ProcessStartInfo
                {
                    FileName = "powercfg.exe",
                    Arguments = "/getactivescheme",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var processGuid = Process.Start(psi);
                string guidOutput = processGuid?.StandardOutput.ReadToEnd() ?? "";
                processGuid?.WaitForExit();

                // Extrair GUID - formato: "GUID do Esquema de Energia: 8ba1a16c-9e24-4aef-9439-8a9f2a79069f  (Nome)"
                string? activeSchemeGuid = null;
                var lines = guidOutput.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains(":"))
                    {
                        // Pega a parte depois dos dois pontos
                        var parts = line.Split(':');
                        if (parts.Length > 1)
                        {
                            var guidPart = parts[1].Trim();
                            // Remove o nome entre parênteses se existir
                            var parenIndex = guidPart.IndexOf('(');
                            if (parenIndex > 0)
                            {
                                guidPart = guidPart.Substring(0, parenIndex).Trim();
                            }
                            // Verifica se parece um GUID (8-4-4-4-12)
                            if (System.Text.RegularExpressions.Regex.IsMatch(guidPart, @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"))
                            {
                                activeSchemeGuid = guidPart;
                                break;
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(activeSchemeGuid))
                {
                    Logger.Log($"UnparkCpuPowerConfig: Saída do powercfg: {guidOutput}");
                    return (false, "Não foi possível obter o GUID do power scheme ativo.");
                }

                Logger.Log($"UnparkCpuPowerConfig: GUID encontrado: {activeSchemeGuid}");

                // 2. Mostrar as configurações ocultas (unhide)
                psi.Arguments = "-attributes SUB_PROCESSOR 4b92d758-5a24-4851-a470-815d78aee119 -ATTRIB_HIDE";
                using var process1 = Process.Start(psi);
                process1?.WaitForExit();

                psi.Arguments = "-attributes SUB_PROCESSOR 7b224883-b3cc-4d79-819f-8374152cbe7c -ATTRIB_HIDE";
                using var process2 = Process.Start(psi);
                process2?.WaitForExit();

                // 3. Remover valores personalizados das configurações de idle threshold
                // Isso desmarca as caixinhas (volta ao padrão)
                psi.Arguments = $"/deletesetting {activeSchemeGuid} SUB_PROCESSOR 4b92d758-5a24-4851-a470-815d78aee119";
                using var process4 = Process.Start(psi);
                process4?.WaitForExit();

                psi.Arguments = $"/deletesetting {activeSchemeGuid} SUB_PROCESSOR 7b224883-b3cc-4d79-819f-8374152cbe7c";
                using var process5 = Process.Start(psi);
                process5?.WaitForExit();

                Logger.Log("Gaming Latency: CPU unparked via powercfg (idle thresholds removidos)");
                return (true, "CPU unparked via powercfg. Processor idle thresholds desmarcados para menor latência.");
            }
            catch (Exception ex)
            {
                Logger.LogError("UnparkCpuPowerConfig", ex.Message);
                return (false, $"Falha ao unpark CPU via powercfg: {ex.Message}");
            }
        }

        /// <summary>
        /// Reverte as alterações de unpark CPU via powercfg
        /// Esconde novamente as configurações e restaura valores padrão
        /// </summary>
        public static (bool Success, string Message) RevertUnparkCpuPowerConfig()
        {
            try
            {
                // 1. Esconder as configurações (hide novamente)
                var psi = new ProcessStartInfo
                {
                    FileName = "powercfg.exe",
                    Arguments = "-attributes SUB_PROCESSOR 4b92d758-5a24-4851-a470-815d78aee119 +ATTRIB_HIDE",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process1 = Process.Start(psi);
                if (process1 != null)
                {
                    process1.StandardOutput.ReadToEnd();
                    process1.StandardError.ReadToEnd();
                    process1.WaitForExit();
                }

                psi.Arguments = "-attributes SUB_PROCESSOR 7b224883-b3cc-4d79-819f-8374152cbe7c +ATTRIB_HIDE";
                using var process2 = Process.Start(psi);
                if (process2 != null)
                {
                    process2.StandardOutput.ReadToEnd();
                    process2.StandardError.ReadToEnd();
                    process2.WaitForExit();
                }

                Logger.Log("Gaming Latency: CPU unpark revertido (idle thresholds ocultados novamente)");
                return (true, "CPU unpark revertido. Processor idle thresholds restaurados para padrão.");
            }
            catch (Exception ex)
            {
                Logger.LogError("RevertUnparkCpuPowerConfig", ex.Message);
                return (false, $"Falha ao reverter unpark CPU: {ex.Message}");
            }
        }

        public static (bool Success, string Message) DisableTimerCoalescing()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "CoalescingTimerInterval", 0, RegistryValueKind.DWord);
                Logger.Log("Gaming Latency: Timer Coalescing desativado (CoalescingTimerInterval=0)");
                return (true, "Timer Coalescing desativado. Timers de alta precisão ativados.");
            }
            catch (Exception ex)
            {
                Logger.LogError("DisableTimerCoalescing", ex.Message);
                return (false, $"Falha ao desativar Timer Coalescing: {ex.Message}");
            }
        }

        public static (bool Success, string Message) OptimizeInputQueue()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", "KeyboardDataQueueSize", 30, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\mouclass\Parameters", "MouseDataQueueSize", 30, RegistryValueKind.DWord);
                Logger.Log("Gaming Latency: Input Queue otimizado (Keyboard=30, Mouse=30)");
                return (true, "Input Queue otimizado. Buffer de mouse/teclado definido para 30.");
            }
            catch (Exception ex)
            {
                Logger.LogError("OptimizeInputQueue", ex.Message);
                return (false, $"Falha ao otimizar Input Queue: {ex.Message}");
            }
        }

        public static (bool Success, string Message) EnableGlobalTimerResolution()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "GlobalTimerResolutionRequests", 1, RegistryValueKind.DWord);
                Logger.Log("Gaming Latency: Global Timer Resolution ativado (GlobalTimerResolutionRequests=1)");
                return (true, "Global Timer Resolution ativado. Apps podem solicitar timers de 1ms.");
            }
            catch (Exception ex)
            {
                Logger.LogError("EnableGlobalTimerResolution", ex.Message);
                return (false, $"Falha ao ativar Global Timer Resolution: {ex.Message}");
            }
        }

        public static (bool Success, string Message) SetSystemResponsivenessGaming()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 0, RegistryValueKind.DWord);
                Logger.Log("Gaming Latency: SystemResponsiveness definido para 0 (modo gaming)");
                return (true, "SystemResponsiveness definido para 0. Máxima performance para jogos.");
            }
            catch (Exception ex)
            {
                Logger.LogError("SetSystemResponsivenessGaming", ex.Message);
                return (false, $"Falha ao definir SystemResponsiveness: {ex.Message}");
            }
        }

        public static (bool Success, string Message, List<string> Applied) ApplyFullGamingLatencyProfile(int win32PriorityValue = 0x26)
        {
            var applied = new List<string>();
            var errors = new List<string>();

            var win32Result = SetWin32PrioritySeparation(win32PriorityValue);
            if (win32Result.Success) applied.Add($"Win32PrioritySeparation=0x{win32PriorityValue:X2}");
            else errors.Add($"Win32PrioritySeparation: {win32Result.Message}");

            var coreParkingResult = DisableCoreParking();
            if (coreParkingResult.Success) applied.Add("CoreParking");
            else errors.Add($"CoreParking: {coreParkingResult.Message}");

            var timerResult = DisableTimerCoalescing();
            if (timerResult.Success) applied.Add("TimerCoalescing");
            else errors.Add($"TimerCoalescing: {timerResult.Message}");

            var inputResult = OptimizeInputQueue();
            if (inputResult.Success) applied.Add("InputQueue");
            else errors.Add($"InputQueue: {inputResult.Message}");

            var globalTimerResult = EnableGlobalTimerResolution();
            if (globalTimerResult.Success) applied.Add("GlobalTimerResolution");
            else errors.Add($"GlobalTimerResolution: {globalTimerResult.Message}");

            var sysRespResult = SetSystemResponsivenessGaming();
            if (sysRespResult.Success) applied.Add("SystemResponsiveness");
            else errors.Add($"SystemResponsiveness: {sysRespResult.Message}");

            Logger.Log($"Gaming Latency Profile aplicado. Itens: {applied.Count}, Erros: {errors.Count}");

            if (applied.Count > 0)
            {
                string msg = errors.Count > 0 
                    ? $"Profile aplicado parcialmente ({applied.Count}/{applied.Count + errors.Count}). Alguns erros: {string.Join(", ", errors.Take(2))}"
                    : "Gaming Latency Profile aplicado com sucesso! Reinicie para todos os efeitos.";
                return (true, msg, applied);
            }
            else
            {
                return (false, "Falha ao aplicar Gaming Latency Profile. Verifique permissões de administrador.", applied);
            }
        }

        public static (bool Success, string Message) RevertGamingLatencyProfile()
        {
            try
            {
                using var prioKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\PriorityControl", true);
                prioKey?.SetValue("Win32PrioritySeparation", 2, RegistryValueKind.DWord);

                using var sysProfKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", true);
                if (sysProfKey != null)
                    sysProfKey.SetValue("SystemResponsiveness", 20, RegistryValueKind.DWord);

                using var kernelKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", true);
                if (kernelKey != null)
                    kernelKey.DeleteValue("CoalescingTimerInterval", false);

                using var powerKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power", true);
                if (powerKey != null)
                    powerKey.DeleteValue("GlobalTimerResolutionRequests", false);

                // Reverte Core Parking para valores padrão
                string[] coreParkingKeys = {
                    @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c8-3b32988b1dd4\0cc5b647-c1df-4637-891a-dec35c318583",
                    @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c8-3b32988b1dd4\ea4be0c1-7c65-46f8-8c17-f298766665d9"
                };
                foreach (var key in coreParkingKeys)
                {
                    using var rk = Registry.LocalMachine.OpenSubKey(key, true);
                    rk?.DeleteValue("ValueMax", false);
                    rk?.DeleteValue("ValueMin", false);
                }

                // Reverte Input Queue para valores padrão
                using var kbdKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", true);
                kbdKey?.DeleteValue("KeyboardDataQueueSize", false);

                using var mouKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\mouclass\Parameters", true);
                mouKey?.DeleteValue("MouseDataQueueSize", false);

                Logger.Log("Gaming Latency Profile revertido para padrões Windows");
                return (true, "Gaming Latency Profile revertido. Configurações restauradas para padrão Windows.");
            }
            catch (Exception ex)
            {
                Logger.LogError("RevertGamingLatencyProfile", ex.Message);
                return (false, $"Falha ao reverter: {ex.Message}");
            }
        }

        public static Dictionary<string, bool> CheckGamingLatencyStatus()
        {

            // Típico: 5-10 status de latência
            var status = new Dictionary<string, bool>(10)
            {
                ["Win32PrioritySeparation"] = false,
                ["CoreParking"] = false,
                ["TimerCoalescing"] = false,
                ["InputQueue"] = false,
                ["GlobalTimerResolution"] = false,
                ["SystemResponsiveness"] = false,
            };

            try
            {
                int win32Value = GetWin32PrioritySeparation();
                status["Win32PrioritySeparation"] = win32Value != 2;

                var coreParkingValue = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c8-3b32988b1dd4\0cc5b647-c1df-4637-891a-dec35c318583", "ValueMax", 64);
                status["CoreParking"] = coreParkingValue is int cpVal && cpVal == 0;

                var timerValue = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "CoalescingTimerInterval", null);
                status["TimerCoalescing"] = timerValue is int tVal && tVal == 0;

                var kbdValue = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", "KeyboardDataQueueSize", 100);
                var mouseValue = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\mouclass\Parameters", "MouseDataQueueSize", 100);
                status["InputQueue"] = kbdValue is int kVal && kVal == 30 && mouseValue is int mVal && mVal == 30;

                var globalTimerValue = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "GlobalTimerResolutionRequests", 0);
                status["GlobalTimerResolution"] = globalTimerValue is int gtVal && gtVal == 1;

                var sysRespValue = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 20);
                status["SystemResponsiveness"] = sysRespValue is int srVal && srVal == 0;
            }
            catch (Exception ex)
            {
                Logger.LogError("CheckGamingLatencyStatus", ex.Message);
            }

            return status;
        }

        #endregion

        #region GDI Scaling Control

        public static (bool Success, string Message) DisableGdiScaling()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "DisableGdiScaling", 1, RegistryValueKind.DWord);
                Logger.Log("GDI Scaling desativado (DisableGdiScaling=1)");
                return (true, "GDI Scaling desativado. Aplicativos legados não terão scaling automático.");
            }
            catch (Exception ex)
            {
                Logger.LogError("DisableGdiScaling", ex.Message);
                return (false, $"Falha ao desativar GDI Scaling: {ex.Message}");
            }
        }

        public static (bool Success, string Message) EnableGdiScaling()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true);
                if (key != null)
                {
                    key.DeleteValue("DisableGdiScaling", false);
                }
                Logger.Log("GDI Scaling restaurado para padrão");
                return (true, "GDI Scaling restaurado para o padrão do Windows.");
            }
            catch (Exception ex)
            {
                Logger.LogError("EnableGdiScaling", ex.Message);
                return (false, $"Falha ao restaurar GDI Scaling: {ex.Message}");
            }
        }

        public static bool IsGdiScalingDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "DisableGdiScaling", 0);
                return value is int intVal && intVal == 1;
            }
            catch (Exception ex)
            {
                Logger.LogError("IsGdiScalingDisabled", ex.Message);
                return false;
            }
        }

        #endregion

        #region Windows 11 Additional Tweaks

        public static (bool Success, string Message) DisablePowerThrottling()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1, RegistryValueKind.DWord);
                Logger.Log("Power Throttling desativado (PowerThrottlingOff=1)");
                return (true, "Power Throttling desativado. CPU rodará em performance máxima.");
            }
            catch (Exception ex)
            {
                Logger.LogError("DisablePowerThrottling", ex.Message);
                return (false, $"Falha ao desativar Power Throttling: {ex.Message}");
            }
        }

        public static (bool Success, string Message) EnablePowerThrottling()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", true);
                key?.DeleteValue("PowerThrottlingOff", false);
                Logger.Log("Power Throttling restaurado para padrão");
                return (true, "Power Throttling restaurado para padrão Windows.");
            }
            catch (Exception ex)
            {
                Logger.LogError("EnablePowerThrottling", ex.Message);
                return (false, $"Falha ao restaurar Power Throttling: {ex.Message}");
            }
        }

        public static bool IsPowerThrottlingDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 0);
                return value is int intVal && intVal == 1;
            }
            catch (Exception ex)
            {
                Logger.LogError("IsPowerThrottlingDisabled", ex.Message);
                return false;
            }
        }

        public static (bool Success, string Message) OptimizeGamingProfileAdvanced()
        {
            try
            {
                string gamesPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
                Registry.SetValue($@"HKEY_LOCAL_MACHINE\{gamesPath}", "GPU Priority", 8, RegistryValueKind.DWord);
                Registry.SetValue($@"HKEY_LOCAL_MACHINE\{gamesPath}", "Affinity", 0xF, RegistryValueKind.DWord);
                Registry.SetValue($@"HKEY_LOCAL_MACHINE\{gamesPath}", "Background Only", "False", RegistryValueKind.String);
                Registry.SetValue($@"HKEY_LOCAL_MACHINE\{gamesPath}", "Background Priority", 1, RegistryValueKind.DWord);
                Registry.SetValue($@"HKEY_LOCAL_MACHINE\{gamesPath}", "Priority", 6, RegistryValueKind.DWord);
                Registry.SetValue($@"HKEY_LOCAL_MACHINE\{gamesPath}", "Scheduling Category", "High", RegistryValueKind.String);
                Registry.SetValue($@"HKEY_LOCAL_MACHINE\{gamesPath}", "SFIO Priority", "High", RegistryValueKind.String);
                
                Logger.Log("Gaming Profile avançado aplicado");
                return (true, "Gaming Profile avançado aplicado. Jogos terão prioridade máxima.");
            }
            catch (Exception ex)
            {
                Logger.LogError("OptimizeGamingProfileAdvanced", ex.Message);
                return (false, $"Falha ao aplicar Gaming Profile: {ex.Message}");
            }
        }

        public static bool IsGamingProfileAdvancedApplied()
        {
            try
            {
                var gpuPriority = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority", 0);
                var priority = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Priority", 0);
                return gpuPriority is int gpVal && gpVal == 8 && priority is int pVal && pVal == 6;
            }
            catch (Exception ex)
            {
                Logger.LogError("IsGamingProfileAdvancedApplied", ex.Message);
                return false;
            }
        }

        #endregion

        #region Tweaks Diversos

        public static bool IsNDUDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Ndu", "Start", 2);
                return value is int intVal && intVal == 4;
            }
            catch (Exception ex)
            {
                Logger.LogError("IsNDUDisabled", ex.Message);
                return false;
            }
        }

        public static void DisableNDU()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Ndu", "Start", 4, RegistryValueKind.DWord);
                Logger.Log("Serviço NDU desabilitado (fix memory leak)");
            }
            catch (Exception ex)
            {
                Logger.LogError("DisableNDU", ex.Message);
            }
        }

        public static void EnableNDU()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Ndu", "Start", 2, RegistryValueKind.DWord);
                Logger.Log("Serviço NDU habilitado");
            }
            catch (Exception ex)
            {
                Logger.LogError("EnableNDU", ex.Message);
            }
        }

        private static bool RunScCommand(string arguments)
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            p.Start();
            string _ = p.StandardOutput.ReadToEnd();
            string __ = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return p.ExitCode == 0;
        }

        /// <summary>
        /// Serviços de diagnóstico: DPS (Diagnostic Policy Service), 
        /// WdiServiceHost (Windows Diagnostic Service Host), 
        /// WdiSystemHost (Windows Diagnostic System Host).
        /// Desabilitar reduz background activity em ~10 MB e remove o 
        /// ícone de "sem internet" da bandeja quando há captive portal.
        /// </summary>
        public static bool IsDiagnosticServicesDisabled()
        {
            try
            {
                var dps = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\DPS", "Start", 2);
                var wdiHost = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\WdiServiceHost", "Start", 3);
                var wdiSys = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\WdiSystemHost", "Start", 3);
                return dps is int d && d == 4 &&
                       wdiHost is int wh && wh == 4 &&
                       wdiSys is int ws && ws == 4;
            }
            catch (Exception ex)
            {
                Logger.LogError("IsDiagnosticServicesDisabled", ex.Message);
                return false;
            }
        }

        public static bool DisableDiagnosticServices()
        {
            try
            {
                foreach (var name in new[] { "DPS", "WdiServiceHost", "WdiSystemHost" })
                {
                    RunScCommand($"config {name} start= disabled");
                    RunScCommand($"stop {name}");
                }

                Logger.Log("Serviços de diagnóstico desabilitados (DPS, WdiServiceHost, WdiSystemHost)");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("DisableDiagnosticServices", ex.Message);
                return false;
            }
        }

        public static bool EnableDiagnosticServices()
        {
            try
            {
                RunScCommand("config DPS start= auto");
                RunScCommand("config WdiServiceHost start= demand");
                RunScCommand("config WdiSystemHost start= demand");

                Logger.Log("Serviços de diagnóstico habilitados (DPS=Auto, WdiServiceHost=Demand, WdiSystemHost=Demand)");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("EnableDiagnosticServices", ex.Message);
                return false;
            }
        }

        private static readonly string[] _manualStartupServices = new string[]
        {
            "SysMain", "DiagTrack", "MapsBroker", "lfsvc", "SharedAccess",
            "lltdsvc", "CDPUserSvc", "WpnService", "WpnUserService", "OneSyncSvc",
            "PcaSvc", "WerSvc", "wisvc", "icssvc"
        };

        public static bool IsBackgroundAppsDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground", 0);
                return value is int intVal && intVal == 2;
            }
            catch (Exception ex)
            {
                Logger.LogError("IsBackgroundAppsDisabled", ex.Message);
                return false;
            }
        }

        public static void DisableBackgroundApps()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground", 2, RegistryValueKind.DWord);
                Logger.Log("Apps em segundo plano desabilitados via GPEDIT");
            }
            catch (Exception ex)
            {
                Logger.LogError("DisableBackgroundApps", ex.Message);
            }
        }

        public static void EnableBackgroundApps()
        {
            try
            {
                // Definir LetAppsRunInBackground = 0 (Allow)
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground", 0, RegistryValueKind.DWord);
                Logger.Log("Apps em segundo plano habilitados via GPEDIT");
            }
            catch (Exception ex)
            {
                Logger.LogError("EnableBackgroundApps", ex.Message);
            }
        }

        public static bool IsServiceStartupOptimized()
        {
            try
            {
                // Verificar se pelo menos alguns serviços estão em Manual (3)
                int manualCount = 0;
                foreach (var service in _manualStartupServices)
                {
                    var value = Registry.GetValue($@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{service}", "Start", 2);
                    if (value is int intVal && intVal == 3)
                    {
                        manualCount++;
                    }
                }
                // Considerar otimizado se pelo menos 50% dos serviços estão em Manual
                return manualCount >= _manualStartupServices.Length / 2;
            }
            catch (Exception ex)
            {
                Logger.LogError("IsServiceStartupOptimized", ex.Message);
                return false;
            }
        }

        public static void OptimizeServiceStartup()
        {
            try
            {
                int modifiedCount = 0;
                foreach (var service in _manualStartupServices)
                {
                    try
                    {
                        var value = Registry.GetValue($@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{service}", "Start", 2);
                        // Se estiver em Automatic (2), mudar para Manual (3)
                        if (value is int intVal && intVal == 2)
                        {
                            Registry.SetValue($@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{service}", "Start", 3, RegistryValueKind.DWord);
                            modifiedCount++;
                        }
                    }
                    catch
                    {
                        // Ignora serviços que não existem ou não podem ser modificados
                    }
                }
                Logger.Log($"Startup de serviços otimizado: {modifiedCount} serviços definidos para Manual");
            }
            catch (Exception ex)
            {
                Logger.LogError("OptimizeServiceStartup", ex.Message);
            }
        }

        public static void RevertServiceStartup()
        {
            try
            {
                int modifiedCount = 0;
                foreach (var service in _manualStartupServices)
                {
                    try
                    {
                        var value = Registry.GetValue($@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{service}", "Start", 3);
                        // Se estiver em Manual (3), mudar para Automatic (2)
                        if (value is int intVal && intVal == 3)
                        {
                            Registry.SetValue($@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{service}", "Start", 2, RegistryValueKind.DWord);
                            modifiedCount++;
                        }
                    }
                    catch
                    {
                        // Ignora serviços que não existem ou não podem ser modificados
                    }
                }
                Logger.Log($"Startup de serviços revertido: {modifiedCount} serviços definidos para Automatic");
            }
            catch (Exception ex)
            {
                Logger.LogError("RevertServiceStartup", ex.Message);
            }
        }

        public static bool IsNoAutoRebootEnabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 0);
                return value is int intVal && intVal == 1;
            }
            catch (Exception ex)
            {
                Logger.LogError("IsNoAutoRebootEnabled", ex.Message);
                return false;
            }
        }

        public static void EnableNoAutoReboot()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 1, RegistryValueKind.DWord);
                Logger.Log("NoAutoRebootWithLoggedOnUsers habilitado");
            }
            catch (Exception ex)
            {
                Logger.LogError("EnableNoAutoReboot", ex.Message);
            }
        }

        public static void DisableNoAutoReboot()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 0, RegistryValueKind.DWord);
                Logger.Log("NoAutoRebootWithLoggedOnUsers desabilitado");
            }
            catch (Exception ex)
            {
                Logger.LogError("DisableNoAutoReboot", ex.Message);
            }
        }

        #endregion

        #region WinTune Optimizations

        // SYSTEM
        public static void DisableGameBar()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\GameBar", "AllowAutoGameMode", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\GameBar", "AutoGameModeEnabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\System\GameConfigStore", "GameDVR_Enabled", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: GameBar desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar GameBar - {ex.Message}");
            }
        }
        public static void EnableGameBar()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\GameBar", "AllowAutoGameMode", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\GameBar", "AutoGameModeEnabled", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\System\GameConfigStore", "GameDVR_Enabled", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: GameBar ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar GameBar - {ex.Message}");
            }
        }
        public static bool IsGameBarDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\System\GameConfigStore", "GameDVR_Enabled", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void EnableAutoEndTasks()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "AutoEndTasks", "1", RegistryValueKind.String);
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "HungAppTimeout", "1000", RegistryValueKind.String);
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WaitToKillAppTimeout", "2000", RegistryValueKind.String);
                Logger.Log("WinTune: AutoEndTasks ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar AutoEndTasks - {ex.Message}");
            }
        }
        public static void DisableAutoEndTasks()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "AutoEndTasks", "0", RegistryValueKind.String);
                Logger.Log("WinTune: AutoEndTasks desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar AutoEndTasks - {ex.Message}");
            }
        }
        public static bool IsAutoEndTasksEnabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "AutoEndTasks", "0");
                return value != null && value.ToString() == "1";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableAeDebug()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AeDebug", "Auto", "0", RegistryValueKind.String);
                Logger.Log("WinTune: AeDebug desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar AeDebug - {ex.Message}");
            }
        }
        public static void EnableAeDebug()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AeDebug", "Auto", "1", RegistryValueKind.String);
                Logger.Log("WinTune: AeDebug ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar AeDebug - {ex.Message}");
            }
        }
        public static bool IsAeDebugDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AeDebug", "Auto", "1");
                return value != null && value.ToString() == "0";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableAnimationEffectMaxMin()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", RegistryValueKind.String);
                Logger.Log("WinTune: Animação minimizar/maximizar desativada");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar animação - {ex.Message}");
            }
        }
        public static void EnableAnimationEffectMaxMin()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop\WindowMetrics", "MinAnimate", "1", RegistryValueKind.String);
                Logger.Log("WinTune: Animação minimizar/maximizar ativada");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar animação - {ex.Message}");
            }
        }
        public static bool IsAnimationEffectMaxMinDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop\WindowMetrics", "MinAnimate", "1");
                return value != null && value.ToString() == "0";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableAutoDefragIdle()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\OptimalLayout", "EnableAutoLayout", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: AutoDefragIdle desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar AutoDefragIdle - {ex.Message}");
            }
        }
        public static void EnableAutoDefragIdle()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\OptimalLayout", "EnableAutoLayout", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: AutoDefragIdle ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar AutoDefragIdle - {ex.Message}");
            }
        }
        public static bool IsAutoDefragIdleDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\OptimalLayout", "EnableAutoLayout", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableBootOptimize()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Dfrg\BootOptimizeFunction", "Enable", "N", RegistryValueKind.String);
                Logger.Log("WinTune: BootOptimize desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar BootOptimize - {ex.Message}");
            }
        }
        public static void EnableBootOptimize()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Dfrg\BootOptimizeFunction", "Enable", "Y", RegistryValueKind.String);
                Logger.Log("WinTune: BootOptimize ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar BootOptimize - {ex.Message}");
            }
        }
        public static bool IsBootOptimizeDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Dfrg\BootOptimizeFunction", "Enable", "N");
                return value != null && value.ToString() == "N";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableCustomInking()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\WindowsInkWorkspace", "AllowWindowsInkWorkspace", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: CustomInking desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar CustomInking - {ex.Message}");
            }
        }
        public static void EnableCustomInking()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\WindowsInkWorkspace", "AllowWindowsInkWorkspace", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: CustomInking ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar CustomInking - {ex.Message}");
            }
        }
        public static bool IsCustomInkingDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\WindowsInkWorkspace", "AllowWindowsInkWorkspace", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableCrashAutoReboot()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\CrashControl", "AutoReboot", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: CrashAutoReboot desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar CrashAutoReboot - {ex.Message}");
            }
        }
        public static void EnableCrashAutoReboot()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\CrashControl", "AutoReboot", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: CrashAutoReboot ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar CrashAutoReboot - {ex.Message}");
            }
        }
        public static bool IsCrashAutoRebootDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\CrashControl", "AutoReboot", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableErrorReporting()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: ErrorReporting desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar ErrorReporting - {ex.Message}");
            }
        }
        public static void EnableErrorReporting()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: ErrorReporting ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar ErrorReporting - {ex.Message}");
            }
        }
        public static bool IsErrorReportingDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", 0);
                return value != null && Convert.ToInt32(value) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableGoogleUpdateTask()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = "/Delete /TN \"GoogleUpdateTaskMachineCore\" /F",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                });
                Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = "/Delete /TN \"GoogleUpdateTaskMachineUA\" /F",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                });
                Logger.Log("WinTune: GoogleUpdateTask desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar GoogleUpdateTask - {ex.Message}");
            }
        }
        public static void EnableGoogleUpdateTask()
        {
            Logger.Log("WinTune: GoogleUpdateTask requer reinstalação do Chrome");
        }
        public static bool IsGoogleUpdateTaskDisabled()
        {
            return false;
        }

        public static void DisableLockScreen()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Personalization", "NoLockScreen", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: LockScreen desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar LockScreen - {ex.Message}");
            }
        }
        public static void EnableLockScreen()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Personalization", "NoLockScreen", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: LockScreen ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar LockScreen - {ex.Message}");
            }
        }
        public static bool IsLockScreenDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Personalization", "NoLockScreen", 0);
                return value != null && Convert.ToInt32(value) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableLowDiskSpaceChecks()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoLowDiskSpaceChecks", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: LowDiskSpaceChecks desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar LowDiskSpaceChecks - {ex.Message}");
            }
        }
        public static void EnableLowDiskSpaceChecks()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoLowDiskSpaceChecks", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: LowDiskSpaceChecks ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar LowDiskSpaceChecks - {ex.Message}");
            }
        }
        public static bool IsLowDiskSpaceChecksDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoLowDiskSpaceChecks", 0);
                return value != null && Convert.ToInt32(value) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableMemoryPagination()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "DisablePagingExecutive", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: MemoryPagination desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar MemoryPagination - {ex.Message}");
            }
        }
        public static void EnableMemoryPagination()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "DisablePagingExecutive", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: MemoryPagination ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar MemoryPagination - {ex.Message}");
            }
        }
        public static bool IsMemoryPaginationDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "DisablePagingExecutive", 0);
                return value != null && Convert.ToInt32(value) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableMenuShowDelay()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "MenuShowDelay", "0", RegistryValueKind.String);
                Logger.Log("WinTune: MenuShowDelay desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar MenuShowDelay - {ex.Message}");
            }
        }
        public static void EnableMenuShowDelay()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "MenuShowDelay", "400", RegistryValueKind.String);
                Logger.Log("WinTune: MenuShowDelay ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar MenuShowDelay - {ex.Message}");
            }
        }
        public static bool IsMenuShowDelayDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "MenuShowDelay", "400");
                return value != null && value.ToString() == "0";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableMicrosoftEdgeUpdateTask()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = "/Delete /TN \"MicrosoftEdgeUpdateTaskMachineCore\" /F",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                });
                Logger.Log("WinTune: MicrosoftEdgeUpdateTask desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar MicrosoftEdgeUpdateTask - {ex.Message}");
            }
        }
        public static void EnableMicrosoftEdgeUpdateTask()
        {
            Logger.Log("WinTune: MicrosoftEdgeUpdateTask requer reinstalação do Edge");
        }
        public static bool IsMicrosoftEdgeUpdateTaskDisabled()
        {
            return false;
        }

        public static void DisablePrefetchParameters()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", "EnablePrefetcher", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: PrefetchParameters desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar PrefetchParameters - {ex.Message}");
            }
        }
        public static void EnablePrefetchParameters()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", "EnablePrefetcher", 3, RegistryValueKind.DWord);
                Logger.Log("WinTune: PrefetchParameters ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar PrefetchParameters - {ex.Message}");
            }
        }
        public static bool IsPrefetchParametersDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", "EnablePrefetcher", 3);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableScheduledDefrag()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = "/Change /TN \"\\Microsoft\\Windows\\Defrag\\ScheduledDefrag\" /Disable",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                });
                Logger.Log("WinTune: ScheduledDefrag desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar ScheduledDefrag - {ex.Message}");
            }
        }
        public static void EnableScheduledDefrag()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = "/Change /TN \"\\Microsoft\\Windows\\Defrag\\ScheduledDefrag\" /Enable",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                });
                Logger.Log("WinTune: ScheduledDefrag ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar ScheduledDefrag - {ex.Message}");
            }
        }
        public static bool IsScheduledDefragDisabled()
        {
            return false;
        }

        public static void DisableShortcutText()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer", "link", "00000000", RegistryValueKind.String);
                Logger.Log("WinTune: ShortcutText desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar ShortcutText - {ex.Message}");
            }
        }
        public static void EnableShortcutText()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer", "link", "000000", RegistryValueKind.String);
                Logger.Log("WinTune: ShortcutText ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar ShortcutText - {ex.Message}");
            }
        }
        public static bool IsShortcutTextDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer", "link", "000000");
                return value != null && value.ToString() == "00000000";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void EnableIoPageLockLimit()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "IoPageLockLimit", 0x100000, RegistryValueKind.DWord);
                Logger.Log("WinTune: IoPageLockLimit ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar IoPageLockLimit - {ex.Message}");
            }
        }
        public static void DisableIoPageLockLimit()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "IoPageLockLimit", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: IoPageLockLimit desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar IoPageLockLimit - {ex.Message}");
            }
        }
        public static bool IsIoPageLockLimitEnabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "IoPageLockLimit", 0);
                return value != null && Convert.ToInt32(value) > 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void EnableLinkResolveIgnoreLinkInfo()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoResolveSearch", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoResolveTrack", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: LinkResolveIgnoreLinkInfo ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar LinkResolveIgnoreLinkInfo - {ex.Message}");
            }
        }
        public static void DisableLinkResolveIgnoreLinkInfo()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoResolveSearch", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoResolveTrack", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: LinkResolveIgnoreLinkInfo desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar LinkResolveIgnoreLinkInfo - {ex.Message}");
            }
        }
        public static bool IsLinkResolveIgnoreLinkInfoEnabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoResolveSearch", 0);
                return value != null && Convert.ToInt32(value) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void EnableMouseHoverTime()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "MouseHoverTime", "0", RegistryValueKind.String);
                Logger.Log("WinTune: MouseHoverTime ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar MouseHoverTime - {ex.Message}");
            }
        }
        public static void DisableMouseHoverTime()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "MouseHoverTime", "400", RegistryValueKind.String);
                Logger.Log("WinTune: MouseHoverTime desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar MouseHoverTime - {ex.Message}");
            }
        }
        public static bool IsMouseHoverTimeEnabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "MouseHoverTime", "400");
                return value != null && value.ToString() == "0";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void EnableNoInternetOpenWith()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Internet Settings", "MimeExclusionListForCache", ".exe;.dll;.com;.bat;.cmd;.vbs;.js;.msi", RegistryValueKind.String);
                Logger.Log("WinTune: NoInternetOpenWith ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar NoInternetOpenWith - {ex.Message}");
            }
        }
        public static void DisableNoInternetOpenWith()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Internet Settings", "MimeExclusionListForCache", "", RegistryValueKind.String);
                Logger.Log("WinTune: NoInternetOpenWith desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar NoInternetOpenWith - {ex.Message}");
            }
        }
        public static bool IsNoInternetOpenWithEnabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Internet Settings", "MimeExclusionListForCache", "");
                return value != null && !string.IsNullOrEmpty(value.ToString());
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void EnableNoResolveSearch()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoResolveSearch", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: NoResolveSearch ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar NoResolveSearch - {ex.Message}");
            }
        }
        public static void DisableNoResolveSearch()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoResolveSearch", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: NoResolveSearch desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar NoResolveSearch - {ex.Message}");
            }
        }
        public static bool IsNoResolveSearchEnabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoResolveSearch", 0);
                return value != null && Convert.ToInt32(value) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void EnableNoResolveTrack()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoResolveTrack", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: NoResolveTrack ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar NoResolveTrack - {ex.Message}");
            }
        }
        public static void DisableNoResolveTrack()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoResolveTrack", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: NoResolveTrack desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar NoResolveTrack - {ex.Message}");
            }
        }
        public static bool IsNoResolveTrackEnabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoResolveTrack", 0);
                return value != null && Convert.ToInt32(value) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void EnableNumLockonStartup()
        {
            try
            {
                Registry.SetValue(@"HKEY_USERS\.DEFAULT\Control Panel\Keyboard", "InitialKeyboardIndicators", "2", RegistryValueKind.String);
                Logger.Log("WinTune: NumLockonStartup ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar NumLockonStartup - {ex.Message}");
            }
        }
        public static void DisableNumLockonStartup()
        {
            try
            {
                Registry.SetValue(@"HKEY_USERS\.DEFAULT\Control Panel\Keyboard", "InitialKeyboardIndicators", "0", RegistryValueKind.String);
                Logger.Log("WinTune: NumLockonStartup desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar NumLockonStartup - {ex.Message}");
            }
        }
        public static bool IsNumLockonStartupEnabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_USERS\.DEFAULT\Control Panel\Keyboard", "InitialKeyboardIndicators", "0");
                return value != null && value.ToString() == "2";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void EnableOptimizeNetworkTransfer()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", 0xffffffff, RegistryValueKind.DWord);
                Logger.Log("WinTune: OptimizeNetworkTransfer ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar OptimizeNetworkTransfer - {ex.Message}");
            }
        }
        public static void DisableOptimizeNetworkTransfer()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", 10, RegistryValueKind.DWord);
                Logger.Log("WinTune: OptimizeNetworkTransfer desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar OptimizeNetworkTransfer - {ex.Message}");
            }
        }
        public static bool IsNetworkTransferOptimized()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", 10);
                return value != null && Convert.ToInt64(value) == 0xffffffff;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void EnableOptimizeProcessorPerformance()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\bc5038f7-23e0-4960-96da-33abaf5935ec", "Default", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: OptimizeProcessorPerformance ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar OptimizeProcessorPerformance - {ex.Message}");
            }
        }
        public static void DisableOptimizeProcessorPerformance()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\bc5038f7-23e0-4960-96da-33abaf5935ec", "Default", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: OptimizeProcessorPerformance desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar OptimizeProcessorPerformance - {ex.Message}");
            }
        }
        public static bool IsProcessorPerformanceOptimized()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\bc5038f7-23e0-4960-96da-33abaf5935ec", "Default", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void EnableOptimizeRefreshPolicy()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Update", "UpdateMode", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: OptimizeRefreshPolicy ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar OptimizeRefreshPolicy - {ex.Message}");
            }
        }
        public static void DisableOptimizeRefreshPolicy()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Update", "UpdateMode", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: OptimizeRefreshPolicy desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar OptimizeRefreshPolicy - {ex.Message}");
            }
        }
        public static bool IsRefreshPolicyOptimized()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Update", "UpdateMode", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void EnableShutdownAcceleration()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control", "WaitToKillServiceTimeout", "1000", RegistryValueKind.String);
                Logger.Log("WinTune: ShutdownAcceleration ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar ShutdownAcceleration - {ex.Message}");
            }
        }
        public static void DisableShutdownAcceleration()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control", "WaitToKillServiceTimeout", "5000", RegistryValueKind.String);
                Logger.Log("WinTune: ShutdownAcceleration desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar ShutdownAcceleration - {ex.Message}");
            }
        }
        public static bool IsShutdownAccelerationEnabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control", "WaitToKillServiceTimeout", "5000");
                return value != null && value.ToString() == "1000";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void EnableSnippingPrintScreen()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Keyboard", "PrintScreenKeyForSnippingEnabled", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: SnippingPrintScreen ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar SnippingPrintScreen - {ex.Message}");
            }
        }
        public static void DisableSnippingPrintScreen()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Keyboard", "PrintScreenKeyForSnippingEnabled", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: SnippingPrintScreen desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar SnippingPrintScreen - {ex.Message}");
            }
        }
        public static bool IsSnippingPrintScreenEnabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Keyboard", "PrintScreenKeyForSnippingEnabled", 0);
                return value != null && Convert.ToInt32(value) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        // PRIVACY
        public static void DisableWebSearch()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: WebSearch desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar WebSearch - {ex.Message}");
            }
        }
        public static void EnableWebSearch()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: WebSearch ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar WebSearch - {ex.Message}");
            }
        }
        public static bool IsWebSearchDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableMSACloudSearch()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "CloudSearchEnabled", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: MSACloudSearch desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar MSACloudSearch - {ex.Message}");
            }
        }
        public static void EnableMSACloudSearch()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "CloudSearchEnabled", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: MSACloudSearch ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar MSACloudSearch - {ex.Message}");
            }
        }
        public static bool IsMSACloudSearchDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "CloudSearchEnabled", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableAADCloudSearch()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "RestrictCloudSearch", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: AADCloudSearch desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar AADCloudSearch - {ex.Message}");
            }
        }
        public static void EnableAADCloudSearch()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "RestrictCloudSearch", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: AADCloudSearch ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar AADCloudSearch - {ex.Message}");
            }
        }
        public static bool IsAADCloudSearchDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "RestrictCloudSearch", 0);
                return value != null && Convert.ToInt32(value) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableDeviceSearchHistory()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "DeviceHistoryEnabled", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: DeviceSearchHistory desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar DeviceSearchHistory - {ex.Message}");
            }
        }
        public static void EnableDeviceSearchHistory()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "DeviceHistoryEnabled", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: DeviceSearchHistory ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar DeviceSearchHistory - {ex.Message}");
            }
        }
        public static bool IsDeviceSearchHistoryDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "DeviceHistoryEnabled", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableDiagTrack()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: DiagTrack desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar DiagTrack - {ex.Message}");
            }
        }
        public static void EnableDiagTrack()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 3, RegistryValueKind.DWord);
                Logger.Log("WinTune: DiagTrack ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar DiagTrack - {ex.Message}");
            }
        }
        public static bool IsDiagTrackDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 3);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DiagnosticDataOff()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: DiagnosticDataOff ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar DiagnosticDataOff - {ex.Message}");
            }
        }
        public static void DiagnosticDataOn()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 3, RegistryValueKind.DWord);
                Logger.Log("WinTune: DiagnosticDataOn ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar DiagnosticDataOn - {ex.Message}");
            }
        }
        public static bool IsDiagnosticDataOff()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 3);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableAdsOnLockScreen()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: AdsOnLockScreen desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar AdsOnLockScreen - {ex.Message}");
            }
        }
        public static void EnableAdsOnLockScreen()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: AdsOnLockScreen ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar AdsOnLockScreen - {ex.Message}");
            }
        }
        public static bool IsAdsOnLockScreenDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableAutoInstallationApps()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: AutoInstallationApps desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar AutoInstallationApps - {ex.Message}");
            }
        }
        public static void EnableAutoInstallationApps()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: AutoInstallationApps ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar AutoInstallationApps - {ex.Message}");
            }
        }
        public static bool IsAutoInstallationAppsDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableAutoplay()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers", "DisableAutoplay", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: Autoplay desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar Autoplay - {ex.Message}");
            }
        }
        public static void EnableAutoplay()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers", "DisableAutoplay", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: Autoplay ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar Autoplay - {ex.Message}");
            }
        }
        public static bool IsAutoplayDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers", "DisableAutoplay", 0);
                return value != null && Convert.ToInt32(value) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableVBSCodeIntegrity()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: VBSCodeIntegrity desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar VBSCodeIntegrity - {ex.Message}");
            }
        }
        public static void EnableVBSCodeIntegrity()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: VBSCodeIntegrity ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar VBSCodeIntegrity - {ex.Message}");
            }
        }
        public static bool IsVBSCodeIntegrityDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableOfferSuggestions()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "ContentDeliveryAllowed", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: OfferSuggestions desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar OfferSuggestions - {ex.Message}");
            }
        }
        public static void EnableOfferSuggestions()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "ContentDeliveryAllowed", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: OfferSuggestions ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar OfferSuggestions - {ex.Message}");
            }
        }
        public static bool IsOfferSuggestionsDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "ContentDeliveryAllowed", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisablePersonalizedAdsStoreApps()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: PersonalizedAdsStoreApps desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar PersonalizedAdsStoreApps - {ex.Message}");
            }
        }
        public static void EnablePersonalizedAdsStoreApps()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: PersonalizedAdsStoreApps ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar PersonalizedAdsStoreApps - {ex.Message}");
            }
        }
        public static bool IsPersonalizedAdsStoreAppsDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableRemoteRegAccess()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecurePipeServers\winreg", "RemoteRegAccess", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: RemoteRegAccess desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar RemoteRegAccess - {ex.Message}");
            }
        }
        public static void EnableRemoteRegAccess()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecurePipeServers\winreg", "RemoteRegAccess", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: RemoteRegAccess ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar RemoteRegAccess - {ex.Message}");
            }
        }
        public static bool IsRemoteRegAccessDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecurePipeServers\winreg", "RemoteRegAccess", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableSettingsAppSuggestions()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "ContentDeliveryAllowed", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: SettingsAppSuggestions desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar SettingsAppSuggestions - {ex.Message}");
            }
        }
        public static void EnableSettingsAppSuggestions()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "ContentDeliveryAllowed", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: SettingsAppSuggestions ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar SettingsAppSuggestions - {ex.Message}");
            }
        }
        public static bool IsSettingsAppSuggestionsDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "ContentDeliveryAllowed", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableTailoredExperiences()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: TailoredExperiences desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar TailoredExperiences - {ex.Message}");
            }
        }
        public static void EnableTailoredExperiences()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: TailoredExperiences ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar TailoredExperiences - {ex.Message}");
            }
        }
        public static bool IsTailoredExperiencesDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableTipsAndSuggestions()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "ContentDeliveryAllowed", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: TipsAndSuggestions desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar TipsAndSuggestions - {ex.Message}");
            }
        }
        public static void EnableTipsAndSuggestions()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "ContentDeliveryAllowed", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: TipsAndSuggestions ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar TipsAndSuggestions - {ex.Message}");
            }
        }
        public static bool IsTipsAndSuggestionsDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableWCE()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", "Disabled", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: WCE desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar WCE - {ex.Message}");
            }
        }
        public static void EnableWCE()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", "Disabled", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: WCE ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar WCE - {ex.Message}");
            }
        }
        public static bool IsWCEDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", "Disabled", 0);
                return value != null && Convert.ToInt32(value) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableVisualStudioTelemetry()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\VSCommon\15.0\SQM", "OptIn", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\VSCommon\16.0\SQM", "OptIn", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\VSCommon\17.0\SQM", "OptIn", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: VisualStudioTelemetry desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar VisualStudioTelemetry - {ex.Message}");
            }
        }
        public static void EnableVisualStudioTelemetry()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\VSCommon\15.0\SQM", "OptIn", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\VSCommon\16.0\SQM", "OptIn", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\VSCommon\17.0\SQM", "OptIn", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: VisualStudioTelemetry ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar VisualStudioTelemetry - {ex.Message}");
            }
        }
        public static bool IsVisualStudioTelemetryDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\VSCommon\17.0\SQM", "OptIn", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableWindowsFeedback()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: WindowsFeedback desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar WindowsFeedback - {ex.Message}");
            }
        }
        public static void EnableWindowsFeedback()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 3, RegistryValueKind.DWord);
                Logger.Log("WinTune: WindowsFeedback ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar WindowsFeedback - {ex.Message}");
            }
        }
        public static bool IsWindowsFeedbackDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        // EXPLORER
        public static void DisableAutoSuggest()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\AutoComplete", "AutoSuggest", "No", RegistryValueKind.String);
                Logger.Log("WinTune: AutoSuggest desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar AutoSuggest - {ex.Message}");
            }
        }
        public static void EnableAutoSuggest()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\AutoComplete", "AutoSuggest", "Yes", RegistryValueKind.String);
                Logger.Log("WinTune: AutoSuggest ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar AutoSuggest - {ex.Message}");
            }
        }
        public static bool IsAutoSuggestDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\AutoComplete", "AutoSuggest", "Yes");
                return value != null && value.ToString() == "No";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableAppendCompletion()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\AutoComplete", "Append Completion", "No", RegistryValueKind.String);
                Logger.Log("WinTune: AppendCompletion desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar AppendCompletion - {ex.Message}");
            }
        }
        public static void EnableAppendCompletion()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\AutoComplete", "Append Completion", "Yes", RegistryValueKind.String);
                Logger.Log("WinTune: AppendCompletion ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar AppendCompletion - {ex.Message}");
            }
        }
        public static bool IsAppendCompletionDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\AutoComplete", "Append Completion", "Yes");
                return value != null && value.ToString() == "No";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void ShowExtensions()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: ShowExtensions ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar ShowExtensions - {ex.Message}");
            }
        }
        public static void HideExtensions()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: ShowExtensions desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar ShowExtensions - {ex.Message}");
            }
        }
        public static bool IsExtensionsShown()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void ShowHidden()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Hidden", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: ShowHidden ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar ShowHidden - {ex.Message}");
            }
        }
        public static void HideHidden()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Hidden", 2, RegistryValueKind.DWord);
                Logger.Log("WinTune: ShowHidden desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar ShowHidden - {ex.Message}");
            }
        }
        public static bool IsHiddenShown()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Hidden", 2);
                return value != null && Convert.ToInt32(value) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void ShowHiddenSystem()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSuperHidden", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: ShowHiddenSystem ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar ShowHiddenSystem - {ex.Message}");
            }
        }
        public static void HideHiddenSystem()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSuperHidden", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: ShowHiddenSystem desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar ShowHiddenSystem - {ex.Message}");
            }
        }
        public static bool IsHiddenSystemShown()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSuperHidden", 0);
                return value != null && Convert.ToInt32(value) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void ShowThisPC()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FolderDescriptions\{A52BBA46-E9E1-435f-B3D9-28DAA648C0F6}", "PropertyBag", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: ShowThisPC ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar ShowThisPC - {ex.Message}");
            }
        }
        public static void HideThisPC()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FolderDescriptions\{A52BBA46-E9E1-435f-B3D9-28DAA648C0F6}", "PropertyBag", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: ShowThisPC desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar ShowThisPC - {ex.Message}");
            }
        }
        public static bool IsThisPCShown()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FolderDescriptions\{A52BBA46-E9E1-435f-B3D9-28DAA648C0F6}", "PropertyBag", 0);
                return value != null && Convert.ToInt32(value) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void OpenFileExplorerThisPC()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LaunchTo", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: OpenFileExplorerThisPC ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar OpenFileExplorerThisPC - {ex.Message}");
            }
        }
        public static void DisableOpenFileExplorerThisPC()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LaunchTo", 2, RegistryValueKind.DWord);
                Logger.Log("WinTune: OpenFileExplorerThisPC desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar OpenFileExplorerThisPC - {ex.Message}");
            }
        }
        public static bool IsFileExplorerThisPCEnabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LaunchTo", 2);
                return value != null && Convert.ToInt32(value) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void IncreaseIconCache()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "Max Cached Icons", 8192, RegistryValueKind.String);
                Logger.Log("WinTune: IncreaseIconCache ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar IncreaseIconCache - {ex.Message}");
            }
        }
        public static void ResetIconCache()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "Max Cached Icons", 500, RegistryValueKind.String);
                Logger.Log("WinTune: IncreaseIconCache desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar IncreaseIconCache - {ex.Message}");
            }
        }
        public static bool IsIconCacheIncreased()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "Max Cached Icons", "500");
                return value != null && Convert.ToInt32(value) > 500;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableRecentFiles()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackDocs", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: RecentFiles desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar RecentFiles - {ex.Message}");
            }
        }
        public static void EnableRecentFiles()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackDocs", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: RecentFiles ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar RecentFiles - {ex.Message}");
            }
        }
        public static bool IsRecentFilesDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackDocs", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableFrequentFolders()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackProgs", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: FrequentFolders desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar FrequentFolders - {ex.Message}");
            }
        }
        public static void EnableFrequentFolders()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackProgs", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: FrequentFolders ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar FrequentFolders - {ex.Message}");
            }
        }
        public static bool IsFrequentFoldersDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackProgs", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableSyncProviderNotifications()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSyncProviderNotifications", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: SyncProviderNotifications desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar SyncProviderNotifications - {ex.Message}");
            }
        }
        public static void EnableSyncProviderNotifications()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSyncProviderNotifications", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: SyncProviderNotifications ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar SyncProviderNotifications - {ex.Message}");
            }
        }
        public static bool IsSyncProviderNotificationsDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSyncProviderNotifications", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        // START MENU
        public static void DisableStartMenuAppSuggestions()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: StartMenuAppSuggestions desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar StartMenuAppSuggestions - {ex.Message}");
            }
        }
        public static void EnableStartMenuAppSuggestions()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: StartMenuAppSuggestions ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar StartMenuAppSuggestions - {ex.Message}");
            }
        }
        public static bool IsStartMenuAppSuggestionsDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void HideMostUsedApps()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_RecentDocs", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: MostUsedApps oculto");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ocultar MostUsedApps - {ex.Message}");
            }
        }
        public static void ShowMostUsedApps()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_RecentDocs", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: MostUsedApps visível");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao mostrar MostUsedApps - {ex.Message}");
            }
        }
        public static bool IsMostUsedAppsHidden()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_RecentDocs", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void HideStartMenuRecentlyAdded()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_ShowRecentApps", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: StartMenuRecentlyAdded oculto");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ocultar StartMenuRecentlyAdded - {ex.Message}");
            }
        }
        public static void ShowStartMenuRecentlyAdded()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_ShowRecentApps", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: StartMenuRecentlyAdded visível");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao mostrar StartMenuRecentlyAdded - {ex.Message}");
            }
        }
        public static bool IsStartMenuRecentlyAddedHidden()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_ShowRecentApps", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void HideStartMenuRecentlyOpened()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackDocs", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: StartMenuRecentlyOpened oculto");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ocultar StartMenuRecentlyOpened - {ex.Message}");
            }
        }
        public static void ShowStartMenuRecentlyOpened()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackDocs", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: StartMenuRecentlyOpened visível");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao mostrar StartMenuRecentlyOpened - {ex.Message}");
            }
        }
        public static bool IsStartMenuRecentlyOpenedHidden()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackDocs", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void HideStartMenuAccountNotifications()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_AccountNotifications", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: StartMenuAccountNotifications oculto");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ocultar StartMenuAccountNotifications - {ex.Message}");
            }
        }
        public static void ShowStartMenuAccountNotifications()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_AccountNotifications", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: StartMenuAccountNotifications visível");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao mostrar StartMenuAccountNotifications - {ex.Message}");
            }
        }
        public static bool IsStartMenuAccountNotificationsHidden()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_AccountNotifications", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void HideStartMenuRecommendations()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_IrisRecommendations", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: StartMenuRecommendations oculto");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ocultar StartMenuRecommendations - {ex.Message}");
            }
        }
        public static void ShowStartMenuRecommendations()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_IrisRecommendations", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: StartMenuRecommendations visível");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao mostrar StartMenuRecommendations - {ex.Message}");
            }
        }
        public static bool IsStartMenuRecommendationsHidden()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_IrisRecommendations", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        // OPTIONAL
        public static void EnableDarkMode()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: DarkMode ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar DarkMode - {ex.Message}");
            }
        }
        public static void DisableDarkMode()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: DarkMode desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar DarkMode - {ex.Message}");
            }
        }
        public static bool IsDarkModeEnabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void EnableClassicContextMenu()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", "", RegistryValueKind.String);
                Logger.Log("WinTune: ClassicContextMenu ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar ClassicContextMenu - {ex.Message}");
            }
        }
        public static void DisableClassicContextMenu()
        {
            try
            {
                Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}", true)?.DeleteSubKeyTree("");
                Logger.Log("WinTune: ClassicContextMenu desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar ClassicContextMenu - {ex.Message}");
            }
        }
        public static bool IsClassicContextMenuEnabled()
        {
            try
            {
                var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32");
                return key != null;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void EnableAUOptions()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: AUOptions ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar AUOptions - {ex.Message}");
            }
        }
        public static void DisableAUOptions()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: AUOptions desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar AUOptions - {ex.Message}");
            }
        }
        public static bool IsAUOptionsEnabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableAutoWindowsUpdates()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "AUOptions", 2, RegistryValueKind.DWord);
                Logger.Log("WinTune: AutoWindowsUpdates desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar AutoWindowsUpdates - {ex.Message}");
            }
        }
        public static void EnableAutoWindowsUpdates()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "AUOptions", 4, RegistryValueKind.DWord);
                Logger.Log("WinTune: AutoWindowsUpdates ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar AutoWindowsUpdates - {ex.Message}");
            }
        }
        public static bool IsAutoWindowsUpdatesDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", 0);
                return value != null && Convert.ToInt32(value) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableHibernate()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powercfg",
                    Arguments = "/h off",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                });
                Logger.Log("WinTune: Hibernate desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar Hibernate - {ex.Message}");
            }
        }
        public static void EnableHibernate()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powercfg",
                    Arguments = "/h on",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                });
                Logger.Log("WinTune: Hibernate ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar Hibernate - {ex.Message}");
            }
        }
        public static bool IsHibernateDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableHybridSleep()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\238C9FA8-0AAD-41ED-83F4-97BE242C8F20\94ACACD1-32AF-449B-9A99-2D291A88E8B3", "ACSettingIndex", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\238C9FA8-0AAD-41ED-83F4-97BE242C8F20\94ACACD1-32AF-449B-9A99-2D291A88E8B3", "DCSettingIndex", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: HybridSleep desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar HybridSleep - {ex.Message}");
            }
        }
        public static void EnableHybridSleep()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\238C9FA8-0AAD-41ED-83F4-97BE242C8F20\94ACACD1-32AF-449B-9A99-2D291A88E8B3", "ACSettingIndex", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\238C9FA8-0AAD-41ED-83F4-97BE242C8F20\94ACACD1-32AF-449B-9A99-2D291A88E8B3", "DCSettingIndex", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: HybridSleep ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar HybridSleep - {ex.Message}");
            }
        }
        public static bool IsHybridSleepDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\238C9FA8-0AAD-41ED-83F4-97BE242C8F20\94ACACD1-32AF-449B-9A99-2D291A88E8B3", "ACSettingIndex", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisablePrintSpooler()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = "config Spooler start=disabled",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                });
                Process.Start(new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = "stop Spooler",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                });
                Logger.Log("WinTune: PrintSpooler desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar PrintSpooler - {ex.Message}");
            }
        }
        public static void EnablePrintSpooler()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = "config Spooler start=auto",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                });
                Process.Start(new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = "start Spooler",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                });
                Logger.Log("WinTune: PrintSpooler ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar PrintSpooler - {ex.Message}");
            }
        }
        public static bool IsPrintSpoolerDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Spooler", "Start", 2);
                return value != null && Convert.ToInt32(value) == 4;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableSleep()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\238C9FA8-0AAD-41ED-83F4-97BE242C8F20\29F6C1DB-86DA-48C5-9FDB-F2B67B1F44DA", "ACSettingIndex", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\238C9FA8-0AAD-41ED-83F4-97BE242C8F20\29F6C1DB-86DA-48C5-9FDB-F2B67B1F44DA", "DCSettingIndex", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: Sleep desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar Sleep - {ex.Message}");
            }
        }
        public static void EnableSleep()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\238C9FA8-0AAD-41ED-83F4-97BE242C8F20\29F6C1DB-86DA-48C5-9FDB-F2B67B1F44DA", "ACSettingIndex", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\238C9FA8-0AAD-41ED-83F4-97BE242C8F20\29F6C1DB-86DA-48C5-9FDB-F2B67B1F44DA", "DCSettingIndex", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: Sleep ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar Sleep - {ex.Message}");
            }
        }
        public static bool IsSleepDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\238C9FA8-0AAD-41ED-83F4-97BE242C8F20\29F6C1DB-86DA-48C5-9FDB-F2B67B1F44DA", "ACSettingIndex", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableSystemRestore()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-Command \"Disable-ComputerRestore -Drive 'C:'\"",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                });
                Logger.Log("WinTune: SystemRestore desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar SystemRestore - {ex.Message}");
            }
        }
        public static void EnableSystemRestore()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-Command \"Enable-ComputerRestore -Drive 'C:'\"",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                });
                Logger.Log("WinTune: SystemRestore ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar SystemRestore - {ex.Message}");
            }
        }
        public static bool IsSystemRestoreDisabled()
        {
            try
            {

                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "RPSessionInterval", 0);
                return value == null || Convert.ToInt32(value) < 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableTurnOffDisplay()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "ScreenSaveActive", "0", RegistryValueKind.String);
                Logger.Log("WinTune: TurnOffDisplay desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar TurnOffDisplay - {ex.Message}");
            }
        }
        public static void EnableTurnOffDisplay()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "ScreenSaveActive", "1", RegistryValueKind.String);
                Logger.Log("WinTune: TurnOffDisplay ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar TurnOffDisplay - {ex.Message}");
            }
        }
        public static bool IsTurnOffDisplayDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "ScreenSaveActive", "1");
                return value != null && value.ToString() == "0";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void HideWindowsSecurityNotifications()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings", "C_SecurityAndMaintenance", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: WindowsSecurityNotifications oculto");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ocultar WindowsSecurityNotifications - {ex.Message}");
            }
        }
        public static void ShowWindowsSecurityNotifications()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings", "C_SecurityAndMaintenance", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: WindowsSecurityNotifications visível");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao mostrar WindowsSecurityNotifications - {ex.Message}");
            }
        }
        public static bool IsWindowsSecurityNotificationsHidden()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings", "C_SecurityAndMaintenance", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void HideWindowsSecurityNoncriticalNotifications()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows Defender Security Center\Notifications", "DisableNonCriticalNotifications", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: WindowsSecurityNoncriticalNotifications oculto");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ocultar WindowsSecurityNoncriticalNotifications - {ex.Message}");
            }
        }
        public static void ShowWindowsSecurityNoncriticalNotifications()
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows Defender Security Center\Notifications", "DisableNonCriticalNotifications", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: WindowsSecurityNoncriticalNotifications visível");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao mostrar WindowsSecurityNoncriticalNotifications - {ex.Message}");
            }
        }
        public static bool IsWindowsSecurityNoncriticalNotificationsHidden()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows Defender Security Center\Notifications", "DisableNonCriticalNotifications", 0);
                return value != null && Convert.ToInt32(value) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableWindowsSearch()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = "config WSearch start=disabled",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                });
                Process.Start(new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = "stop WSearch",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                });
                Logger.Log("WinTune: WindowsSearch desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar WindowsSearch - {ex.Message}");
            }
        }
        public static void EnableWindowsSearch()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = "config WSearch start=auto",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                });
                Process.Start(new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = "start WSearch",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                });
                Logger.Log("WinTune: WindowsSearch ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar WindowsSearch - {ex.Message}");
            }
        }
        public static bool IsWindowsSearchDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\WSearch", "Start", 2);
                return value != null && Convert.ToInt32(value) == 4;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void UninstallOneDrive()
        {
            try
            {

                try
                {
                    SystemUtils.RunExternalProcess("taskkill", "/F /IM OneDrive.exe", true);
                    Logger.Log("WinTune: Processo OneDrive.exe terminado");
                }
                catch { /* Ignora se processo não estiver rodando */ }


                System.Threading.Thread.Sleep(1000);


                string onedriveSetup = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "OneDriveSetup.exe");
                if (File.Exists(onedriveSetup))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = onedriveSetup,
                        Arguments = "/uninstall",
                        WindowStyle = ProcessWindowStyle.Hidden,
                        UseShellExecute = true
                    });
                    Logger.Log("WinTune: OneDrive desinstalado via OneDriveSetup.exe");
                }
                else
                {
                    // Fallback para PowerShell se OneDriveSetup.exe não existir
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "powershell",
                        Arguments = "-Command \"Get-AppxPackage -Name Microsoft.OneDrive | Remove-AppxPackage\"",
                        WindowStyle = ProcessWindowStyle.Hidden,
                        UseShellExecute = true
                    });
                    Logger.Log("WinTune: OneDrive desinstalado via PowerShell (fallback)");
                }


                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\OneDrive", "DisableFileSync", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: OneDrive bloqueado de reinstalar via Group Policy");


                try
                {
                    Registry.ClassesRoot.DeleteSubKeyTree(@"CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}", false);
                    Registry.ClassesRoot.DeleteSubKeyTree(@"WOW6432Node\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}", false);
                    Logger.Log("WinTune: OneDrive removido do File Explorer sidebar");
                }
                catch { /* Ignora se chaves não existirem */ }


                string userPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", "Personal", $"{userPath}\\Documents", RegistryValueKind.String);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", "Desktop", $"{userPath}\\Desktop", RegistryValueKind.String);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", "My Pictures", $"{userPath}\\Pictures", RegistryValueKind.String);
                Logger.Log("WinTune: Folder redirection corrigido");

                Logger.Log("WinTune: OneDrive completamente removido");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desinstalar OneDrive - {ex.Message}");
            }
        }

        public static bool IsOneDriveUninstalled()
        {
            try
            {

                string onedriveSetup = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "OneDriveSetup.exe");
                if (File.Exists(onedriveSetup))
                    return false;


                bool anyOneDrive = false;
                foreach (var p in Process.GetProcessesByName("OneDrive"))
                {
                    if (!p.HasExited) anyOneDrive = true;
                    p.Dispose();
                }
                if (anyOneDrive)
                    return false;


                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell",
                        Arguments = "-Command \"Get-AppxPackage -Name Microsoft.OneDrive\"",
                        WindowStyle = ProcessWindowStyle.Hidden,
                        UseShellExecute = false,
                        RedirectStandardOutput = true
                    };
                    using (var process = Process.Start(psi))
                    {
                        if (process != null)
                        {
                            string output = process.StandardOutput.ReadToEnd();
                            process.WaitForExit();
                            if (!string.IsNullOrWhiteSpace(output))
                                return false;
                        }
                    }
                }
                catch { /* Ignora erro do PowerShell */ }


                string userPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string onedrivePath = Path.Combine(userPath, "OneDrive");
                if (Directory.Exists(onedrivePath))
                    return false;


                var disableFileSync = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\OneDrive", "DisableFileSync", 0);
                if (disableFileSync != null && Convert.ToInt32(disableFileSync) == 1)
                    return true;

                return true;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static void DisableMSDefender()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "DisableRealtimeMonitoring", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "DisableBehaviorMonitoring", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "DisableScanOnRealtimeEnable", 1, RegistryValueKind.DWord);
                Logger.Log("WinTune: MSDefender desativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao desativar MSDefender - {ex.Message}");
            }
        }
        public static void EnableMSDefender()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "DisableRealtimeMonitoring", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "DisableBehaviorMonitoring", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "DisableScanOnRealtimeEnable", 0, RegistryValueKind.DWord);
                Logger.Log("WinTune: MSDefender ativado");
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao ativar MSDefender - {ex.Message}");
            }
        }
        public static bool IsMSDefenderDisabled()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", 0);
                return value != null && Convert.ToInt32(value) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        #endregion

        #region TweaksPage UI - Info Methods

        public static (string Name, int L2CacheKb, int L3CacheKb) GetCpuInfo()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, L2CacheSize, L3CacheSize FROM Win32_Processor");
                foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
                {
                    using (obj)
                    {
                        string name = obj["Name"]?.ToString()?.Trim() ?? "Desconhecido";
                        int l2 = obj["L2CacheSize"] != null ? Convert.ToInt32(obj["L2CacheSize"]) : 0;
                        int l3 = obj["L3CacheSize"] != null ? Convert.ToInt32(obj["L3CacheSize"]) : 0;
                        return (name, l2, l3);
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return ("Desconhecido", 0, 0);
        }

        public static string GetPrimaryGpuName()
        {
            try
            {
                var names = GetAllGpuNames();
                return names.Count > 0 ? names[0] : "N/A";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return "N/A"; }
        }

        public static int GetVramAppliedMb()
        {
            try
            {
                using var primaryGpu = GetPrimaryGpu();
                if (primaryGpu == null) return 0;
                string? regPath = FindGpuRegistryPath(primaryGpu);
                if (string.IsNullOrEmpty(regPath)) return 0;
                var val = Registry.GetValue(regPath, "DedicatedSegmentSize", 0);
                return val != null ? Convert.ToInt32(val) : 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return 0; }
        }

        #endregion

        #region TweaksPage UI - Check Methods

        public static bool IsSecondLevelDataCacheSet()
        {
            try
            {
                var val = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "SecondLevelDataCache", 0);
                return val != null && Convert.ToInt64(val) > 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static bool IsVramTweakApplied()
        {
            try
            {
                using var primaryGpu = GetPrimaryGpu();
                if (primaryGpu == null) return false;
                string? regPath = FindGpuRegistryPath(primaryGpu);
                if (string.IsNullOrEmpty(regPath)) return false;
                var val = Registry.GetValue(regPath, "DedicatedSegmentSize", 0);
                return val != null && Convert.ToInt32(val) > 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static bool IsNagleAlgorithmDisabled()
        {
            try
            {
                string? regPath = GetActiveInterfaceRegPath();
                if (string.IsNullOrEmpty(regPath)) return false;
                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = localMachine.OpenSubKey(regPath.Replace("HKEY_LOCAL_MACHINE\\", ""), false);
                if (key == null) return false;
                var tcpNoDelay = key.GetValue("TCPNoDelay");
                return tcpNoDelay != null && Convert.ToInt32(tcpNoDelay) == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static bool IsCoreParkingDisabled()
        {
            try
            {
                string[] keys = {
                    @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c8-3b32988b1dd4\0cc5b647-c1df-4637-891a-dec35c318583",
                    @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c8-3b32988b1dd4\ea4be0c1-7c65-46f8-8c17-f298766665d9"
                };
                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                foreach (var keyPath in keys)
                {
                    using var key = localMachine.OpenSubKey(keyPath, false);
                    if (key == null) continue;
                    var valMax = key.GetValue("ValueMax");
                    var valMin = key.GetValue("ValueMin");
                    if (valMax == null || Convert.ToInt32(valMax) != 0) return false;
                    if (valMin == null || Convert.ToInt32(valMin) != 0) return false;
                }
                return true;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static (bool Success, string Message) ToggleAutoCacheTweak()
        {
            if (IsSecondLevelDataCacheSet())
            {
                RevertRegistryValue(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "SecondLevelDataCache");
                return (true, "Cache L2/L3 restaurado para padrão.");
            }
            else
            {
                ApplyAutoCacheTweak();
                return (true, "Cache L2/L3 configurado conforme CPU.");
            }
        }

        public static (bool Success, string Message) ToggleVramTweak()
        {
            if (IsVramTweakApplied())
            {
                RevertVramTweaks();
                return (true, "VRAM restaurada para padrão.");
            }
            else
            {
                ApplyAutomaticVramTweak();
                return (true, "VRAM ajustada automaticamente.");
            }
        }

        // ============================================================
        // RMCACHELOC (NVIDIA Resource Manager Cache)
        // RmCacheLoc e um valor do driver NVIDIA (Resource Manager) aplicado
        // via .inf; muitos guias de otimizacao o configuram com o numero de
        // nucleos LOGICOS da CPU para melhorar a localidade de cache no
        // driver. Requer admin (HKLM).
        // ============================================================

        private const string GpuClassRegPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        private const string GpuVideoRegRoot = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Video";

        private static bool RegKeyIsNvidia(RegistryKey key)
        {
            try
            {
                // Gatilho 1: nome do adaptador (mais comum)
                var desc = (key.GetValue("DriverDesc") ?? key.GetValue("HardwareInformation.AdapterString"))?.ToString() ?? "";
                if (desc.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0) return true;

                // Gatilho 2: MatchingDeviceId com vendor PCI NVIDIA (ven_10de)
                // — pega a placa mesmo se DriverDesc estiver vazio/estranho
                var match = key.GetValue("MatchingDeviceId")?.ToString() ?? "";
                if (match.StartsWith(@"pci\ven_10de", StringComparison.OrdinalIgnoreCase)) return true;

                // Gatilho 3: DLLs do driver user-mode instaladas (nvlddmkm/nvldumd).
                // REG_MULTI_SZ retorna string[] — juntar antes de procurar.
                var drvObj = key.GetValue("InstalledDisplayDrivers");
                string drv = drvObj is string[] drvArr ? string.Join(",", drvArr) : drvObj?.ToString() ?? "";
                if (drv.IndexOf("nvlddmkm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    drv.IndexOf("nvldumd", StringComparison.OrdinalIgnoreCase) >= 0) return true;

                // Gatilho 4: ProviderName NVIDIA
                var prov = key.GetValue("ProviderName")?.ToString() ?? "";
                return prov.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Resolve TODOS os caminhos de registro onde o driver NVIDIA le/grava config,
        /// com gatilhos em cascata para nunca ficar sem alvos:
        /// 1) PnP software key (Control\Class\{4d36e968-...}\00xx) — caminho canonico
        ///    (a placa pode estar em 0000 OU 0004).
        /// 2) Espelho Control\Video\{GUID}\0000 — um por adaptador
        ///    (ex: ...\Video\{3E046CDA-9115-11F1-8047-0E12A5BE7880}\0000).
        /// Cada caminho e validado por 4 gatilhos (DriverDesc-2/AdapterString, ven_10de,
        /// InstalledDisplayDrivers, ProviderName).
        /// 3) FALLBACK: se NENHUM match NVIDIA for encontrado, retorna TODAS as subchaves
        ///    de display (Class e Video GUIDs) que tenham DriverDesc — "aplica em todas
        ///    que existirem" em vez de aplicar simplesmente nada.
        /// </summary>
        private static List<string> FindNvidiaAdapterRegPaths()
        {
            var paths = new List<string>();
            try
            {
                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

                // 1) Control\Class (PnP software key) — 0000, 0001, 0004...
                using (var displayClass = localMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (displayClass != null)
                    {
                        foreach (var subName in displayClass.GetSubKeyNames())
                        {
                            if (!int.TryParse(subName, out _)) continue;
                            using var k = displayClass.OpenSubKey(subName);
                            if (k != null && RegKeyIsNvidia(k))
                                paths.Add($@"{GpuClassRegPath}\{subName}");
                        }
                    }
                }

                // 2) Control\Video\{GUID}\0000 — espelho por adaptador
                using (var videoRoot = localMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video"))
                {
                    if (videoRoot != null)
                    {
                        foreach (var guid in videoRoot.GetSubKeyNames())
                        {
                            if (!guid.StartsWith("{")) continue;
                            using var k = videoRoot.OpenSubKey(guid + @"\0000");
                            if (k != null && RegKeyIsNvidia(k))
                                paths.Add($@"{GpuVideoRegRoot}\{guid}\0000");
                        }
                    }
                }

                // 3) FALLBACK: nada NVIDIA encontrado — pega TODAS as chaves de display
                if (paths.Count == 0)
                {
                    using (var displayClass = localMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                    {
                        if (displayClass != null)
                        {
                            foreach (var subName in displayClass.GetSubKeyNames())
                            {
                                if (!int.TryParse(subName, out _)) continue;
                                using var k = displayClass.OpenSubKey(subName);
                                if (k != null && !string.IsNullOrEmpty(k.GetValue("DriverDesc")?.ToString()))
                                    paths.Add($@"{GpuClassRegPath}\{subName}");
                            }
                        }
                    }
                    using (var videoRoot = localMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video"))
                    {
                        if (videoRoot != null)
                        {
                            foreach (var guid in videoRoot.GetSubKeyNames())
                            {
                                if (!guid.StartsWith("{")) continue;
                                using var k = videoRoot.OpenSubKey(guid + @"\0000");
                                if (k != null && !string.IsNullOrEmpty(k.GetValue("DriverDesc")?.ToString()))
                                    paths.Add($@"{GpuVideoRegRoot}\{guid}\0000");
                            }
                        }
                    }
                    if (paths.Count > 0)
                        Logger.Log("RmCacheLoc: nenhuma chave NVIDIA explicita — fallback aplicado em todas as chaves de display encontradas.");
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return paths;
        }

        public static bool IsRmCacheLocSet()
        {
            try
            {
                foreach (var path in FindNvidiaAdapterRegPaths())
                {
                    var val = Registry.GetValue(path, "RmCacheLoc", 0);
                    if (val != null && Convert.ToInt32(val) > 0) return true;
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return false;
        }

        public static void ApplyRmCacheLocTweak()
        {
            try
            {
                int logicalProcessors = Environment.ProcessorCount;
                var paths = FindNvidiaAdapterRegPaths();
                int created = 0;
                foreach (var path in paths)
                {
                    // MUDANCA AGRESSIVA (mesmo padrao do tweak de VRAM): CreateSubKey
                    // cria a chave se ela nao existir — nunca falha por chave ausente.
                    string subPath = path.Replace(@"HKEY_LOCAL_MACHINE\", "");
                    bool existed = Registry.LocalMachine.OpenSubKey(subPath) != null;
                    using (var key = Registry.LocalMachine.CreateSubKey(subPath, true))
                    {
                        if (key == null) continue;
                        key.SetValue("RmCacheLoc", logicalProcessors, RegistryValueKind.DWord);
                        if (!existed) created++;
                    }
                }
                Logger.Log($"RmCacheLoc configurado em {paths.Count} chave(s) NVIDIA ({created} criada(s)): {logicalProcessors} nucleos logicos.");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static void RevertRmCacheLocTweak()
        {
            try
            {
                foreach (var path in FindNvidiaAdapterRegPaths())
                {
                    using var key = Registry.LocalMachine.OpenSubKey(path.Replace("HKEY_LOCAL_MACHINE\\", ""), true);
                    key?.DeleteValue("RmCacheLoc", false);
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static (bool Success, string Message) ToggleRmCacheLocTweak()
        {
            if (IsRmCacheLocSet())
            {
                RevertRmCacheLocTweak();
                return (true, "RmCacheLoc restaurado para padrão.");
            }
            else
            {
                ApplyRmCacheLocTweak();
                return (true, $"RmCacheLoc configurado com {Environment.ProcessorCount} núcleos lógicos.");
            }
        }

        // ============================================================
        // SHUTDOWN TWEAKS (WaitToKill, AutoEndTasks, ClearPageFile, VerboseStatus)
        // ============================================================

        private const string ControlPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control";
        private const string DesktopPath = @"HKEY_CURRENT_USER\Control Panel\Desktop";
        private const string MemMgmtPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
        private const string PoliciesSystemPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

        // --- WaitToKillServiceTimeout: 2000ms ---
        public static bool IsWaitToKillServiceOptimized()
        {
            try
            {
                var val = Registry.GetValue(ControlPath, "WaitToKillServiceTimeout", null) as string;
                return val != null && int.TryParse(val, out var ms) && ms <= 2000;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void OptimizeWaitToKillService()
        {
            try { Registry.SetValue(ControlPath, "WaitToKillServiceTimeout", "2000", RegistryValueKind.String); }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RevertWaitToKillService()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(ControlPath.Replace(@"HKEY_LOCAL_MACHINE\", ""), true);
                key?.DeleteValue("WaitToKillServiceTimeout", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- WaitToKillAppTimeout (HKCU): 2000ms ---
        public static bool IsWaitToKillAppOptimized()
        {
            try
            {
                var val = Registry.GetValue(DesktopPath, "WaitToKillAppTimeout", null) as string;
                return val != null && int.TryParse(val, out var ms) && ms <= 2000;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void OptimizeWaitToKillApp()
        {
            try { Registry.SetValue(DesktopPath, "WaitToKillAppTimeout", "2000", RegistryValueKind.String); }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RevertWaitToKillApp()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(DesktopPath.Replace(@"HKEY_CURRENT_USER\", ""), true);
                key?.DeleteValue("WaitToKillAppTimeout", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- HungAppTimeout (HKCU): 1000ms ---
        public static bool IsHungAppTimeoutOptimized()
        {
            try
            {
                var val = Registry.GetValue(DesktopPath, "HungAppTimeout", null) as string;
                return val != null && int.TryParse(val, out var ms) && ms <= 1000;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void OptimizeHungAppTimeout()
        {
            try { Registry.SetValue(DesktopPath, "HungAppTimeout", "1000", RegistryValueKind.String); }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RevertHungAppTimeout()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(DesktopPath.Replace(@"HKEY_CURRENT_USER\", ""), true);
                key?.DeleteValue("HungAppTimeout", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- ClearPageFileAtShutdown: 0 (disabled) ---
        public static bool IsClearPageFileDisabled()
        {
            try
            {
                var val = Registry.GetValue(MemMgmtPath, "ClearPageFileAtShutdown", null);
                return val is int i && i == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void DisableClearPageFile()
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(MemMgmtPath.Replace(@"HKEY_LOCAL_MACHINE\", ""));
                key?.SetValue("ClearPageFileAtShutdown", 0, RegistryValueKind.DWord);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void EnableClearPageFile()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(MemMgmtPath.Replace(@"HKEY_LOCAL_MACHINE\", ""), true);
                key?.DeleteValue("ClearPageFileAtShutdown", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- VerboseStatus: 1 (verbose boot/shutdown messages) ---
        public static bool IsVerboseStatusEnabled()
        {
            try
            {
                var val = Registry.GetValue(PoliciesSystemPath, "verbosestatus", null);
                return val is int i && i == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void EnableVerboseStatus()
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(PoliciesSystemPath.Replace(@"HKEY_LOCAL_MACHINE\", ""));
                key?.SetValue("verbosestatus", 1, RegistryValueKind.DWord);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void DisableVerboseStatus()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(PoliciesSystemPath.Replace(@"HKEY_LOCAL_MACHINE\", ""), true);
                key?.DeleteValue("verbosestatus", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- Quick App Kill (combined: WaitToKillApp + HungApp + AutoEndTasks) ---
        public static bool IsQuickAppKillApplied()
        {
            try
            {
                var wtk = Registry.GetValue(DesktopPath, "WaitToKillAppTimeout", null) as string;
                var hung = Registry.GetValue(DesktopPath, "HungAppTimeout", null) as string;
                var auto = Registry.GetValue(DesktopPath, "AutoEndTasks", null) as string;
                return wtk == "2000" && hung == "1000" && auto == "1";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void ApplyQuickAppKill()
        {
            try
            {
                Registry.SetValue(DesktopPath, "WaitToKillAppTimeout", "2000", RegistryValueKind.String);
                Registry.SetValue(DesktopPath, "HungAppTimeout", "1000", RegistryValueKind.String);
                Registry.SetValue(DesktopPath, "AutoEndTasks", "1", RegistryValueKind.String);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RevertQuickAppKill()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(DesktopPath.Replace(@"HKEY_CURRENT_USER\", ""), true);
                key?.DeleteValue("WaitToKillAppTimeout", false);
                key?.DeleteValue("HungAppTimeout", false);
                key?.DeleteValue("AutoEndTasks", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // ============================================================
        // INTERFACE & EXPLORER TWEAKS
        // ============================================================

        private const string ExplorerAdvancedPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        private const string ExplorerPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer";
        private         const string DesktopCtrlPath = @"HKEY_CURRENT_USER\Control Panel\Desktop";
        private const string VisualFxPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";
        private const string AppCompatPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppCompat";
        private const string WindowMetricsPath = @"HKEY_CURRENT_USER\Control Panel\Desktop\WindowMetrics";

        // --- MenuShowDelay: 0 (instant menus) ---
        public static bool IsMenuShowDelayOptimized()
        {
            try
            {
                var val = Registry.GetValue(DesktopCtrlPath, "MenuShowDelay", null) as string;
                return val == "0";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void OptimizeMenuShowDelay()
        {
            try { Registry.SetValue(DesktopCtrlPath, "MenuShowDelay", "0", RegistryValueKind.String); }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RevertMenuShowDelay()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(DesktopCtrlPath.Replace(@"HKEY_CURRENT_USER\", ""), true);
                key?.DeleteValue("MenuShowDelay", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- VisualFX (best performance): setting VisualFXSetting=2 is not enough,
        //     need to also disable MinAnimate, TaskbarAnimations, etc. ---
        public static bool IsVisualFXOptimized()
        {
            try
            {
                var fx = Registry.GetValue(VisualFxPath, "VisualFXSetting", null);
                if (!(fx is int i && i == 2)) return false;
                var minAnimate = Registry.GetValue(WindowMetricsPath, "MinAnimate", null) as string;
                if (minAnimate != "0") return false;
                var taskbar = Registry.GetValue(ExplorerAdvancedPath, "TaskbarAnimations", null);
                return taskbar is int t && t == 0;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void OptimizeVisualFX()
        {
            try
            {
                using var key1 = Registry.CurrentUser.CreateSubKey(VisualFxPath.Replace(@"HKEY_CURRENT_USER\", ""));
                key1?.SetValue("VisualFXSetting", 2, RegistryValueKind.DWord);

                using var key2 = Registry.CurrentUser.CreateSubKey(WindowMetricsPath.Replace(@"HKEY_CURRENT_USER\", ""));
                key2?.SetValue("MinAnimate", "0", RegistryValueKind.String);

                using var key3 = Registry.CurrentUser.CreateSubKey(ExplorerAdvancedPath.Replace(@"HKEY_CURRENT_USER\", ""));
                key3?.SetValue("TaskbarAnimations", 0, RegistryValueKind.DWord);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RevertVisualFX()
        {
            try
            {
                using var key1 = Registry.CurrentUser.OpenSubKey(VisualFxPath.Replace(@"HKEY_CURRENT_USER\", ""), true);
                key1?.DeleteValue("VisualFXSetting", false);

                using var key2 = Registry.CurrentUser.OpenSubKey(WindowMetricsPath.Replace(@"HKEY_CURRENT_USER\", ""), true);
                key2?.DeleteValue("MinAnimate", false);

                using var key3 = Registry.CurrentUser.OpenSubKey(ExplorerAdvancedPath.Replace(@"HKEY_CURRENT_USER\", ""), true);
                key3?.DeleteValue("TaskbarAnimations", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- Explorer LaunchTo (ComboBox): 1=ThisPC, 2=QuickAccess, 3=Downloads ---
        public static int GetExplorerLaunchToIndex()
        {
            try
            {
                var val = Registry.GetValue(ExplorerAdvancedPath, "LaunchTo", null);
                if (val is int i)
                {
                    if (i == 1) return 1; // This PC
                    if (i == 2) return 0; // Quick Access / Home
                    if (i == 3) return 2; // Downloads
                }
                return 0; // default = Quick Access
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return 0; }
        }
        public static void SetExplorerLaunchTo(int selectedIndex)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(ExplorerAdvancedPath.Replace(@"HKEY_CURRENT_USER\", ""));
                int value;
                switch (selectedIndex)
                {
                    case 1: value = 1; break; // This PC
                    case 2: value = 3; break; // Downloads
                    default: value = 2; break; // Quick Access / Home
                }
                key?.SetValue("LaunchTo", value, RegistryValueKind.DWord);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- Disable PCA (Program Compatibility Assistant) ---
        public static bool IsPCADisabled()
        {
            try
            {
                var val = Registry.GetValue(AppCompatPath, "DisablePCA", null);
                return val is int i && i == 1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void DisablePCA()
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(AppCompatPath.Replace(@"HKEY_LOCAL_MACHINE\", ""));
                key?.SetValue("DisablePCA", 1, RegistryValueKind.DWord);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void EnablePCA()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(AppCompatPath.Replace(@"HKEY_LOCAL_MACHINE\", ""), true);
                key?.DeleteValue("DisablePCA", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // ============================================================
        // CONTEXT MENU TWEAKS
        // ============================================================

        // --- Take Ownership (files + directories) ---
        public static bool IsTakeOwnershipAdded()
        {
            try
            {
                var val = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\*\shell\runas", "", null);
                return val is string s && !string.IsNullOrEmpty(s);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void AddTakeOwnership()
        {
            try
            {
                using var k1 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\*\shell\runas");
                k1?.SetValue("", "Take Ownership");
                k1?.SetValue("NoWorkingDirectory", "");
                using var c1 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\*\shell\runas\command");
                c1?.SetValue("", "cmd.exe /c takeown /f \"%1\" && icacls \"%1\" /grant administrators:F");

                using var k2 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\runas");
                k2?.SetValue("", "Take Ownership");
                k2?.SetValue("NoWorkingDirectory", "");
                using var c2 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\runas\command");
                c2?.SetValue("", "cmd.exe /c takeown /f \"%1\" /r /d y && icacls \"%1\" /grant administrators:F /t");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RemoveTakeOwnership()
        {
            try
            {
                using var s1 = Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell", true);
                s1?.DeleteSubKeyTree("runas", false);
                using var s2 = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell", true);
                s2?.DeleteSubKeyTree("runas", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- Take Ownership via Kit (IPC --takeown) — versão “SUPER” usando FileTakeOwnership in-process ---
        public static bool IsTakeOwnershipKitAdded()
        {
            try
            {
                string[] paths =
                {
                    @"HKEY_CURRENT_USER\Software\Classes\*\shell\kittakeown",
                    @"HKEY_CURRENT_USER\Software\Classes\Directory\shell\kittakeown",
                    @"HKEY_CURRENT_USER\Software\Classes\Drive\shell\kittakeown",
                };
                foreach (var p in paths)
                {
                    if (Registry.GetValue(p + @"\command", "", null) is string s && !string.IsNullOrEmpty(s))
                        return true;
                    if (Registry.GetValue(p, "", null) is string s2 && !string.IsNullOrEmpty(s2))
                        return true;
                }
                return false;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void AddTakeOwnershipKit()
        {
            try
            {
                RemoveTakeOwnershipKit();
                string exePath = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
                exePath = System.IO.Path.Combine(exePath, "KitLugia.GUI.exe");
                if (!System.IO.File.Exists(exePath))
                    exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(exePath)) return;

                string cmd = $"\"{exePath}\" --takeown \"%1\"";
                string icon = "%SystemRoot%\\System32\\imageres.dll,-78";
                string[] shellPaths =
                {
                    @"Software\Classes\*\shell",
                    @"Software\Classes\Directory\shell",
                    @"Software\Classes\Drive\shell"
                };
                foreach (var shellPath in shellPaths)
                {
                    using var k = Registry.CurrentUser.CreateSubKey(shellPath + @"\kittakeown");
                    k?.SetValue("", "👑 Take Ownership (KitLugia)");
                    k?.SetValue("Icon", icon);
                    k?.SetValue("HasLUAShield", "");
                    using var c = Registry.CurrentUser.CreateSubKey(shellPath + @"\kittakeown\command");
                    c?.SetValue("", cmd);
                }
                Logger.Log($"[TAKEOWN KIT] Menu de contexto registrado: {cmd}");
            }
            catch (Exception ex) { Logger.Log($"[TAKEOWN KIT] Erro ao registrar: {ex.Message}"); }
        }
        public static void RemoveTakeOwnershipKit()
        {
            try
            {
                string[] shellPaths =
                {
                    @"Software\Classes\*\shell",
                    @"Software\Classes\Directory\shell",
                    @"Software\Classes\Drive\shell"
                };
                string[] keyNames = { "kittakeown", "kit_takeown", "kit_takeownership" };
                foreach (var shellPath in shellPaths)
                    foreach (var keyName in keyNames)
                        try { using var s = Registry.CurrentUser.OpenSubKey(shellPath, true); s?.DeleteSubKeyTree(keyName, false); } catch { }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        /// <summary>Chamado a cada abertura do Kit: se o exe mudou de pasta (update/cópia), atualiza o comando do menu para o Kit mais recente.</summary>
        public static void RefreshContextMenuPathsIfNeeded()
        {
            try
            {
                string currentExe = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
                currentExe = System.IO.Path.Combine(currentExe, "KitLugia.GUI.exe");
                if (!System.IO.File.Exists(currentExe))
                    currentExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(currentExe)) return;

                // Take Ownership (kittakeown)
                if (IsTakeOwnershipKitAdded())
                {
                    string[] checkPaths =
                    {
                        @"HKEY_CURRENT_USER\Software\Classes\*\shell\kittakeown\command",
                        @"HKEY_CURRENT_USER\Software\Classes\Directory\shell\kittakeown\command",
                        @"HKEY_CURRENT_USER\Software\Classes\Drive\shell\kittakeown\command"
                    };
                    bool needsUpdate = false;
                    foreach (var p in checkPaths)
                    {
                        var cmd = Registry.GetValue(p, "", null) as string;
                        if (!string.IsNullOrEmpty(cmd) && cmd.IndexOf(currentExe, StringComparison.OrdinalIgnoreCase) < 0)
                        { needsUpdate = true; break; }
                    }
                    if (needsUpdate)
                    {
                        Logger.Log($"[TAKEOWN KIT] Atualizando caminho do menu para Kit mais recente: {currentExe}");
                        AddTakeOwnershipKit();
                    }
                }

                // Force Stop Unlock (forcestopunlock)
                if (IsForceStopUnlockAdded())
                {
                    string[] checkPaths =
                    {
                        @"HKEY_CURRENT_USER\Software\Classes\*\shell\forcestopunlock\command",
                        @"HKEY_CURRENT_USER\Software\Classes\Directory\shell\forcestopunlock\command",
                        @"HKEY_CURRENT_USER\Software\Classes\Drive\shell\forcestopunlock\command"
                    };
                    bool needsUpdate = false;
                    foreach (var p in checkPaths)
                    {
                        var cmd = Registry.GetValue(p, "", null) as string;
                        if (!string.IsNullOrEmpty(cmd) && cmd.IndexOf(currentExe, StringComparison.OrdinalIgnoreCase) < 0)
                        { needsUpdate = true; break; }
                    }
                    if (needsUpdate)
                    {
                        Logger.Log($"[FORCE STOP] Atualizando caminho do menu para Kit mais recente: {currentExe}");
                        AddForceStopUnlock();
                    }
                }
            }
            catch (Exception ex) { Logger.Log($"[CTX REFRESH] {ex.Message}"); }
        }

        // --- Force Close (exefile) ---
        public static bool IsForceCloseAdded()
        {
            try
            {
                var val = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\exefile\shell\forceclose\command", "", null);
                return val is string s && !string.IsNullOrEmpty(s);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void AddForceClose()
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\exefile\shell\forceclose");
                k?.SetValue("", "Force Close");
                using var c = Registry.CurrentUser.CreateSubKey(@"Software\Classes\exefile\shell\forceclose\command");
                c?.SetValue("", "taskkill /f /fi \"IMAGENAME eq %~nx1\"");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RemoveForceClose()
        {
            try
            {
                using var s = Registry.CurrentUser.OpenSubKey(@"Software\Classes\exefile\shell", true);
                s?.DeleteSubKeyTree("forceclose", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- CMD Here (background + drive) ---
        public static bool IsCmdHereAdded()
        {
            try
            {
                var val = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell\cmd_here\command", "", null);
                return val is string s && !string.IsNullOrEmpty(s);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void AddCmdHere()
        {
            try
            {
                using var k1 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\Background\shell\cmd_here");
                k1?.SetValue("", "CMD Here");
                using var c1 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\Background\shell\cmd_here\command");
                c1?.SetValue("", "cmd.exe /s /k pushd \"%V\"");

                using var k2 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Drive\shell\cmd_here");
                k2?.SetValue("", "CMD Here");
                using var c2 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Drive\shell\cmd_here\command");
                c2?.SetValue("", "cmd.exe /s /k pushd \"%V\"");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RemoveCmdHere()
        {
            try
            {
                using var s1 = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\Background\shell", true);
                s1?.DeleteSubKeyTree("cmd_here", false);
                using var s2 = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Drive\shell", true);
                s2?.DeleteSubKeyTree("cmd_here", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- PowerShell Here (background + drive) ---
        public static bool IsPowerShellHereAdded()
        {
            try
            {
                var val = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell\powershell_here\command", "", null);
                return val is string s && !string.IsNullOrEmpty(s);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void AddPowerShellHere()
        {
            try
            {
                using var k1 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\Background\shell\powershell_here");
                k1?.SetValue("", "PowerShell Here");
                using var c1 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\Background\shell\powershell_here\command");
                c1?.SetValue("", "powershell.exe -NoExit -Command Set-Location -Path \"%V\"");

                using var k2 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Drive\shell\powershell_here");
                k2?.SetValue("", "PowerShell Here");
                using var c2 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Drive\shell\powershell_here\command");
                c2?.SetValue("", "powershell.exe -NoExit -Command Set-Location -Path \"%V\"");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RemovePowerShellHere()
        {
            try
            {
                using var s1 = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\Background\shell", true);
                s1?.DeleteSubKeyTree("powershell_here", false);
                using var s2 = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Drive\shell", true);
                s2?.DeleteSubKeyTree("powershell_here", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- CMD as Admin (background + drive) ---
        public static bool IsCmdAdminAdded()
        {
            try
            {
                var val = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell\cmd_admin\command", "", null);
                return val is string s && !string.IsNullOrEmpty(s);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void AddCmdAdmin()
        {
            try
            {
                using var k1 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\Background\shell\cmd_admin");
                k1?.SetValue("", "CMD as Admin");
                k1?.SetValue("HasLUAShield", "");
                using var c1 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\Background\shell\cmd_admin\command");
                c1?.SetValue("", "powershell -Command \"Start-Process cmd -Verb RunAs -ArgumentList '/k pushd \\\"%V\\\"'\"");

                using var k2 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Drive\shell\cmd_admin");
                k2?.SetValue("", "CMD as Admin");
                k2?.SetValue("HasLUAShield", "");
                using var c2 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Drive\shell\cmd_admin\command");
                c2?.SetValue("", "powershell -Command \"Start-Process cmd -Verb RunAs -ArgumentList '/k pushd \\\"%V\\\"'\"");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RemoveCmdAdmin()
        {
            try
            {
                using var s1 = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\Background\shell", true);
                s1?.DeleteSubKeyTree("cmd_admin", false);
                using var s2 = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Drive\shell", true);
                s2?.DeleteSubKeyTree("cmd_admin", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- PowerShell as Admin (background + drive) ---
        public static bool IsPowerShellAdminAdded()
        {
            try
            {
                var val = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell\powershell_admin\command", "", null);
                return val is string s && !string.IsNullOrEmpty(s);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void AddPowerShellAdmin()
        {
            try
            {
                using var k1 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\Background\shell\powershell_admin");
                k1?.SetValue("", "PowerShell as Admin");
                k1?.SetValue("HasLUAShield", "");
                using var c1 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\Background\shell\powershell_admin\command");
                c1?.SetValue("", "powershell -Command \"Start-Process powershell -Verb RunAs -ArgumentList '-NoExit -Command Set-Location \\\"%V\\\"'\"");

                using var k2 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Drive\shell\powershell_admin");
                k2?.SetValue("", "PowerShell as Admin");
                k2?.SetValue("HasLUAShield", "");
                using var c2 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Drive\shell\powershell_admin\command");
                c2?.SetValue("", "powershell -Command \"Start-Process powershell -Verb RunAs -ArgumentList '-NoExit -Command Set-Location \\\"%V\\\"'\"");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RemovePowerShellAdmin()
        {
            try
            {
                using var s1 = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\Background\shell", true);
                s1?.DeleteSubKeyTree("powershell_admin", false);
                using var s2 = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Drive\shell", true);
                s2?.DeleteSubKeyTree("powershell_admin", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- Open with Notepad (all files) ---
        public static bool IsNotepadAdded()
        {
            try
            {
                var val = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\*\shell\openwithnotepad", "", null);
                return val is string s && !string.IsNullOrEmpty(s);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void AddNotepad()
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\*\shell\openwithnotepad");
                k?.SetValue("", "Abrir com Bloco de Notas");
                using var c = Registry.CurrentUser.CreateSubKey(@"Software\Classes\*\shell\openwithnotepad\command");
                c?.SetValue("", "notepad.exe \"%1\"");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RemoveNotepad()
        {
            try
            {
                using var s = Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell", true);
                s?.DeleteSubKeyTree("openwithnotepad", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- Copy as Path (files + folders) ---
        public static bool IsCopyAsPathAdded()
        {
            try
            {
                return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\*\shell\copyaspath", "", null) is string s1 && !string.IsNullOrEmpty(s1)
                    || Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\Directory\shell\copyaspath", "", null) is string s2 && !string.IsNullOrEmpty(s2);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void AddCopyAsPath()
        {
            try
            {
                using var k1 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\*\shell\copyaspath");
                k1?.SetValue("", "Copiar como Caminho");
                using var c1 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\*\shell\copyaspath\command");
                c1?.SetValue("", "powershell -Command \"[System.Windows.Clipboard]::SetText('%~1')\"");
                using var k2 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\copyaspath");
                k2?.SetValue("", "Copiar como Caminho");
                using var c2 = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\copyaspath\command");
                c2?.SetValue("", "powershell -Command \"[System.Windows.Clipboard]::SetText('%~1')\"");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RemoveCopyAsPath()
        {
            try
            {
                using var s1 = Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell", true);
                s1?.DeleteSubKeyTree("copyaspath", false);
                using var s2 = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell", true);
                s2?.DeleteSubKeyTree("copyaspath", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static string KitLugiaBaseFolder
        {
            get
            {
                try
                {
                    return AppDomain.CurrentDomain.BaseDirectory ?? "";
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                return "";
            }
        }

        // --- ForceStopUnlock (unlock locked files via Handle tool) ---
        public static string GetForceStopUnlockFolder()
        {
            return System.IO.Path.Combine(KitLugiaBaseFolder, "External", "ForceStopUnlock");
        }
        public static string GetForceStopUnlockScriptPath()
        {
            return System.IO.Path.Combine(GetForceStopUnlockFolder(), "Unlock-File.ps1");
        }

        public static bool IsForceStopUnlockAdded()
        {
            try
            {
                // FIX: checava só o valor padrão da chave "*", que pode existir vazia.
                // Agora considera ativo se QUALQUER um dos 3 locais registrados tiver
                // chave + command (o Add grava em *, Directory e Drive).
                string[] paths =
                {
                    @"HKEY_CURRENT_USER\Software\Classes\*\shell\forcestopunlock",
                    @"HKEY_CURRENT_USER\Software\Classes\Directory\shell\forcestopunlock",
                    @"HKEY_CURRENT_USER\Software\Classes\Drive\shell\forcestopunlock",
                };
                foreach (var path in paths)
                {
                    if (Registry.GetValue(path + @"\command", "", null) is string s && !string.IsNullOrEmpty(s))
                        return true;
                    // fallback: valor padrão preenchido (formato antigo)
                    if (Registry.GetValue(path, "", null) is string s2 && !string.IsNullOrEmpty(s2))
                        return true;
                }
                return false;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void AddForceStopUnlock()
        {
            try
            {
                // Always remove old entries first (prevents duplicates from PowerShell era)
                RemoveForceStopUnlock();

                // Use the KitLugia executable path for context menu command
                string exePath = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
                exePath = System.IO.Path.Combine(exePath, "KitLugia.GUI.exe");
                if (!System.IO.File.Exists(exePath))
                    exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(exePath)) return;

                string cmd = $"\"{exePath}\" --unlock \"%1\"";
                string icon = "%SystemRoot%\\System32\\shell32.dll,276";

                // Register for: *, Directory, Drive (3 shell locations)
                string[] shellPaths = {
                    @"Software\Classes\*\shell",
                    @"Software\Classes\Directory\shell",
                    @"Software\Classes\Drive\shell"
                };

                foreach (string shellPath in shellPaths)
                {
                    using var k = Registry.CurrentUser.CreateSubKey(shellPath + @"\forcestopunlock");
                    k?.SetValue("", "Force Stop Unlock (KitLugia)");
                    k?.SetValue("Icon", icon);
                    using var c = Registry.CurrentUser.CreateSubKey(shellPath + @"\forcestopunlock\command");
                    c?.SetValue("", cmd);
                }


                Logger.Log($"[FORCE STOP] Menu de contexto registrado: {cmd}");
            }
            catch (Exception ex) { Logger.Log($"[FORCE STOP] Erro ao registrar menu de contexto: {ex.Message}"); }
        }
        public static void RemoveForceStopUnlock()
        {
            try
            {
                // Remove ALL variants: old PowerShell name and new C# name
                string[] shellPaths = {
                    @"Software\Classes\*\shell",
                    @"Software\Classes\Directory\shell",
                    @"Software\Classes\Drive\shell"
                };
                string[] keyNames = { "forcestopunlock", "ForceStopUnlock", "force_stop_unlock" };

                foreach (string shellPath in shellPaths)
                {
                    foreach (string keyName in keyNames)
                    {
                        try
                        {
                            using var s = Registry.CurrentUser.OpenSubKey(shellPath, true);
                            s?.DeleteSubKeyTree(keyName, false);
                        }
                        catch { }
                    }
                }

            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- VS Code (Admin) context menu ---
        public static bool IsVsCodeAdded()
        {
            try
            {
                return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell\runascode", "", null) is string s && !string.IsNullOrEmpty(s);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
        public static void AddVsCode()
        {
            try
            {
                string? vsCodePath = Registry.GetValue(@"HKEY_CLASSES_ROOT\vscode\shell\open\command", "", null)?.ToString()?.Split('"')[1];
                if (string.IsNullOrEmpty(vsCodePath)) return;

                using var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\Background\shell\runascode");
                k?.SetValue("", "Abrir com VS Code (Admin)");
                k?.SetValue("HasLUAShield", "");
                k?.SetValue("Icon", $"\"{vsCodePath}\",0");
                using var c = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\Background\shell\runascode\command");
                c?.SetValue("", $"powershell.exe -Command \"Start-Process '{vsCodePath}' -ArgumentList '\"\"%V\"\"' -Verb runas\"");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
        public static void RemoveVsCode()
        {
            try
            {
                using var s = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\Background\shell", true);
                s?.DeleteSubKeyTree("runascode", false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // ============================================================
        // CONTEXT MENU SCAN, BACKUP & RESTORE
        // ============================================================

        public class ContextMenuEntry
        {
            public string Location { get; set; } = "";
            public string Name { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string Command { get; set; } = "";
            public string Extended { get; set; } = "";
            public string Icon { get; set; } = "";
            public string Type { get; set; } = "shell";
            public string Source { get; set; } = "HKCU";
            public string Clsid { get; set; } = "";
        }

        public class ContextMenuBackupData
        {
            public DateTime BackupTime { get; set; }
            public List<ContextMenuEntry> Entries { get; set; } = new();
        }

        private static readonly string[] ShellScanLocations =
        {
            @"*\shell",
            @"AllFileSystemObjects\shell",
            @"Directory\shell",
            @"Directory\Background\shell",
            @"Folder\shell",
            @"Drive\shell",
            @"DesktopBackground\shell",
            @"exefile\shell",
            @"batfile\shell",
            @"cmdfile\shell",
            @"regfile\shell",
            @"LibraryFolder\shell",
            @"LibraryFolder\Background\shell",
            @"SystemFileAssociations\image\shell",
            @"SystemFileAssociations\video\shell",
            @"SystemFileAssociations\audio\shell",
            @"SystemFileAssociations\directory.audio\shell",
            @"SystemFileAssociations\directory.video\shell",
            @"SystemFileAssociations\directory.image\shell",
        };

        private static readonly string[] ShellexScanLocations =
        {
            @"*\shellex\ContextMenuHandlers",
            @"AllFileSystemObjects\shellex\ContextMenuHandlers",
            @"Directory\shellex\ContextMenuHandlers",
            @"Directory\Background\shellex\ContextMenuHandlers",
            @"Folder\shellex\ContextMenuHandlers",
            @"Drive\shellex\ContextMenuHandlers",
            @"DesktopBackground\shellex\ContextMenuHandlers",
            @"exefile\shellex\ContextMenuHandlers",
            @"LibraryFolder\shellex\ContextMenuHandlers",
            @"SystemFileAssociations\image\shellex\ContextMenuHandlers",
            @"SystemFileAssociations\video\shellex\ContextMenuHandlers",
            @"SystemFileAssociations\audio\shellex\ContextMenuHandlers",
        };

        private static readonly string[] Wow6432ScanLocations =
        {
            @"WOW6432Node\Classes\*\shellex\ContextMenuHandlers",
            @"WOW6432Node\Classes\AllFileSystemObjects\shellex\ContextMenuHandlers",
            @"WOW6432Node\Classes\Directory\shellex\ContextMenuHandlers",
            @"WOW6432Node\Classes\Directory\Background\shellex\ContextMenuHandlers",
            @"WOW6432Node\Classes\Folder\shellex\ContextMenuHandlers",
            @"WOW6432Node\Classes\Drive\shellex\ContextMenuHandlers",
        };

        public static List<ContextMenuEntry> GetAllUserContextMenuEntries()
        {
            var entries = new List<ContextMenuEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddIfNotSeen(ContextMenuEntry entry)
            {
                var key = entry.Location + "|" + entry.Name;
                if (seen.Add(key))
                    entries.Add(entry);
            }

            // === HKCU ===
            foreach (var loc in ShellScanLocations)
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + loc);
                    if (key == null) continue;
                    foreach (var sub in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var subKey = key.OpenSubKey(sub);
                            if (subKey == null) continue;
                            var displayName = subKey.GetValue("") as string ?? sub;
                            var extended = subKey.GetValue("Extended") as string ?? "";
                            var icon = subKey.GetValue("Icon") as string ?? "";
                            string command = "";
                            using var cmdKey = subKey.OpenSubKey("command");
                            if (cmdKey != null)
                                command = cmdKey.GetValue("") as string ?? "";
                            if (string.IsNullOrEmpty(command))
                            {
                                using var delegateKey = subKey.OpenSubKey("DelegateExecute");
                                if (delegateKey != null)
                                    command = "[CLSID: " + (delegateKey.GetValue("") as string ?? "?") + "]";
                            }
                            AddIfNotSeen(new ContextMenuEntry
                            {
                                Location = loc, Name = sub, DisplayName = displayName,
                                Command = command, Extended = extended, Icon = icon,
                                Type = "shell", Source = "HKCU"
                            });
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            foreach (var loc in ShellexScanLocations)
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + loc);
                    if (key == null) continue;
                    foreach (var sub in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var subKey = key.OpenSubKey(sub);
                            if (subKey == null) continue;
                            var clsid = (subKey.GetValue("") as string ?? "").Trim();
                            var displayName = ResolveClsidName(clsid) ?? sub;
                            string dllPath = ResolveClsidDll(clsid);
                            AddIfNotSeen(new ContextMenuEntry
                            {
                                Location = loc, Name = sub, DisplayName = displayName,
                                Command = dllPath, Type = "shellex", Source = "HKCU",
                                Clsid = clsid
                            });
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            // === HKLM ===
            foreach (var loc in ShellScanLocations)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"Software\Classes\" + loc);
                    if (key == null) continue;
                    foreach (var sub in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var subKey = key.OpenSubKey(sub);
                            if (subKey == null) continue;
                            var displayName = subKey.GetValue("") as string ?? sub;
                            var extended = subKey.GetValue("Extended") as string ?? "";
                            var icon = subKey.GetValue("Icon") as string ?? "";
                            string command = "";
                            using var cmdKey = subKey.OpenSubKey("command");
                            if (cmdKey != null)
                                command = cmdKey.GetValue("") as string ?? "";
                            if (string.IsNullOrEmpty(command))
                            {
                                using var delegateKey = subKey.OpenSubKey("DelegateExecute");
                                if (delegateKey != null)
                                    command = "[CLSID: " + (delegateKey.GetValue("") as string ?? "?") + "]";
                            }
                            AddIfNotSeen(new ContextMenuEntry
                            {
                                Location = loc, Name = sub, DisplayName = displayName,
                                Command = command, Extended = extended, Icon = icon,
                                Type = "shell", Source = "HKLM"
                            });
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            foreach (var loc in ShellexScanLocations)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"Software\Classes\" + loc);
                    if (key == null) continue;
                    foreach (var sub in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var subKey = key.OpenSubKey(sub);
                            if (subKey == null) continue;
                            var clsid = (subKey.GetValue("") as string ?? "").Trim();
                            var displayName = ResolveClsidName(clsid) ?? sub;
                            string dllPath = ResolveClsidDll(clsid);
                            AddIfNotSeen(new ContextMenuEntry
                            {
                                Location = loc, Name = sub, DisplayName = displayName,
                                Command = dllPath, Type = "shellex", Source = "HKLM",
                                Clsid = clsid
                            });
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            // === WOW6432Node (HKLM only, 32-bit COM on 64-bit Windows) ===
            foreach (var loc in Wow6432ScanLocations)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"Software\Classes\" + loc);
                    if (key == null) continue;
                    foreach (var sub in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var subKey = key.OpenSubKey(sub);
                            if (subKey == null) continue;
                            var clsid = (subKey.GetValue("") as string ?? "").Trim();
                            var displayName = ResolveClsidName(clsid) ?? sub;
                            string dllPath = ResolveClsidDll(clsid);
                            AddIfNotSeen(new ContextMenuEntry
                            {
                                Location = loc, Name = sub, DisplayName = displayName,
                                Command = dllPath, Type = "shellex", Source = "HKLM",
                                Clsid = clsid
                            });
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            return entries;
        }

        private static string? ResolveClsidName(string clsid)
        {
            if (string.IsNullOrEmpty(clsid) || !clsid.StartsWith("{")) return null;
            try
            {
                var val = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\CLSID\" + clsid, "", null);
                if (val is string s && !string.IsNullOrEmpty(s))
                    return s;
                val = Registry.GetValue(@"HKEY_LOCAL_MACHINE\Software\Classes\CLSID\" + clsid, "", null);
                if (val is string s2 && !string.IsNullOrEmpty(s2))
                    return s2;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return null;
        }

        private static string ResolveClsidDll(string clsid)
        {
            if (string.IsNullOrEmpty(clsid) || !clsid.StartsWith("{")) return "";
            try
            {
                var val = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\CLSID\" + clsid + @"\InProcServer32", "", null);
                if (val is string s && !string.IsNullOrEmpty(s))
                    return s;
                val = Registry.GetValue(@"HKEY_LOCAL_MACHINE\Software\Classes\CLSID\" + clsid + @"\InProcServer32", "", null);
                if (val is string s2 && !string.IsNullOrEmpty(s2))
                    return s2;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return "";
        }

        private static string GetContextMenuBackupPath()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KitLugia");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return Path.Combine(dir, "contextmenu_backup.json");
        }

        public static bool ContextMenuBackupExists()
        {
            try { return File.Exists(GetContextMenuBackupPath()); }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static DateTime? GetContextMenuBackupTime()
        {
            try
            {
                var json = File.ReadAllText(GetContextMenuBackupPath());
                var data = JsonSerializer.Deserialize<ContextMenuBackupData>(json);
                return data?.BackupTime;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return null; }
        }

        public static void BackupUserContextMenu()
        {
            try
            {
                var entries = GetAllUserContextMenuEntries().Where(e => e.Source == "HKCU").ToList();
                var data = new ContextMenuBackupData
                {
                    BackupTime = DateTime.Now,
                    Entries = entries
                };
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(GetContextMenuBackupPath(), json);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static (bool Success, string Message) RestoreUserContextMenu()
        {
            try
            {
                var path = GetContextMenuBackupPath();
                if (!File.Exists(path))
                    return (false, "Nenhum backup encontrado.");

                var json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<ContextMenuBackupData>(json);
                if (data == null || data.Entries.Count == 0)
                    return (false, "Backup vazio ou invalido.");

                int restored = 0;
                foreach (var entry in data.Entries)
                {
                    try
                    {
                        var fullPath = @"Software\Classes\" + entry.Location;
                        using var shellKey = Registry.CurrentUser.CreateSubKey(fullPath);
                        if (shellKey == null) continue;

                        using var entryKey = shellKey.CreateSubKey(entry.Name);
                        if (entryKey == null) continue;

                        if (!string.IsNullOrEmpty(entry.DisplayName) && entry.DisplayName != entry.Name)
                            entryKey.SetValue("", entry.DisplayName);

                        if (!string.IsNullOrEmpty(entry.Extended))
                            entryKey.SetValue("Extended", entry.Extended);

                        if (!string.IsNullOrEmpty(entry.Icon))
                            entryKey.SetValue("Icon", entry.Icon);

                        if (!string.IsNullOrEmpty(entry.Command) && entry.Type == "shell")
                        {
                            using var cmdKey = entryKey.CreateSubKey("command");
                            cmdKey?.SetValue("", entry.Command);
                        }

                        restored++;
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }

                return (true, $"{restored} de {data.Entries.Count} entradas restauradas.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao restaurar: {ex.Message}");
            }
        }

        public static bool RemoveUserContextMenuEntry(string location, string name)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + location, true);
                if (key == null) return false;
                key.DeleteSubKeyTree(name, false);
                return true;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static int GetContextMenuEntryCount()
        {
            try { return GetAllUserContextMenuEntries().Count; }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return 0; }
        }

        #endregion

        #region Telemetria e Relatorios (servicos + tarefas agendadas)

        /// <summary>
        /// Servicos de telemetria/reportes seguros de desativar. DefaultStart: 2=demand, 3=auto.
        /// </summary>
        public static readonly (string Service, string Display, int DefaultStart)[] TelemetryServices =
        {
            ("DiagTrack", "Telemetria (Connected User Experiences)", 3),
            ("dmwappushservice", "WAP Push Message Routing", 2),
            ("WerSvc", "Relatorio de Erros do Windows", 2),
            ("PcaSvc", "Assistente de Compatibilidade de Programas", 3),
        };

        /// <summary>
        /// Pastas do Task Scheduler cujas tarefas sao telemetria/reportes (seguro desativar todas).
        /// </summary>
        public static readonly string[] TelemetryTaskFolders =
        {
            @"\Microsoft\Windows\Application Experience\",
            @"\Microsoft\Windows\Customer Experience Improvement Program\",
        };

        private static void RunSc(string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("sc.exe", args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
                p?.WaitForExit(15000);
            }
            catch (Exception ex)
            {
                Logger.Log($"sc.exe falhou ({args}): {ex.Message}");
            }
        }

        public static bool IsServiceDisabled(string service)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}");
                return key?.GetValue("Start") is int s && s == 4;
            }
            catch { return false; }
        }

        public static string GetServiceStartMode(string service)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}");
                return key?.GetValue("Start") is int s
                    ? s switch { 0 => "boot", 1 => "system", 2 => "demand", 3 => "auto", 4 => "disabled", _ => "demand" }
                    : "demand";
            }
            catch { return "demand"; }
        }

        /// <summary>
        /// Desativa/restaura um servico. Ao desativar, guarda o Start original em
        /// HKCU\Software\KitLugia\Tweaks\ServiceOriginals para restaurar depois.
        /// </summary>
        public static void SetServiceStartup(string service, bool disable)
        {
            try
            {
                string start;
                if (disable)
                {
                    using var save = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\KitLugia\Tweaks\ServiceOriginals");
                    save?.SetValue(service, GetServiceStartMode(service), RegistryValueKind.String);
                    start = "disabled";
                    RunSc($"config {service} start= disabled");
                    RunSc($"stop {service}");
                    Logger.Log($"Servico desativado: {service} (start= disabled)");
                }
                else
                {
                    string original = "demand";
                    using var save = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\KitLugia\Tweaks\ServiceOriginals");
                    string? stored = save?.GetValue(service) as string;
                    if (!string.IsNullOrEmpty(stored)) original = stored;
                    else
                    {
                        var def = TelemetryServices.FirstOrDefault(t => t.Service == service);
                        original = def.DefaultStart == 3 ? "auto" : "demand";
                    }
                    start = original;
                    RunSc($"config {service} start= {original}");
                    Logger.Log($"Servico restaurado: {service} (start= {original})");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao alterar servico {service}: {ex.Message}");
            }
        }

        /// <summary>
        /// Desativa/reativa TODAS as tarefas das pastas de telemetria. Retorna o numero de tarefas alteradas.
        /// </summary>
        public static int ApplyTelemetryScheduledTasks(bool disable)
        {
            int changed = 0;
            try
            {
                using (var ts = new Microsoft.Win32.TaskScheduler.TaskService())
                {
                    foreach (var folderPath in TelemetryTaskFolders)
                    {
                        var folder = ts.GetFolder(folderPath);
                        if (folder == null) continue;
                        foreach (var task in folder.Tasks)
                        {
                            try
                            {
                                if (task.Enabled == disable) continue;
                                task.Enabled = disable;
                                changed++;
                                Logger.Log($"Tarefa {(disable ? "desativada" : "ativada")}: {task.Path}");
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"Tarefa inacessivel: {task.Path} - {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao alterar tarefas de telemetria: {ex.Message}");
            }
            return changed;
        }

        /// <summary>
        /// True se TODAS as tarefas monitoradas estao desativadas (ou a pasta nao existe).
        /// </summary>
        public static bool AreTelemetryTasksDisabled()
        {
            try
            {
                using (var ts = new Microsoft.Win32.TaskScheduler.TaskService())
                {
                    foreach (var folderPath in TelemetryTaskFolders)
                    {
                        var folder = ts.GetFolder(folderPath);
                        if (folder == null) continue;
                        foreach (var task in folder.Tasks)
                            if (task.Enabled) return false;
                    }
                }
                return true;
            }
            catch { return true; }
        }


        #region Top Achados 2025 (Atlas OS / MS Learn — adicionados 24/08)

        // ───────────────────────── Helpers internos ─────────────────────────

        private static void RegSetDword(string keyPath, string valueName, int data)
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(keyPath);
            key.SetValue(valueName, data, Microsoft.Win32.RegistryValueKind.DWord);
        }

        private static int RegGetDword(string keyPath, string valueName, int defaultValue = 0)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath);
                var v = key?.GetValue(valueName);
                return v is int i ? i : (v != null && int.TryParse(v.ToString(), out var parsed) ? parsed : defaultValue);
            }
            catch { return defaultValue; }
        }

        private static void RunFsutil(string args)
        {
            try { SystemUtils.RunExternalProcess("fsutil", args, hidden: true); }
            catch (Exception ex) { Logger.LogWarning("SystemTweaks", $"fsutil {args} falhou: {ex.Message}"); }
        }

        private static bool RunWevtutilQuery(string logName)
        {
            try
            {
                var output = SystemUtils.RunExternalProcess("wevtutil", $"get-log \"{logName}\"", hidden: true);
                return !output.Contains("enabled: true", StringComparison.OrdinalIgnoreCase) &&
                       !output.Contains("/e:true", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ───────────────────────── 1. Sleep Study ─────────────────────────
        // Atlas: logs de diagnóstico de energia gravam continuamente em Modern Standby.

        public static void DisableSleepStudy()
        {
            SystemUtils.RunExternalProcess("wevtutil", "set-log \"Microsoft-Windows-SleepStudy/Diagnostic\" /e:false", hidden: true);
            SystemUtils.RunExternalProcess("wevtutil", "set-log \"Microsoft-Windows-Kernel-Processor-Power/Diagnostic\" /e:false", hidden: true);
            SystemUtils.RunExternalProcess("wevtutil", "set-log \"Microsoft-Windows-UserModePowerService/Diagnostic\" /e:false", hidden: true);
            SystemUtils.RunExternalProcess("schtasks", "/Change /TN \"\\Microsoft\\Windows\\Power Efficiency Diagnostics\\AnalyzeSystem\" /Disable", hidden: true);
            Logger.Log("[TWEAKS] Sleep Study desativado");
        }

        public static void EnableSleepStudy()
        {
            SystemUtils.RunExternalProcess("wevtutil", "set-log \"Microsoft-Windows-SleepStudy/Diagnostic\" /e:true", hidden: true);
            SystemUtils.RunExternalProcess("wevtutil", "set-log \"Microsoft-Windows-Kernel-Processor-Power/Diagnostic\" /e:true", hidden: true);
            SystemUtils.RunExternalProcess("wevtutil", "set-log \"Microsoft-Windows-UserModePowerService/Diagnostic\" /e:true", hidden: true);
            SystemUtils.RunExternalProcess("schtasks", "/Change /TN \"\\Microsoft\\Windows\\Power Efficiency Diagnostics\\AnalyzeSystem\" /Enable", hidden: true);
            Logger.Log("[TWEAKS] Sleep Study reativado");
        }

        public static bool IsSleepStudyDisabled()
        {
            // Considera desativado se a task AnalyzeSystem estiver desabilitada OU o log SleepStudy off
            try
            {
                var taskOutput = SystemUtils.RunExternalProcess("schtasks", "/Query /TN \"\\Microsoft\\Windows\\Power Efficiency Diagnostics\\AnalyzeSystem\" /FO LIST", hidden: true);
                if (taskOutput.Contains("Desabilitada") || taskOutput.Contains("Disabled")) return true;
            }
            catch { }
            return RunWevtutilQuery("Microsoft-Windows-SleepStudy/Diagnostic");
        }

        // ───────────────────────── 2. NTFS lastaccess + 8dot3 ─────────────────────────

        public static void OptimizeNtfsPerformance()
        {
            RunFsutil("behavior set disablelastaccess 1");
            RunFsutil("behavior set disable8dot3 1");
            Logger.Log("[TWEAKS] NTFS otimizado (lastaccess off, 8dot3 off)");
        }

        public static void RevertNtfsPerformance()
        {
            RunFsutil("behavior set disablelastaccess 2"); // 2 = padrão do sistema
            RunFsutil("behavior set disable8dot3 2");      // 2 = por volume (padrão)
            Logger.Log("[TWEAKS] NTFS revertido ao padrão");
        }

        public static bool IsNtfsOptimized()
        {
            try
            {
                var out1 = SystemUtils.RunExternalProcess("fsutil", "behavior query disablelastaccess", hidden: true);
                var out2 = SystemUtils.RunExternalProcess("fsutil", "behavior query disable8dot3", hidden: true);
                bool lastAccessOff = out1.Contains("= 1") || out1.Contains("= 0 ") is false && out1.Contains("(1)") || out1.TrimEnd().EndsWith("1");
                bool dot8Off = out2.Contains("= 1");
                // fsutil retorna "disablelastaccess = 1  (User Managed, Disabled)" ou similar
                lastAccessOff |= out1.Contains("Disabled");
                dot8Off |= out2.Contains("Disabled") || out2.Contains("disable8dot3 = 1");
                return lastAccessOff && dot8Off;
            }
            catch { return false; }
        }

        // ───────────────────────── 3. Fault Tolerant Heap ─────────────────────────

        public static void DisableFaultTolerantHeap()
        {
            RegSetDword(@"SOFTWARE\Microsoft\FTH", "EnableFTH", 0);
            Logger.Log("[TWEAKS] FTH desativado (requer reboot)");
        }

        public static void EnableFaultTolerantHeap() => RegSetDword(@"SOFTWARE\Microsoft\FTH", "EnableFTH", 1);

        public static bool IsFthDisabled() => RegGetDword(@"SOFTWARE\Microsoft\FTH", "EnableFTH", 1) == 0;

        // ───────────────────────── 4. Recall / Windows AI ─────────────────────────

        public static void DisableWindowsAIAnalysis()
        {
            RegSetDword(@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1);
            RegSetDword(@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecallEnablement", 0);
            // HKCU também (política pode ser por usuário em alguns builds)
            try
            {
                using var cu = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI");
                cu.SetValue("DisableAIDataAnalysis", 1, Microsoft.Win32.RegistryValueKind.DWord);
                cu.SetValue("AllowRecallEnablement", 0, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch { }
            Logger.Log("[TWEAKS] Recall/Análise IA desativado");
        }

        public static void EnableWindowsAIAnalysis()
        {
            try
            {
                Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", false);
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", false);
            }
            catch { }
        }

        public static bool IsWindowsAIAnalysisDisabled()
            => RegGetDword(@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 0) == 1;

        // ───────────────────────── 5. Delivery Optimization ─────────────────────────

        public static void SetDeliveryOptimizationMode(int mode) // 0=HTTP only, 1=LAN only, 2=Group, 3=Internet
            => RegSetDword(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", mode);

        public static void RestrictDeliveryOptimization()
        {
            // DODownloadMode: 0 = HTTP sem P2P (mais restrito), 1 = LAN apenas
            RegSetDword(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 1);
            // Cap de banda: 10 Mbps down / 5 Mbps up para background
            RegSetDword(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DOMaxBackgroundDownloadBandwidth", 1250000);   // bytes/s = 10Mbps
            RegSetDword(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DOMaxUploadBandwidth", 625000);                 // bytes/s = 5Mbps
            Logger.Log("[TWEAKS] Delivery Optimization restrito (LAN + caps)");
        }

        public static void RevertDeliveryOptimization()
        {
            try { Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", false); }
            catch { }
        }

        public static bool IsDeliveryOptimizationRestricted()
            => RegGetDword(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", -1) == 1;

        // ───────────────────────── 6. Activity Feed / Timeline ─────────────────────────

        public static void DisableActivityFeed()
        {
            RegSetDword(@"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0);
            RegSetDword(@"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0);
            RegSetDword(@"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0);
            Logger.Log("[TWEAKS] Activity Feed/Timeline desativado");
        }

        public static void EnableActivityFeed()
        {
            try { Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Policies\Microsoft\Windows\System", false); }
            catch { }
        }

        public static bool IsActivityFeedDisabled()
            => RegGetDword(@"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 1) == 0;

        // ───────────────────────── 7. App Launch Tracking ─────────────────────────

        public static void DisableAppLaunchTracking()
        {
            try
            {
                using var adv = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                adv.SetValue("Start_TrackProgs", 0, Microsoft.Win32.RegistryValueKind.DWord);
                adv.SetValue("Start_TrackEnabled", 0, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch { }
            Logger.Log("[TWEAKS] App Launch Tracking desativado");
        }

        public static void EnableAppLaunchTracking()
        {
            try
            {
                using var adv = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                adv.SetValue("Start_TrackProgs", 1, Microsoft.Win32.RegistryValueKind.DWord);
                adv.SetValue("Start_TrackEnabled", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch { }
        }

        public static bool IsAppLaunchTrackingDisabled()
        {
            try
            {
                using var adv = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                return adv?.GetValue("Start_TrackProgs") is int v && v == 0;
            }
            catch { return false; }
        }

        // ───────────────────────── 8. Online Speech Recognition ─────────────────────────

        public static void DisableOnlineSpeechRecognition()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\InputPersonalization");
                k.SetValue("RestrictImplicitInputCollection", 1, Microsoft.Win32.RegistryValueKind.DWord);
                using var s = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy");
                s.SetValue("HasAccepted", 0, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch { }
            Logger.Log("[TWEAKS] Reconhecimento de fala online desativado");
        }

        public static void EnableOnlineSpeechRecognition()
        {
            try
            {
                using var s = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy");
                s.SetValue("HasAccepted", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch { }
        }

        public static bool IsOnlineSpeechRecognitionDisabled()
        {
            try
            {
                using var s = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy");
                return s?.GetValue("HasAccepted") is int v && v == 0;
            }
            catch { return false; }
        }

        // ───────────────────────── 9. Lock Screen Camera ─────────────────────────

        public static void DisableLockScreenCamera()
        {
            RegSetDword(@"SOFTWARE\Policies\Microsoft\Windows\Personalization", "NoLockScreenCamera", 1);
            Logger.Log("[TWEAKS] Câmera da tela de bloqueio desativada");
        }

        public static void EnableLockScreenCamera() => RegSetDword(@"SOFTWARE\Policies\Microsoft\Windows\Personalization", "NoLockScreenCamera", 0);

        public static bool IsLockScreenCameraDisabled()
            => RegGetDword(@"SOFTWARE\Policies\Microsoft\Windows\Personalization", "NoLockScreenCamera", 0) == 1;

        // ───────────────────────── 10. PerfTrack ─────────────────────────

        public static void DisablePerfTrack()
        {
            RegSetDword(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PerfTrack", "Enabled", 0);
            Logger.Log("[TWEAKS] PerfTrack desativado");
        }

        public static void EnablePerfTrack() => RegSetDword(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PerfTrack", "Enabled", 1);

        public static bool IsPerfTrackDisabled()
            => RegGetDword(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PerfTrack", "Enabled", 1) == 0;

        // ───────────────────────── 11. Experimentation ─────────────────────────

        public static void DisableExperimentation()
        {
            RegSetDword(@"SOFTWARE\Policies\Microsoft\Windows\Messaging", "AllowMessageExperiments", 0); // hmm path real abaixo
            RegSetDword(@"SOFTWARE\Microsoft\PolicyManager\current\device\System", "AllowExperimentation", 0);
            RegSetDword(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "AllowExperimentation", 0);
            Logger.Log("[TWEAKS] Experimentação da MS desativada");
        }

        public static void EnableExperimentation()
        {
            try
            {
                Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\PolicyManager\current\device\System", false);
                using var pol = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer");
                if ((int?)pol.GetValue("AllowExperimentation") == 0) pol.DeleteValue("AllowExperimentation");
            }
            catch { }
        }

        public static bool IsExperimentationDisabled()
            => RegGetDword(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "AllowExperimentation", 1) == 0;

        // ───────────────────────── 12. RSOP Logging ─────────────────────────

        public static void DisableRsopLogging()
        {
            RegSetDword(@"SOFTWARE\Policies\Microsoft\Windows\System", "RsopLoggingEnabled", 0);
            Logger.Log("[TWEAKS] RSOP logging desativado");
        }

        public static void EnableRsopLogging() => RegSetDword(@"SOFTWARE\Policies\Microsoft\Windows\System", "RsopLoggingEnabled", 1);

        public static bool IsRsopLoggingDisabled()
            => RegGetDword(@"SOFTWARE\Policies\Microsoft\Windows\System", "RsopLoggingEnabled", 1) == 0;

        // ───────────────────────── 13. Windows Media Player telemetry ─────────────────────────

        public static void DisableWmpTelemetry()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\MediaPlayer\Preferences");
                k.SetValue("SendUserGUID", 0, Microsoft.Win32.RegistryValueKind.DWord);
                k.SetValue("UsageTracking", 0, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch { }
            Logger.Log("[TWEAKS] Telemetria WMP desativada");
        }

        public static void EnableWmpTelemetry()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\MediaPlayer\Preferences");
                k.SetValue("SendUserGUID", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch { }
        }

        public static bool IsWmpTelemetryDisabled()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\MediaPlayer\Preferences");
                return k?.GetValue("SendUserGUID") is int v && v == 0;
            }
            catch { return false; }
        }

        // ───────────────────────── 14. Automatic Maintenance ─────────────────────────

        public static void ConfigureAutomaticMaintenance()
        {
            // Impede o despertar automático e limita execução fora de idle
            RegSetDword(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance", "WakeUp", 0);
            RegSetDword(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance", "RandomDelay", 60);      // 60 min de janela aleatória
            RegSetDword(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance", "Activation Border", 120); // borda de ativação 120min
            Logger.Log("[TWEAKS] Manutenção automática configurada (sem acordar o PC)");
        }

        public static void RevertAutomaticMaintenance()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance", writable: true);
                if (k != null)
                {
                    foreach (var name in new[] { "WakeUp", "RandomDelay", "Activation Border" })
                        try { k.DeleteValue(name); } catch { }
                }
            }
            catch { }
        }

        public static bool IsAutomaticMaintenanceConfigured()
            => RegGetDword(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance", "WakeUp", 1) == 0;

        // ───────────────────────── 15. Storage Sense ─────────────────────────

        public static void DisableStorageSense()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy");
                k.SetValue("01", 0, Microsoft.Win32.RegistryValueKind.DWord); // master switch
            }
            catch { }
            Logger.Log("[TWEAKS] Storage Sense desativado");
        }

        public static void EnableStorageSense()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy");
                k.SetValue("01", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch { }
        }

        public static bool IsStorageSenseDisabled()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy");
                return k?.GetValue("01") is int v && v == 0;
            }
            catch { return false; }
        }

        // ───────────────────────── 16. Reserved Storage ─────────────────────────

        public static bool IsReservedStorageSupported()
        {
            // Só existe no Win10 1903+
            try
            {
                var output = SystemUtils.RunExternalProcess("dism", "/Online /Get-ReservedStorageState", hidden: true);
                return !output.Contains("7968", StringComparison.Ordinal) || output.Contains("Reserved"); // tem estado reservado?
            }
            catch { return false; }
        }

        public static (bool Success, string Message) DisableReservedStorage()
        {
            try
            {
                var output = SystemUtils.RunExternalProcess("dism", "/Online /Set-ReservedStorageState /State:Disabled", hidden: true);
                bool ok = !output.Contains("erro", StringComparison.OrdinalIgnoreCase) &&
                          !output.Contains("failed", StringComparison.OrdinalIgnoreCase) &&
                          !output.Contains("[TIMEOUT]");
                Logger.Log($"[TWEAKS] Reserved Storage disable → {(ok ? "OK" : "falhou")}");
                return (ok, ok ? "Reserved Storage liberado (~7GB recuperados)." : "Falha: " + output.Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim());
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public static (bool Success, string Message) EnableReservedStorage()
        {
            try
            {
                var output = SystemUtils.RunExternalProcess("dism", "/Online /Set-ReservedStorageState /State:Enabled", hidden: true);
                bool ok = !output.Contains("erro", StringComparison.OrdinalIgnoreCase) && !output.Contains("failed", StringComparison.OrdinalIgnoreCase);
                return (ok, ok ? "Reserved Storage reativado." : "Falha ao reativar.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static bool IsReservedStorageDisabled()
        {
            try
            {
                var output = SystemUtils.RunExternalProcess("dism", "/Online /Get-ReservedStorageState", hidden: true);
                return output.Contains("Disabled", StringComparison.OrdinalIgnoreCase) ||
                       output.Contains("Desabilitado", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        #endregion

        #endregion
    }
}
