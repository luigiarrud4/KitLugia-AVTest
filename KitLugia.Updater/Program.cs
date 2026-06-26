using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace KitLugia.Updater;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("   KitLugia Updater v2.5");
        Console.WriteLine("========================================");
        Console.WriteLine();

        if (args.Length < 3)
        {
            Console.WriteLine("USO: KitLugia.Updater <zipPath> <mainPid> <mainExePath> [sha256]");
            Console.WriteLine();
            PressioneTecla();
            return;
        }

        string zipPath = args[0];
        int mainPid = int.Parse(args[1]);
        string mainExePath = args[2];
        string expectedHash = args.Length > 3 ? args[3] : null;
        string appDir = Path.GetDirectoryName(mainExePath);
        string logPath = Path.Combine(appDir, "update.log");

        bool success = false;
        try
        {
            Console.Write("[1/6] Verificando hash... ");
            if (!string.IsNullOrEmpty(expectedHash))
            {
                string actualHash = ComputeSha256(zipPath);
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"FALHOU");
                    Console.WriteLine($"  Hash esperado: {expectedHash}");
                    Console.WriteLine($"  Hash calculado: {actualHash}");
                    Log(logPath, $"HASH MISMATCH. Expected: {expectedHash}, Actual: {actualHash}");
                    goto error;
                }
                Console.WriteLine("OK");
            }
            else
            {
                Console.WriteLine("pulado (sem hash)");
            }

            Console.Write("[2/6] Aguardando fechamento do KitLugia... ");
            try
            {
                var mainProcess = Process.GetProcessById(mainPid);
                if (!mainProcess.WaitForExit(60000))
                {
                    mainProcess.Kill();
                    mainProcess.WaitForExit(5000);
                }
            }
            catch (ArgumentException) { }
            Console.WriteLine("OK");
            Thread.Sleep(1000);

            Console.Write("[3/6] Extraindo ZIP... ");
            string extractDir = Path.Combine(Path.GetTempPath(), $"KitLugia_Update_{Guid.NewGuid():N}");
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
            Console.WriteLine("OK");

            Console.Write("[4/6] Copiando arquivos... ");
            CopyDirectory(extractDir, appDir, logPath);
            Console.WriteLine("OK");

            Console.Write("[5/6] Limpando temporarios... ");
            try { Directory.Delete(extractDir, true); } catch { }
            try { File.Delete(zipPath); } catch { }
            Console.WriteLine("OK");

            Console.Write("[6/6] Iniciando nova versao... ");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = mainExePath,
                    UseShellExecute = true
                };
                Process.Start(psi);
                success = true;
                Console.WriteLine("OK");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FALHOU: {ex.Message}");
                Log(logPath, $"Restart failed: {ex}");
                goto error;
            }

            Console.WriteLine();
            Console.WriteLine("========================================");
            Console.WriteLine("   ATUALIZACAO CONCLUIDA COM SUCESSO!");
            Console.WriteLine("========================================");
            Console.WriteLine("A janela fechara em 3 segundos...");
            Thread.Sleep(3000);
            return;

        error:
            Console.WriteLine();
            Console.WriteLine("========================================");
            Console.WriteLine("   ERRO NA ATUALIZACAO");
            Console.WriteLine("========================================");
            Console.WriteLine("  Log salvo em: update.log");
            PressioneTecla();
            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"ERRO FATAL: {ex.Message}");
            Log(logPath, $"FATAL: {ex}");
            Console.WriteLine();
            PressioneTecla();
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

            string name = Path.GetFileName(file);
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".pdb" or ".xml" or ".config" or ".log" or ".tmp")
                continue;
            if (name is "KitLugia.Updater.exe" or "KitLugia.Updater.dll")
                continue;

            try { File.Copy(file, dest, overwrite: true); }
            catch (Exception ex) { Log(logPath, $"Warning: could not copy {relative}: {ex.Message}"); }
        }
    }

    static void PressioneTecla()
    {
        try { Console.ReadKey(); }
        catch { Console.ReadLine(); }
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
}
