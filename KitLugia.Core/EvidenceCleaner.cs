using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace KitLugia.Core
{
    public static class EvidenceCleaner
    {
        public static string[] EvidenceCategories = {
            "Recent Documents (MRU)",
            "RunMRU (Executar)",
            "Typed URLs (Internet Explorer/Edge)",
            "UserAssist (Execução de programas)",
            "BagMRU (Histórico de pastas)",
            "Jump Lists",
            "Windows Timeline",
            "Clipboard History",
            "Prefetch",
            "Office MRU",
            "Visual Studio MRU"
        };

        public static Dictionary<string, (int items, List<string> errors)> CleanAll()
        {
            var results = new Dictionary<string, (int, List<string>)>();
            results["Recent Documents (MRU)"] = CleanRecentDocs();
            results["RunMRU (Executar)"] = CleanRunMRU();
            results["Typed URLs (Internet Explorer/Edge)"] = CleanTypedURLs();
            results["UserAssist (Execução de programas)"] = CleanUserAssist();
            results["BagMRU (Histórico de pastas)"] = CleanBagMRU();
            results["Jump Lists"] = CleanJumpLists();
            results["Windows Timeline"] = CleanWindowsTimeline();
            results["Clipboard History"] = CleanClipboardHistory();
            results["Prefetch"] = CleanPrefetch();
            results["Office MRU"] = CleanOfficeMRU();
            results["Visual Studio MRU"] = CleanVisualStudioMRU();
            return results;
        }

        public static (int items, List<string> errors) CleanRecentDocs()
        {
            int count = 0;
            var errors = new List<string>();
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs", true);
                if (key != null)
                {
                    var names = key.GetValueNames();
                    foreach (var n in names)
                    {
                        try { key.DeleteValue(n); count++; } catch (Exception ex) { errors.Add($"RecentDocs value: {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex) { errors.Add($"RecentDocs: {ex.Message}"); }

            // Limpa também a pasta Recent
            try
            {
                string recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                if (Directory.Exists(recent))
                {
                    foreach (var f in Directory.GetFiles(recent)) { try { File.Delete(f); count++; } catch { } }
                }
            }
            catch { }
            return (count, errors);
        }

        public static (int items, List<string> errors) CleanRunMRU()
        {
            int count = 0;
            var errors = new List<string>();
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU", true);
                if (key != null)
                {
                    var names = key.GetValueNames();
                    foreach (var n in names)
                    {
                        if (n != "MRUListEx")
                        {
                            try { key.DeleteValue(n); count++; } catch (Exception ex) { errors.Add($"RunMRU: {ex.Message}"); }
                        }
                    }
                }
            }
            catch (Exception ex) { errors.Add($"RunMRU key: {ex.Message}"); }
            return (count, errors);
        }

        public static (int items, List<string> errors) CleanTypedURLs()
        {
            int count = 0;
            var errors = new List<string>();
            string[] paths = {
                @"Software\Microsoft\Internet Explorer\TypedURLs",
                @"Software\Microsoft\Internet Explorer\TypedURLsTime",
                @"Software\Microsoft\Edge\TypedURLs",
                @"Software\Microsoft\Edge\TypedURLsTime"
            };
            foreach (var path in paths)
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(path, true);
                    if (key != null)
                    {
                        var names = key.GetValueNames();
                        foreach (var n in names) { try { key.DeleteValue(n); count++; } catch { } }
                    }
                }
                catch { }
            }
            return (count, errors);
        }

        public static (int items, List<string> errors) CleanUserAssist()
        {
            int count = 0;
            var errors = new List<string>();
            try
            {
                string[] guidPaths = {
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{CEBFF5CD-ACE2-4F4F-9178-9926F417A6DB}\Count",
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{F4E57C4B-2036-45F0-A9AB-443BCFE33D9F}\Count",
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{75048700-EF1F-11D0-9888-006097DEACF9}\Count"
                };
                foreach (var guid in guidPaths)
                {
                    using var key = Registry.CurrentUser.OpenSubKey(guid, true);
                    if (key != null)
                    {
                        var names = key.GetValueNames();
                        foreach (var n in names)
                        {
                            try { key.DeleteValue(n); count++; } catch { }
                        }
                    }
                }
            }
            catch (Exception ex) { errors.Add($"UserAssist: {ex.Message}"); }
            return (count, errors);
        }

        public static (int items, List<string> errors) CleanBagMRU()
        {
            int count = 0;
            var errors = new List<string>();
            string[] mruPaths = {
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\BagMRU",
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags",
                @"Software\Microsoft\Windows\Shell\BagMRU",
                @"Software\Microsoft\Windows\Shell\Bags"
            };
            foreach (var path in mruPaths)
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(path, true);
                    if (key != null)
                    {
                        foreach (var sub in key.GetSubKeyNames())
                        {
                            try { key.DeleteSubKeyTree(sub); count++; } catch { }
                        }
                        var names = key.GetValueNames();
                        foreach (var n in names) { try { key.DeleteValue(n); } catch { } }
                    }
                }
                catch { }
            }
            return (count, errors);
        }

        public static (int items, List<string> errors) CleanJumpLists()
        {
            int count = 0;
            var errors = new List<string>();
            try
            {
                string jlDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Recent\AutomaticDestinations");
                if (Directory.Exists(jlDir))
                {
                    foreach (var f in Directory.GetFiles(jlDir, "*.automaticDestinations-ms"))
                    {
                        try { File.Delete(f); count++; } catch (Exception ex) { errors.Add($"JumpList: {ex.Message}"); }
                    }
                }
                string customDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Recent\CustomDestinations");
                if (Directory.Exists(customDir))
                {
                    foreach (var f in Directory.GetFiles(customDir, "*.customDestinations-ms"))
                    {
                        try { File.Delete(f); count++; } catch { }
                    }
                }
            }
            catch (Exception ex) { errors.Add($"JumpLists dir: {ex.Message}"); }
            return (count, errors);
        }

        public static (int items, List<string> errors) CleanWindowsTimeline()
        {
            int count = 0;
            var errors = new List<string>();
            try
            {
                string timelineDb = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"ConnectedDevicesPlatform\L.activities\ActivitiesCache.db");
                if (File.Exists(timelineDb))
                {
                    File.Delete(timelineDb); count++;
                }
                // Tenta também outros perfis
                string cdpDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ConnectedDevicesPlatform");
                if (Directory.Exists(cdpDir))
                {
                    foreach (var profileDir in Directory.GetDirectories(cdpDir))
                    {
                        foreach (var db in Directory.GetFiles(profileDir, "*.db"))
                        {
                            try { File.Delete(db); count++; } catch { }
                        }
                    }
                }
            }
            catch (Exception ex) { errors.Add($"Timeline: {ex.Message}"); }
            return (count, errors);
        }

        public static (int items, List<string> errors) CleanClipboardHistory()
        {
            int count = 0;
            var errors = new List<string>();
            try
            {
                string clipDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Microsoft\Windows\Clipboard");
                if (Directory.Exists(clipDir))
                {
                    foreach (var f in Directory.GetFiles(clipDir))
                    {
                        try { File.Delete(f); count++; } catch { }
                    }
                }
            }
            catch (Exception ex) { errors.Add($"Clipboard: {ex.Message}"); }
            return (count, errors);
        }

        public static (int items, List<string> errors) CleanPrefetch()
        {
            int count = 0;
            var errors = new List<string>();
            try
            {
                string prefetch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
                if (Directory.Exists(prefetch))
                {
                    foreach (var f in Directory.GetFiles(prefetch, "*.pf"))
                    {
                        try { File.Delete(f); count++; } catch { }
                    }
                }
            }
            catch (Exception ex) { errors.Add($"Prefetch: {ex.Message}"); }
            return (count, errors);
        }

        public static (int items, List<string> errors) CleanOfficeMRU()
        {
            int count = 0;
            var errors = new List<string>();
            try
            {
                using var officeKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office", true);
                if (officeKey != null)
                {
                    foreach (var ver in officeKey.GetSubKeyNames())
                    {
                        try
                        {
                            using var mruKey = officeKey.OpenSubKey($@"{ver}\{GetOfficeAppMRUPrefix(ver)}\MRU", true);
                            if (mruKey != null)
                            {
                                foreach (var sub in mruKey.GetSubKeyNames())
                                {
                                    try { mruKey.DeleteSubKeyTree(sub); count++; } catch { }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { errors.Add($"Office MRU: {ex.Message}"); }
            return (count, errors);
        }

        private static string GetOfficeAppMRUPrefix(string version)
        {
            return "Word"; // Simplificado - cobre Word MRU como exemplo
        }

        public static (int items, List<string> errors) CleanVisualStudioMRU()
        {
            int count = 0;
            var errors = new List<string>();
            try
            {
                string[] vsVersions = { "VisualStudio", "VisualStudio_D14", "VisualStudio_D15", "VisualStudio_D16", "VisualStudio_D17" };
                foreach (var vs in vsVersions)
                {
                    try
                    {
                        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\{vs}\MRUList", true);
                        if (key != null)
                        {
                            foreach (var sub in key.GetSubKeyNames())
                            {
                                try { key.DeleteSubKeyTree(sub); count++; } catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { errors.Add($"VS MRU: {ex.Message}"); }
            return (count, errors);
        }
    }
}
