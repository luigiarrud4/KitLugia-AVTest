using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace KitLugia.GUI.Services
{
    public static class DotNetDirectInstaller
    {
        public static bool IsDesktopRuntimeInstalled(string major = "10")
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App");
                if (key != null)
                {
                    foreach (var v in key.GetValueNames())
                    {
                        if (v.StartsWith(major + ".", StringComparison.Ordinal)) return true;
                    }
                }
                try
                {
                    var psi = new ProcessStartInfo("dotnet", "--list-runtimes")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var p = Process.Start(psi);
                    if (p != null)
                    {
                        string outp = p.StandardOutput.ReadToEnd();
                        p.WaitForExit();
                        if (outp.Contains("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase) && outp.Contains(major + "."))
                            return true;
                    }
                }
                catch { }
            }
            catch { }
            return false;
        }

        public static async Task<bool> PromptAndInstallDirectAsync(object? owner = null)
        {
            try
            {
                if (IsDesktopRuntimeInstalled("10") || IsDesktopRuntimeInstalled("8") || IsDesktopRuntimeInstalled("9"))
                    return true;

                string msg = "O .NET Desktop Runtime necessário não foi encontrado.\n\n" +
                             "Deseja instalar diretamente agora? (~60 MB)\n" +
                             "O KitLugia baixará o instalador oficial da Microsoft e executará em modo silencioso,\n" +
                             "sem precisar abrir o navegador.\n\n" +
                             "• SIM = Baixar e instalar agora (requer admin)\n" +
                             "• NÃO = Continuar sem instalar (algumas funções podem falhar)";

                var result = System.Windows.MessageBox.Show(
                    msg, "KitLugia — Instalar .NET Runtime",
                    System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);

                if (result != System.Windows.MessageBoxResult.Yes) return false;

                string[] urls = new[]
                {
                    "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.0/windowsdesktop-runtime-10.0.0-win-x64.exe",
                    "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.15/windowsdesktop-runtime-8.0.15-win-x64.exe",
                    "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"
                };

                string temp = Path.Combine(Path.GetTempPath(), "kitlugia-dotnet-runtime.exe");
                bool downloaded = false;
                string usedUrl = "";
                foreach (var url in urls)
                {
                    try
                    {
                        KitLugia.Core.Logger.Log($"[DotNet] Baixando {url}...");
                        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                        if (!resp.IsSuccessStatusCode) continue;
                        using var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
                        await resp.Content.CopyToAsync(fs);
                        if (new FileInfo(temp).Length > 1_000_000) { downloaded = true; usedUrl = url; break; }
                    }
                    catch (Exception ex) { KitLugia.Core.Logger.Log($"[DotNet] Falha {url}: {ex.Message}"); }
                }

                if (!downloaded)
                {
                    System.Windows.MessageBox.Show("Falha ao baixar o instalador. Verifique sua internet e tente novamente.",
                        "KitLugia", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return false;
                }

                KitLugia.Core.Logger.Log($"[DotNet] Baixado de {usedUrl} -> {temp} ({new FileInfo(temp).Length / 1024 / 1024} MB)");
                var psiInstall = new ProcessStartInfo(temp, "/install /quiet /norestart")
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };
                var proc = Process.Start(psiInstall);
                if (proc != null)
                {
                    await proc.WaitForExitAsync();
                    bool ok = IsDesktopRuntimeInstalled("10") || IsDesktopRuntimeInstalled("8");
                    System.Windows.MessageBox.Show(
                        ok ? "Instalação concluída! Reinicie o KitLugia se necessário." : "Instalação finalizada, mas não foi detectada. Reinicie o PC e tente novamente.",
                        "KitLugia", System.Windows.MessageBoxButton.OK, ok ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
                    try { File.Delete(temp); } catch { }
                    return ok;
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"[DotNet] Erro prompt direto: {ex.Message}");
            }
            return false;
        }
    }
}
