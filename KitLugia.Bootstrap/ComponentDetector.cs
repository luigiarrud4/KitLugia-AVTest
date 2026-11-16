using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace KitLugia.Bootstrap
{
    /// <summary>Componentes que o KitLugia pode precisar antes de abrir.</summary>
    public enum ComponentId
    {
        DotNetDesktopRuntime,
        VisualCppRedist
    }

    /// <summary>
    /// Um componente que pode faltar na maquina: o que ele e', de onde baixa e
    /// com que argumento instala.
    /// </summary>
    public sealed class RequiredComponent
    {
        public ComponentId Id { get; set; }
        public string DisplayName { get; set; } = "";
        public string Detail { get; set; } = "";
        public string[] Urls { get; set; } = Array.Empty<string>();
        public string FileName { get; set; } = "";
        public string InstallArgs { get; set; } = "/install /quiet /norestart";

        public bool IsInstalled()
        {
            return Id == ComponentId.DotNetDesktopRuntime
                ? ComponentDetector.HasDotNetDesktopRuntime()
                : ComponentDetector.HasVisualCppRedistX64();
        }
    }

    /// <summary>
    /// Detecta o que falta na maquina para o KitLugia rodar.
    ///
    /// O app principal e' net10.0-windows framework-dependent: sem o
    /// Microsoft.WindowsDesktop.App 10.x (x64) o WPF nem carrega. O Visual C++
    /// Redistributable x64 tambem e' necessario porque o .NET Framework usa
    /// vcruntime140.dll.
    ///
    /// A deteccao do runtime usa TRES fontes, porque a primeira pode falhar:
    ///  1) registro de instalacao do .NET (fonte oficial, sempre presente apos instalar);
    ///  2) `dotnet --list-runtimes` (pega runtime em path customizado / DOTNET_ROOT);
    ///  3) varredura de %ProgramFiles%\dotnet\shared (ultimo recurso).
    /// </summary>
    public static class ComponentDetector
    {
        /// <summary>Major do .NET Desktop Runtime exigido pelo app principal (net10.0).</summary>
        public const int RequiredDotNetMajor = 10;

        /// <summary>
        /// .NET Desktop Runtime x64 instalado? (Microsoft.WindowsDesktop.App {major}.x)
        /// </summary>
        public static bool HasDotNetDesktopRuntime(int major = RequiredDotNetMajor)
        {
            // 1) Registro de instalacao (fonte canonica do .NET)
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App"))
                {
                    if (key != null)
                    {
                        foreach (string v in key.GetValueNames())
                        {
                            if (v.StartsWith(major + ".", StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                    }
                }
            }
            catch { /* registro bloqueado/ausente — segue para o proximo metodo */ }

            // 2) dotnet --list-runtimes (pega DOTNET_ROOT e installs nao-padrao)
            try
            {
                var psi = new ProcessStartInfo("dotnet", "--list-runtimes")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        string output = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(15000);
                        if (!string.IsNullOrEmpty(output) &&
                            output.IndexOf("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            foreach (string line in output.Split('\n'))
                            {
                                string trimmed = line.Trim();
                                if (trimmed.StartsWith("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase))
                                {
                                    string version = trimmed.Substring("Microsoft.WindowsDesktop.App".Length).Trim();
                                    if (version.StartsWith(major + ".", StringComparison.Ordinal)) return true;
                                }
                            }
                        }
                    }
                }
            }
            catch { /* dotnet pode nao existir no PATH */ }

            // 3) Pasta padrao de install (ultimo recurso)
            try
            {
                string root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
                if (string.IsNullOrEmpty(root))
                    root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
                string shared = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
                if (Directory.Exists(shared))
                {
                    foreach (string dir in Directory.GetDirectories(shared))
                    {
                        if (Path.GetFileName(dir).StartsWith(major + ".", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch { }

            return false;
        }

        /// <summary>Microsoft Visual C++ 2015-2022 Redistributable x64 instalado?</summary>
        public static bool HasVisualCppRedistX64()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64"))
                {
                    if (key == null) return false;
                    object installed = key.GetValue("Installed");
                    object version = key.GetValue("Version");
                    int flag = 0;
                    if (installed != null) int.TryParse(installed.ToString(), out flag);
                    return flag == 1 && version != null && !string.IsNullOrWhiteSpace(version.ToString());
                }
            }
            catch { return false; }
        }

        /// <summary>Lista dos componentes que faltam (vazia = pode abrir o Kit).</summary>
        public static List<RequiredComponent> GetMissingComponents()
        {
            var missing = new List<RequiredComponent>();

            if (!HasDotNetDesktopRuntime())
            {
                missing.Add(new RequiredComponent
                {
                    Id = ComponentId.DotNetDesktopRuntime,
                    DisplayName = ".NET Desktop Runtime " + RequiredDotNetMajor + ".0 x64",
                    Detail = "Obrigatorio para o KitLugia (WPF) funcionar — ~70 MB",
                    FileName = "windowsdesktop-runtime-" + RequiredDotNetMajor + ".0-win-x64.exe",
                    Urls = new[]
                    {
                        "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe",
                        "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.0/windowsdesktop-runtime-10.0.0-win-x64.exe"
                    }
                });
            }

            if (!HasVisualCppRedistX64())
            {
                missing.Add(new RequiredComponent
                {
                    Id = ComponentId.VisualCppRedist,
                    DisplayName = "Microsoft Visual C++ 2015-2022 x64",
                    Detail = "Bibliotecas de execucao usadas pelo .NET — ~25 MB",
                    FileName = "vc_redist.x64.exe",
                    Urls = new[]
                    {
                        "https://aka.ms/vs/17/release/vc_redist.x64.exe"
                    }
                });
            }

            return missing;
        }

        /// <summary>Todos os componentes, instalados ou nao (usado na tela de diagnostico).</summary>
        public static List<RequiredComponent> GetAllComponents()
        {
            var all = new List<RequiredComponent>();
            foreach (var c in GetMissingComponents()) all.Add(c);

            // Complementa com os que JA estao instalados, para o --selftest.
            if (all.All(c => c.Id != ComponentId.DotNetDesktopRuntime))
                all.Insert(0, DotNetComponent());
            if (all.All(c => c.Id != ComponentId.VisualCppRedist))
                all.Add(VcppComponent());

            return all;
        }

        internal static RequiredComponent DotNetComponent()
        {
            return new RequiredComponent
            {
                Id = ComponentId.DotNetDesktopRuntime,
                DisplayName = ".NET Desktop Runtime " + RequiredDotNetMajor + ".0 x64",
                Detail = "Obrigatorio para o KitLugia (WPF) funcionar — ~70 MB",
                FileName = "windowsdesktop-runtime-" + RequiredDotNetMajor + ".0-win-x64.exe",
                Urls = new[]
                {
                    "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe",
                    "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.0/windowsdesktop-runtime-10.0.0-win-x64.exe"
                }
            };
        }

        internal static RequiredComponent VcppComponent()
        {
            return new RequiredComponent
            {
                Id = ComponentId.VisualCppRedist,
                DisplayName = "Microsoft Visual C++ 2015-2022 x64",
                Detail = "Bibliotecas de execucao usadas pelo .NET — ~25 MB",
                FileName = "vc_redist.x64.exe",
                Urls = new[]
                {
                    "https://aka.ms/vs/17/release/vc_redist.x64.exe"
                }
            };
        }
    }
}
