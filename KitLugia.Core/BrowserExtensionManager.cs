using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static class BrowserExtensionManager
    {
        private static readonly string ExtTempDir = Path.Combine(Path.GetTempPath(), "KitLugia", "BrowserExtensions");

        public static List<BrowserDetected> DetectInstalledBrowsers()
        {
            var results = new List<BrowserDetected>();
            foreach (var def in GetBrowserDefinitions())
            {
                bool exeFound = File.Exists(def.ExecutablePath);
                bool hasUserData = Directory.Exists(def.UserDataDir);
                if (exeFound || hasUserData || def.Engine == BrowserEngine.Trident)
                    results.Add(new BrowserDetected(def.Name, def.Engine, def.ExecutablePath, def.UserDataDir, hasUserData));
            }
            return results;
        }

        public static List<ExtensionInfo> ScanExtensions(string browserName)
        {
            var def = GetBrowserDefinitions().Find(d => d.Name.Equals(browserName, StringComparison.OrdinalIgnoreCase));
            if (def == null) return new();

            if (def.Engine == BrowserEngine.Chromium)
                return ScanChromiumExtensions(def.UserDataDir, def.Name);
            if (def.Engine == BrowserEngine.Gecko)
                return ScanFirefoxExtensions(def.UserDataDir, def.Name);
            return new();
        }

        public static bool ExportExtensions(string browserName, string targetDir)
        {
            var def = GetBrowserDefinitions().Find(d => d.Name.Equals(browserName, StringComparison.OrdinalIgnoreCase));
            if (def == null) return false;
            try
            {
                string extRoot = Sanitize(browserName);
                Directory.CreateDirectory(Path.Combine(targetDir, extRoot));

                if (def.Engine == BrowserEngine.Chromium)
                {
                    string src = Path.Combine(def.UserDataDir, "Default", "Extensions");
                    if (!Directory.Exists(src)) return false;
                    CopyDir(src, Path.Combine(targetDir, extRoot, "Extensions"));
                }
                else if (def.Engine == BrowserEngine.Gecko)
                {
                    string profilesDir = Path.Combine(def.UserDataDir, "Profiles");
                    if (!Directory.Exists(profilesDir)) return false;
                    foreach (var prof in Directory.EnumerateDirectories(profilesDir))
                    {
                        string src = Path.Combine(prof, "extensions");
                        if (Directory.Exists(src))
                            CopyDir(src, Path.Combine(targetDir, extRoot, "Profiles", Path.GetFileName(prof), "extensions"));
                    }
                }

                var browsers = DetectInstalledBrowsers().Where(b => b.Name == browserName).ToList();
                File.WriteAllText(Path.Combine(targetDir, extRoot, "_metadata.json"), JsonSerializer.Serialize(new
                {
                    SourceBrowser = browserName,
                    ExportDate = DateTime.Now,
                    ExtensionCount = CountExportedExtensions(Path.Combine(targetDir, extRoot)),
                    Engine = def.Engine.ToString()
                }));
                return true;
            }
            catch { return false; }
        }

        public static bool ImportExtensions(string sourceDir, string browserName)
        {
            var def = GetBrowserDefinitions().Find(d => d.Name.Equals(browserName, StringComparison.OrdinalIgnoreCase));
            if (def == null) return false;

            if (IsBrowserRunning(def.Name)) return false;

            try
            {
                if (def.Engine == BrowserEngine.Chromium)
                {
                    string extRoot = Path.Combine(sourceDir, Sanitize(def.Name), "Extensions");
                    if (!Directory.Exists(extRoot)) return false;
                    string target = Path.Combine(def.UserDataDir, "Default", "Extensions");
                    Directory.CreateDirectory(target);
                    foreach (var d2 in Directory.EnumerateDirectories(extRoot))
                    {
                        string id = Path.GetFileName(d2);
                        string dest = Path.Combine(target, id);
                        if (!Directory.Exists(dest))
                            CopyDir(d2, dest);
                    }
                    return true;
                }

                if (def.Engine == BrowserEngine.Gecko)
                {
                    string profilesDir = Path.Combine(def.UserDataDir, "Profiles");
                    if (!Directory.Exists(profilesDir)) return false;
                    var profile = Directory.EnumerateDirectories(profilesDir).FirstOrDefault();
                    if (profile == null) return false;

                    string srcRoot = Path.Combine(sourceDir, Sanitize(def.Name), "Profiles");
                    if (!Directory.Exists(srcRoot)) return false;
                    foreach (var profBackup in Directory.EnumerateDirectories(srcRoot))
                    {
                        string src = Path.Combine(profBackup, "extensions");
                        if (!Directory.Exists(src)) continue;
                        string target = Path.Combine(profile, "extensions");
                        Directory.CreateDirectory(target);
                        foreach (var xpi in Directory.EnumerateFiles(src, "*.xpi"))
                        {
                            string dest = Path.Combine(target, Path.GetFileName(xpi));
                            if (!File.Exists(dest))
                                File.Copy(xpi, dest);
                        }
                    }
                    return true;
                }

                return false;
            }
            catch { return false; }
        }

        public static bool TransferExtensions(string sourceBrowser, string targetBrowser, IProgress<string>? progress = null)
        {
            string temp = Path.Combine(ExtTempDir, $"{Sanitize(sourceBrowser)}_to_{Sanitize(targetBrowser)}_{DateTime.Now:yyyyMMddHHmmss}");
            try
            {
                Directory.CreateDirectory(temp);
                progress?.Report($"Exportando extensões de {sourceBrowser}...");
                if (!ExportExtensions(sourceBrowser, temp)) return false;

                string srcName = Sanitize(sourceBrowser);
                string tgtName = Sanitize(targetBrowser);
                if (!srcName.Equals(tgtName, StringComparison.OrdinalIgnoreCase))
                {
                    string srcExt = Path.Combine(temp, srcName, "Extensions");
                    string tgtExt = Path.Combine(temp, tgtName, "Extensions");
                    if (Directory.Exists(srcExt) && !Directory.Exists(tgtExt))
                    {
                        Directory.CreateDirectory(Path.Combine(temp, tgtName));
                        CopyDir(srcExt, tgtExt);
                    }
                }

                progress?.Report($"Importando para {targetBrowser}...");
                if (!ImportExtensions(temp, targetBrowser)) return false;

                progress?.Report($"Registrando extensões no {targetBrowser}...");
                RegisterExtensionsViaCdpPipe(targetBrowser);

                return true;
            }
            catch { return false; }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }
        }

        public static void RegisterExtensionsViaCdpPipe(string browserName)
        {
            var def = GetBrowserDefinitions().Find(d => d.Name.Equals(browserName, StringComparison.OrdinalIgnoreCase));
            if (def == null || string.IsNullOrEmpty(def.ExecutablePath)) return;

            string extDir = Path.Combine(def.UserDataDir, "Default", "Extensions");
            if (!Directory.Exists(extDir)) return;

            var versionDirs = new List<string>();
            foreach (var idDir in Directory.EnumerateDirectories(extDir))
            {
                string? versionDir = Directory.EnumerateDirectories(idDir)
                    .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (versionDir != null && File.Exists(Path.Combine(versionDir, "manifest.json")))
                    versionDirs.Add(versionDir);
            }
            if (versionDirs.Count == 0) return;

            string procName = Path.GetFileNameWithoutExtension(def.ExecutablePath);

            try
            {
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(3000))
                        p.Kill();
                }
            }
            catch { }

            Thread.Sleep(1000);

            var psi = new ProcessStartInfo(def.ExecutablePath)
            {
                Arguments = "--remote-debugging-pipe --enable-unsafe-extension-debugging --no-first-run --no-default-browser-check",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return;

            try
            {
                var stdin = proc.StandardInput;
                var stdout = proc.StandardOutput;
                stdout.BaseStream.ReadTimeout = 15000;

                // Wait for Chrome to send the first message (Target.attachedToTarget)
                ReadUntilNull(stdout);

                int cmdId = 1;
                foreach (var versionDir in versionDirs)
                {
                    string req = $"{{\"id\":{cmdId},\"method\":\"Extensions.loadUnpacked\",\"params\":{{\"path\":\"{EscapeJson(versionDir)}\"}}}}";
                    stdin.Write(req);
                    stdin.Write('\0');
                    stdin.Flush();

                    string resp = ReadUntilId(stdout, cmdId);
                    if (resp.Contains("\"error\""))
                    {
                        try
                        {
                            using var errDoc = JsonDocument.Parse(resp);
                            string? errMsg = errDoc.RootElement.GetProperty("error").GetProperty("message").GetString();
                            System.Diagnostics.Debug.WriteLine($"CDP error for {versionDir}: {errMsg}");
                        }
                        catch { }
                    }

                    cmdId++;
                }
            }
            catch { }
            finally
            {
                try { proc.CloseMainWindow(); if (!proc.WaitForExit(5000)) proc.Kill(); } catch { }
            }
        }

        private static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string ReadUntilNull(StreamReader reader)
        {
            var buf = new System.Text.StringBuilder();
            while (true)
            {
                int ch = reader.Read();
                if (ch == -1 || ch == 0) return buf.ToString();
                buf.Append((char)ch);
            }
        }

        private static string ReadUntilId(StreamReader reader, int id)
        {
            var buf = new System.Text.StringBuilder();
            while (true)
            {
                int ch = reader.Read();
                if (ch == -1) return buf.ToString();
                if (ch == 0)
                {
                    string msg = buf.ToString();
                    if (msg.Contains($"\"id\":{id}")) return msg;
                    buf.Clear();
                }
                else
                {
                    buf.Append((char)ch);
                }
            }
        }

        public static bool IsBrowserRunning(string browserName)
        {
            var def = GetBrowserDefinitions().Find(d => d.Name.Equals(browserName, StringComparison.OrdinalIgnoreCase));
            if (def == null || string.IsNullOrEmpty(def.ExecutablePath)) return false;
            try
            {
                string procName = Path.GetFileNameWithoutExtension(def.ExecutablePath);
                return System.Diagnostics.Process.GetProcessesByName(procName).Length > 0;
            }
            catch { return false; }
        }

        public static List<ExportBackupInfo> ListBackups()
        {
            var results = new List<ExportBackupInfo>();
            if (!Directory.Exists(ExtTempDir)) return results;
            foreach (var dir in Directory.EnumerateDirectories(ExtTempDir))
            {
                string name = Path.GetFileName(dir);
                if (name.Contains("_to_"))
                {
                    var parts = name.Split(new[] { "_to_" }, 2, StringSplitOptions.None);
                    string src = parts[0].Replace('_', ' ');
                    string tgt = parts.Length > 1 ? parts[1].Replace('_', ' ') : "";
                    string datePart = tgt.Contains('_') ? tgt.Split('_').Last() : "";
                    results.Add(new ExportBackupInfo(dir, src, tgt, "Transferência", datePart));
                }
            }
            return results;
        }

        private static int CountExportedExtensions(string root)
        {
            int count = 0;
            string extDir = Path.Combine(root, "Extensions");
            if (Directory.Exists(extDir))
                count += Directory.EnumerateDirectories(extDir).Count();
            string profDir = Path.Combine(root, "Profiles");
            if (Directory.Exists(profDir))
            {
                foreach (var p in Directory.EnumerateDirectories(profDir))
                {
                    string e = Path.Combine(p, "extensions");
                    if (Directory.Exists(e))
                        count += Directory.EnumerateFiles(e, "*.xpi").Count();
                }
            }
            return count;
        }

        private static List<ExtensionInfo> ScanChromiumExtensions(string userDataDir, string browserName)
        {
            var results = new List<ExtensionInfo>();
            string extDir = Path.Combine(userDataDir, "Default", "Extensions");
            if (!Directory.Exists(extDir)) return results;

            foreach (var idDir in Directory.EnumerateDirectories(extDir))
            {
                string extId = Path.GetFileName(idDir);
                string? versionDir = Directory.EnumerateDirectories(idDir)
                    .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (versionDir == null) continue;

                string manifest = Path.Combine(versionDir, "manifest.json");
                if (!File.Exists(manifest)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                    var root = doc.RootElement;

                    string name = GetString(root, "name") ?? extId;
                    if (name.StartsWith("__MSG_")) name = ResolveLocale(versionDir, name);

                    string version = GetString(root, "version") ?? "?";
                    string desc = GetString(root, "description") ?? "";
                    if (desc.StartsWith("__MSG_")) desc = ResolveLocale(versionDir, desc);

                    long size = DirSize(versionDir);
                    string? iconPath = FindExtensionIcon(versionDir, root);
                    results.Add(new ExtensionInfo(extId, name, version, desc, versionDir, browserName, "Chromium", size) { IconPath = iconPath });
                }
                catch { }
            }
            return results;
        }

        private static string? FindExtensionIcon(string versionDir, JsonElement manifest)
        {
            if (manifest.TryGetProperty("icons", out var icons))
            {
                int bestSize = 0;
                string? bestPath = null;
                foreach (var icon in icons.EnumerateObject())
                {
                    if (int.TryParse(icon.Name, out int sz) && sz > bestSize)
                    {
                        bestSize = sz;
                        bestPath = icon.Value.GetString();
                    }
                }
                if (bestPath != null && !string.IsNullOrEmpty(bestPath))
                {
                    string full = Path.Combine(versionDir, bestPath.Replace('/', '\\'));
                    if (File.Exists(full)) return full;
                }
            }

            foreach (var name in new[] { "icon.png", "icon128.png", "icon_128.png", "icon-128.png", "icon48.png", "icon_48.png", "icon-48.png", "icon16.png", "icon_16.png" })
            {
                string full = Path.Combine(versionDir, name);
                if (File.Exists(full)) return full;
            }

            return null;
        }

        private static List<ExtensionInfo> ScanFirefoxExtensions(string firefoxDataDir, string browserName)
        {
            var results = new List<ExtensionInfo>();
            string profilesDir = Path.Combine(firefoxDataDir, "Profiles");
            if (!Directory.Exists(profilesDir)) return results;

            foreach (var prof in Directory.EnumerateDirectories(profilesDir))
            {
                string extDir = Path.Combine(prof, "extensions");
                if (!Directory.Exists(extDir)) continue;

                foreach (var f in Directory.EnumerateFiles(extDir, "*.xpi"))
                {
                    var fi = new FileInfo(f);
                    string extId = Path.GetFileNameWithoutExtension(f);
                    results.Add(new ExtensionInfo(extId, extId, "?",
                        $"Extensão Firefox | {FormatSize(fi.Length)}", f, browserName, "Gecko", fi.Length));
                }
            }
            return results;
        }

        private static string ResolveLocale(string versionDir, string msgKey)
        {
            if (!msgKey.StartsWith("__MSG_") || !msgKey.EndsWith("__")) return msgKey;
            string key = msgKey[6..^2];

            string[] paths =
            {
                Path.Combine(versionDir, "_locales", "en", "messages.json"),
                Path.Combine(versionDir, "_locales", "en_US", "messages.json"),
                Path.Combine(versionDir, "_locales", "pt_BR", "messages.json"),
            };

            foreach (var p in paths)
            {
                if (!File.Exists(p)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(p));
                    if (doc.RootElement.TryGetProperty(key, out var prop) &&
                        prop.TryGetProperty("message", out var msg))
                        return msg.GetString() ?? key;
                }
                catch { }
            }

            string localesDir = Path.Combine(versionDir, "_locales");
            if (Directory.Exists(localesDir))
            {
                foreach (var localeDir in Directory.EnumerateDirectories(localesDir))
                {
                    string p = Path.Combine(localeDir, "messages.json");
                    if (!File.Exists(p)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(p));
                        if (doc.RootElement.TryGetProperty(key, out var prop) &&
                            prop.TryGetProperty("message", out var msg))
                            return msg.GetString() ?? key;
                    }
                    catch { }
                }
            }
            return key;
        }

        private static string? GetString(JsonElement el, string prop)
        {
            return el.TryGetProperty(prop, out var v) ? v.GetString() : null;
        }

        private static long DirSize(string dir)
        {
            try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
            catch { return 0; }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:N1} KB";
            return $"{bytes / (1024.0 * 1024.0):N1} MB";
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var d in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(d.Replace(src, dst));
            foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                File.Copy(f, f.Replace(src, dst), true);
        }

        private static string Sanitize(string s)
        {
            var inv = Path.GetInvalidFileNameChars();
            return string.Concat(s.Where(c => !inv.Contains(c))).TrimEnd('.').Trim();
        }

        private static List<BrowserDef> GetBrowserDefinitions()
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pfX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string lApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            return new()
            {
                new("Google Chrome", BrowserEngine.Chromium,
                    FirstExe(pf, @"Google\Chrome\Application\chrome.exe", pfX86, @"Google\Chrome\Application\chrome.exe"),
                    Path.Combine(lApp, @"Google\Chrome\User Data")),
                new("Microsoft Edge", BrowserEngine.Chromium,
                    FirstExe(pfX86, @"Microsoft\Edge\Application\msedge.exe", pf, @"Microsoft\Edge\Application\msedge.exe"),
                    Path.Combine(lApp, @"Microsoft\Edge\User Data")),
                new("Brave", BrowserEngine.Chromium,
                    FirstExe(pf, @"BraveSoftware\Brave-Browser\Application\brave.exe", pfX86, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                    Path.Combine(lApp, @"BraveSoftware\Brave-Browser\User Data")),
                new("Vivaldi", BrowserEngine.Chromium,
                    FirstExe(pf, @"Vivaldi\Application\vivaldi.exe"),
                    Path.Combine(lApp, @"Vivaldi\User Data")),
                new("Opera", BrowserEngine.Chromium,
                    FirstExe(pf, @"Opera\launcher.exe", pfX86, @"Opera\launcher.exe"),
                    Path.Combine(app, @"Opera Software\Opera Stable")),
                new("Opera GX", BrowserEngine.Chromium,
                    FirstExe(lApp, @"Programs\Opera GX\launcher.exe"),
                    Path.Combine(app, @"Opera Software\Opera GX Stable")),
                new("Thorium", BrowserEngine.Chromium,
                    FirstExe(pf, @"Thorium\Application\thorium.exe", pfX86, @"Thorium\Application\thorium.exe"),
                    Path.Combine(lApp, @"Thorium\User Data")),
                new("Firefox", BrowserEngine.Gecko,
                    FirstExe(pf, @"Mozilla Firefox\firefox.exe", pfX86, @"Mozilla Firefox\firefox.exe"),
                    Path.Combine(app, @"Mozilla\Firefox")),
            };
        }

        private static string FirstExe(params string[] paths)
        {
            for (int i = 0; i < paths.Length - 1; i += 2)
            {
                string full = Path.Combine(paths[i], paths[i + 1]);
                if (File.Exists(full)) return full;
            }
            return "";
        }
    }

    public enum BrowserEngine { Chromium, Gecko, Trident }

    public record BrowserDetected(string Name, BrowserEngine Engine, string ExecutablePath, string UserDataDir, bool HasUserData);

    public record ExtensionInfo(string Id, string Name, string Version, string Description, string SourcePath, string SourceBrowser, string Engine, long SizeBytes)
    {
        public string SizeFormatted => SizeBytes < 1024 ? $"{SizeBytes} B" :
            SizeBytes < 1024 * 1024 ? $"{SizeBytes / 1024.0:N1} KB" :
            $"{SizeBytes / (1024.0 * 1024.0):N1} MB";
        public string? IconPath { get; init; }
    }

    public record ExportBackupInfo(string BackupPath, string SourceBrowser, string TargetBrowser, string Type, string DateString);

    internal record BrowserDef(string Name, BrowserEngine Engine, string ExecutablePath, string UserDataDir);
}
