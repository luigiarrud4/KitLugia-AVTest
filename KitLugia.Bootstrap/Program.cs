using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KitLugia.Bootstrap
{
    /// <summary>
    /// LAUNCHER / BOOTSTRAPPER DO KITLUGIA
    /// ===================================
    /// O app principal (KitLugia.GUI.exe) e' net10.0-windows framework-dependent.
    /// Sem o .NET Desktop Runtime 10 na maquina ele NAO abre tela nenhuma: o
    /// apphost do .NET mostra "voce precisa instalar o .NET" e manda o usuario
    /// para a pagina de download no navegador.
    ///
    /// Este launcher (net48, .NET Framework que ja vem no Windows) roda ANTES do
    /// app, detecta o que falta e oferece BAIXAR E INSTALAR ali mesmo — no
    ///.visual do Kit — exatamente como o LuaTools faz com o proprio .NET 8.
    ///
    /// Fluxo:
    ///   1. detecta componentes ausentes (.NET Desktop Runtime x64 / Visual C++ x64);
    ///   2. se nao falta nada -> abre o app e sai;
    ///   3. se falta -> mostra a tela "Componentes Adicionais Necesarios";
    ///   4. "Instalar" -> baixa do servidor oficial da Microsoft, instala (UAC) e
    ///      so entao abre o Kit.
    ///
    /// Modos de diagnostico (nao usados pelo usuario):
    ///   --selftest        imprime a deteccao de componentes
    ///   --shot [pasta]    renderiza a tela em PNG (prova visual)
    /// </summary>
    internal static class Program
    {
        private const int ATTACH_PARENT_PROCESS = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (HasArg(args, "--selftest"))
                {
                    AttachConsole(ATTACH_PARENT_PROCESS);
                    return RunSelfTest(args);
                }

                if (HasArg(args, "--shot"))
                {
                    AttachConsole(ATTACH_PARENT_PROCESS);
                    return RunShot(args);
                }

                return RunLauncher(args);
            }
            catch (Exception ex)
            {
                BootstrapLog.Write("[Bootstrap] ERRO FATAL: " + ex);
                try
                {
                    MessageBox.Show("O inicializador do KitLugia encontrou um erro:\n\n" + ex.Message,
                        "KitLugia", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
                return 3;
            }
        }

        // ── Fluxo principal ──────────────────────────────────────────────

        private static int RunLauncher(string[] args)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string appExe = FindManagedApp(dir);
            List<RequiredComponent> missing = ComponentDetector.GetMissingComponents();

            // --simulate-missing: forca o estado "faltam componentes" mesmo em maquinas
            // que JA tem o runtime. Serve para QA/teste visual da tela (o launcher
            // normal nunca usa isto).
            if (HasArg(args, "--simulate-missing"))
            {
                missing = new List<RequiredComponent>
                {
                    ComponentDetector.DotNetComponent(),
                    ComponentDetector.VcppComponent()
                };
            }

            BootstrapLog.Write("[Bootstrap] Iniciado. Pasta: " + dir);
            BootstrapLog.Write("[Bootstrap] App gerenciado: " + (appExe ?? "(nao encontrado nesta pasta)"));
            BootstrapLog.Write("[Bootstrap] Faltando: " +
                (missing.Count == 0 ? "(nada)" : string.Join(", ", missing.Select(m => m.DisplayName).ToArray())));

            if (missing.Count == 0)
            {
                if (string.IsNullOrEmpty(appExe))
                {
                    MessageBox.Show(
                        "O executável principal do KitLugia (KitLugia.GUI.exe) não foi encontrado nesta pasta:\n\n" + dir,
                        "KitLugia", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return 4;
                }
                return LaunchManagedApp(appExe, args);
            }

            // Modo silencioso (auto-start / --tray): no boot do Windows nao pode
            // aparecer dialogo. Fica so' o registro no log.
            if (IsSilentMode(args))
            {
                BootstrapLog.Write("[Bootstrap] Componentes ausentes e modo silencioso (--tray): nenhuma janela foi exibida.");
                return 2;
            }

            var window = new DependencyDialogWindow(missing);
            bool? result = window.ShowDialog();

            if (result == true)
            {
                if (string.IsNullOrEmpty(appExe))
                {
                    MessageBox.Show(
                        "Os componentes foram instalados, mas o executável principal do KitLugia (KitLugia.GUI.exe)\n" +
                        "não foi encontrado nesta pasta:\n\n" + dir,
                        "KitLugia", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return 4;
                }
                return LaunchManagedApp(appExe, args);
            }

            BootstrapLog.Write("[Bootstrap] Usuario cancelou a instalacao dos componentes.");
            return 1;
        }

        /// <summary>Abre o app gerenciado repassando os argumentos (--tray, --unlock ...).</summary>
        private static int LaunchManagedApp(string exe, string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? AppDomain.CurrentDomain.BaseDirectory
                };

                var forwarded = (args ?? Array.Empty<string>())
                    .Where(a => !a.StartsWith("--selftest", StringComparison.OrdinalIgnoreCase)
                             && !a.StartsWith("--shot", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (forwarded.Length > 0)
                    psi.Arguments = string.Join(" ", forwarded.Select(Quote).ToArray());

                BootstrapLog.Write("[Bootstrap] Abrindo o KitLugia: " + exe + " " + psi.Arguments);
                Process.Start(psi);
                return 0;
            }
            catch (Exception ex)
            {
                BootstrapLog.Write("[Bootstrap] Falha ao abrir o KitLugia: " + ex.Message);
                try
                {
                    MessageBox.Show("Não foi possível abrir o KitLugia:\n\n" + ex.Message,
                        "KitLugia", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
                return 5;
            }
        }

        /// <summary>
        /// Procura o executavel gerenciado (o APP) na mesma pasta. Ordem:
        /// "KitLugia.App.exe" (nome no release) -&gt; "KitLugia.GUI.exe" (nome
        /// antigo / build de dev) -&gt; qualquer outro KitLugia*.exe que nao seja o
        /// launcher.
        /// </summary>
        private static string FindManagedApp(string dir)
        {
            try
            {
                string preferred = Path.Combine(dir, "KitLugia.App.exe");
                if (File.Exists(preferred)) return preferred;

                string legacy = Path.Combine(dir, "KitLugia.GUI.exe");
                if (File.Exists(legacy)) return legacy;

                foreach (string file in Directory.GetFiles(dir, "KitLugia*.exe"))
                {
                    string name = Path.GetFileName(file);
                    if (string.Equals(name, "KitLugia.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    return file;
                }
            }
            catch { }
            return null;
        }

        private static bool IsSilentMode(string[] args)
        {
            if (args == null) return false;
            foreach (string a in args)
            {
                string lower = (a ?? "").ToLowerInvariant();
                if (lower == "--tray" || lower == "-tray" || lower == "--minimized") return true;
            }
            return false;
        }

        // ── Diagnostico: --selftest ──────────────────────────────────────

        private static int RunSelfTest(string[] args)
        {
            var report = new StringBuilder();
            report.AppendLine("=== kitlugia bootstrap selftest ===");
            report.AppendLine("Pasta do launcher : " + AppDomain.CurrentDomain.BaseDirectory);
            report.AppendLine("App gerenciado    : " + (FindManagedApp(AppDomain.CurrentDomain.BaseDirectory) ?? "(nao encontrado)"));
            report.AppendLine(".NET Desktop " + ComponentDetector.RequiredDotNetMajor + " x64 : " +
                (ComponentDetector.HasDotNetDesktopRuntime() ? "INSTALADO" : "AUSENTE"));
            report.AppendLine("Visual C++ 2015-2022 x64        : " +
                (ComponentDetector.HasVisualCppRedistX64() ? "INSTALADO" : "AUSENTE"));

            var missing = ComponentDetector.GetMissingComponents();
            report.AppendLine("Componentes ausentes            : " +
                (missing.Count == 0 ? "(nenhum)" : string.Join(", ", missing.Select(m => m.DisplayName).ToArray())));

            if (HasArg(args, "--download"))
            {
                // Prova real da etapa de download (baixa para %TEMP% e mede).
                var component = ComponentDetector.DotNetComponent();
                var progress = new Progress<InstallProgress>(p =>
                {
                    Console.WriteLine("  " + p.Status + " " + p.Percent.ToString("0") + "%");
                });
                bool ok = ComponentInstaller.DownloadAsync(component, progress, BootstrapLog.Write).GetAwaiter().GetResult();
                string file = Path.Combine(Path.GetTempPath(), component.FileName);
                long size = File.Exists(file) ? new FileInfo(file).Length : 0;
                report.AppendLine("Download de teste               : " + (ok ? "OK" : "FALHOU") +
                    " (" + (size / 1024 / 1024) + " MB em " + file + ")");
                Console.WriteLine(report.ToString());
                BootstrapLog.Write(report.ToString());
                return ok ? 0 : 1;
            }

            if (HasArg(args, "--install-dryrun"))
            {
                // PROVA do caminho de INSTALACAO (processo -> espera -> codigo de saida)
                // sem UAC: usa um instalador stand-in que devolve 0. O que fica de
                // fora desta prova e' apenas a elevacao real do instalador oficial.
                string standin = Path.Combine(Path.GetTempPath(), "kitlugia-standin-installer.cmd");
                File.WriteAllText(standin, "@exit /b 0" + Environment.NewLine);
                var fake = new RequiredComponent
                {
                    Id = ComponentId.VisualCppRedist,
                    DisplayName = "stand-in",
                    FileName = "kitlugia-standin-installer.cmd",
                    InstallArgs = ""
                };
                InstallOutcome installed = ComponentInstaller.Install(fake, BootstrapLog.Write);
                report.AppendLine("Instalacao (stand-in, sem UAC)     : " +
                    (installed == InstallOutcome.Success ? "OK" : installed.ToString()));
            }

            Console.WriteLine(report.ToString());
            BootstrapLog.Write(report.ToString());
            return 0;
        }

        // ── Diagnostico: --shot (renderiza a tela em PNG) ────────────────

        private static int RunShot(string[] args)
        {
            string outDir = GetArgValue(args, "--shot");
            if (string.IsNullOrEmpty(outDir))
                outDir = Path.Combine(Path.GetTempPath(), "kitlugia-bootstrap-shots");

            Directory.CreateDirectory(outDir);

            var components = new List<RequiredComponent>
            {
                ComponentDetector.DotNetComponent(),
                ComponentDetector.VcppComponent()
            };

            // Estado 1: tela inicial (o que o usuario ve ao abrir o Kit sem .NET)
            var view = new DependencyDialogView();
            view.SetComponents(components);
            string p1 = Path.Combine(outDir, "bootstrap-dialog.png");
            RenderDialog(view, p1);
            Console.WriteLine("shot: " + p1);

            // Estado 2: durante a instalacao (barra + status)
            var view2 = new DependencyDialogView();
            view2.SetComponents(components);
            view2.SetProgress(43, "Baixando .NET Desktop Runtime 10 x64...");
            view2.SetBusy(true, "Baixando...");
            string p2 = Path.Combine(outDir, "bootstrap-progress.png");
            RenderDialog(view2, p2);
            Console.WriteLine("shot: " + p2);

            return 0;
        }

        private static void RenderDialog(DependencyDialogView view, string path)
        {
            try
            {
                const double width = 540;

                // Hospeda a view num fundo escuro com folga para o brilho dourado
                // da borda aparecer na captura (o DropShadowEffect e' desenhado
                // FORA dos limites do elemento).
                var host = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)),
                    Padding = new Thickness(28),
                    Child = view
                };

                host.Measure(new Size(width + 56, double.PositiveInfinity));
                double height = host.DesiredSize.Height;
                host.Arrange(new Rect(0, 0, width + 56, height));
                host.UpdateLayout();

                var rtb = new RenderTargetBitmap((int)(width + 56), (int)height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(host);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(path))
                    encoder.Save(fs);
            }
            catch (Exception ex)
            {
                BootstrapLog.Write("[Bootstrap] Falha ao renderizar o shot: " + ex.Message);
            }
        }

        // ── Utilidades ───────────────────────────────────────────────────

        private static bool HasArg(string[] args, string name)
        {
            if (args == null) return false;
            foreach (string a in args)
            {
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Valor do argumento "--shot &lt;pasta&gt;" (ou null).</summary>
        private static string GetArgValue(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            if (value.IndexOf(' ') < 0 && value.IndexOf('"') < 0) return value;
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
