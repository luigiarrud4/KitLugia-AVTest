using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace KitLugia.Updater;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length < 3)
        {
            WriteError("Usage: KitLugia.Updater <zipPath> <mainPid> <mainExePath> [sha256]");
            return;
        }

        string zipPath = args[0];
        int mainPid = int.Parse(args[1]);
        string mainExePath = args[2];
        string expectedHash = args.Length > 3 ? args[3] : null;
        string appDir = Path.GetDirectoryName(mainExePath);
        string logPath = Path.Combine(appDir, "update.log");

        try
        {
            Log(logPath, "KitLugia Updater started");
            Log(logPath, $"Zip: {zipPath}, PID: {mainPid}, Target: {mainExePath}");

            // Verify hash if provided
            if (!string.IsNullOrEmpty(expectedHash))
            {
                Log(logPath, "Verifying SHA256...");
                string actualHash = ComputeSha256(zipPath);
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    WriteError($"Hash mismatch. Expected: {expectedHash}, Actual: {actualHash}");
                    Log(logPath, $"HASH MISMATCH. Expected: {expectedHash}, Actual: {actualHash}");
                    return;
                }
                Log(logPath, "SHA256 verified OK");
            }

            // Wait for main process to exit
            Log(logPath, "Waiting for main process to exit...");
            try
            {
                var mainProcess = Process.GetProcessById(mainPid);
                if (!mainProcess.WaitForExit(30000))
                {
                    mainProcess.Kill();
                    mainProcess.WaitForExit(5000);
                }
            }
            catch (ArgumentException) { }

            // Give OS time to release file handles
            Thread.Sleep(1000);

            // Extract zip to temp
            string extractDir = Path.Combine(Path.GetTempPath(), $"KitLugia_Update_{Guid.NewGuid():N}");
            Directory.CreateDirectory(extractDir);
            Log(logPath, $"Extracting to {extractDir}...");
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
            Log(logPath, "Extraction done.");

            // Copy files to target directory
            Log(logPath, $"Copying files to {appDir}...");
            CopyDirectory(extractDir, appDir, logPath);
            Log(logPath, "File copy complete.");

            // Cleanup
            try { Directory.Delete(extractDir, true); } catch { }
            try { File.Delete(zipPath); } catch { }

            Log(logPath, "Restarting application...");
            Process.Start(new ProcessStartInfo
            {
                FileName = mainExePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            WriteError($"Update failed: {ex.Message}");
            Log(logPath, $"FATAL: {ex}");
            try
            {
                Process.Start("https://github.com/luigiarrud4/KitLugia/releases/latest");
            }
            catch { }
        }
    }

    static void CopyDirectory(string sourceDir, string destDir, string logPath)
    {
        foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDir, file);
            string dest = Path.Combine(destDir, relative);

            string? dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".pdb" or ".xml" or ".config" or ".log" or ".tmp")
                continue;

            try { File.Copy(file, dest, overwrite: true); }
            catch (Exception ex) { Log(logPath, $"Warning: could not copy {relative}: {ex.Message}"); }
        }
    }

    static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        byte[] hash = sha256.ComputeHash(stream);
        return Convert.ToHexStringLower(hash);
    }

    static void Log(string logPath, string message)
    {
        try
        {
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    static void WriteError(string message)
    {
        var err = new { error = message };
        Console.Error.WriteLine(JsonSerializer.Serialize(err));
    }
}
