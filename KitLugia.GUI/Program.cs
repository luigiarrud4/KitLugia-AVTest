using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using KitLugia.Core;

namespace KitLugia.GUI
{
    public class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);
        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;

        private static Mutex? _mutex;

        public static string? UnlockPath { get; private set; }
        public static string? TakeOwnPath { get; private set; }

        [STAThread]
        public static void Main(string[] args)
        {
            bool startMinimized = false;
            string? unlockPath = null;
            string? takeOwnPath = null;

            for (int i = 0; i < args.Length; i++)
            {
                string lower = args[i].ToLower();
                if (lower == "--tray" || lower == "-tray" || lower == "--minimized")
                {
                    startMinimized = true;
                }
                else if (lower == "--unlock" && i + 1 < args.Length)
                {
                    unlockPath = args[++i];
                }
                else if (lower == "--takeown" && i + 1 < args.Length)
                {
                    takeOwnPath = args[++i];
                }
            }

            UnlockPath = unlockPath;
            TakeOwnPath = takeOwnPath;

            bool needsFileOp = !string.IsNullOrEmpty(unlockPath) || !string.IsNullOrEmpty(takeOwnPath);

            // ★ PROVA-DE-TUDO (03/09): --unlock/--takeown SEM privilégio de administrador
            // falham em arquivos protegidos (Windows.old, TrustedInstaller, processos de
            // outros usuários). O menu de contexto lança o Kit sem runas — aqui relançamos
            // a MESMA linha de comando elevada (UAC). Se o usuário cancelar o UAC, cai no
            // fluxo normal (IPC para a instância existente), que pelo menos abre a página.
            if (needsFileOp && !SystemUtils.IsRunningAsAdministrator())
            {
                try
                {
                    string argLine = string.Join(" ", args.Select(a => a.Contains(' ') || a.Contains('\t') ? $"\"{a}\"" : a));
                    Logger.Log($"[ELEV] Relançando elevado (UAC) com: {argLine}");
                    Process.Start(new ProcessStartInfo(Environment.ProcessPath ?? typeof(Program).Assembly.Location)
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                        Arguments = argLine
                    });
                }
                catch (Exception ex)
                {
                    // UAC negado — continua sem admin (IPC/UI normal)
                    Logger.Log($"[ELEV] Falha ao relançar elevado (UAC negado?): {ex.Message}");
                }
                return;
            }

            // ★ OTIMIZAÇÃO: boost self priority to High so the tray icon + watchdog load faster.
            // Padrão é Normal — fica atrás de outros apps de boot na disputa por CPU.
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; }
            catch { /* pode falhar sem admin — não é crítico */ }

            // --- SINGLE INSTANCE CHECK ---
            // Se já existe uma instância, traz a janela dela para frente e sai
            // Usa WaitOne em vez de initiallyOwned=true para tratar AbandonedMutexException
            // (crash da instância anterior não impede reinício do app).
            _mutex = new Mutex(false, "Global\\KitLugia_SingleInstance");
            bool acquired;
            try
            {
                acquired = _mutex.WaitOne(TimeSpan.FromMilliseconds(100));
            }
            catch (AbandonedMutexException)
            {
                // Instância anterior crashou — assumimos ownership e continuamos
                acquired = true;
            }
            if (!acquired)
            {
                // Já existe uma instância rodando.
                // PROVA-DE-TUDO (03/09): se ESTA instância está elevada e veio de --unlock/--takeown,
                // executa a operação DIRETO (headless worker) em vez de enviar via IPC para a
                // instância principal — que pode estar SEM admin e falharia em arquivos protegidos.
                if (SystemUtils.IsRunningAsAdministrator() && needsFileOp)
                {
                    Logger.Log("[ELEV] Instância elevada + mutex ocupado → worker headless.");
                    RunHeadlessFileOperation(takeOwnPath ?? unlockPath!, isTakeOwn: takeOwnPath != null);
                    return;
                }

                // Se --unlock/--takeown foram passados, envia via IPC para a instância existente
                if (!string.IsNullOrEmpty(unlockPath))
                {
                    Services.UnlockIpcServer.SendUnlockCommand(unlockPath);
                }
                if (!string.IsNullOrEmpty(takeOwnPath))
                {
                    Services.UnlockIpcServer.SendTakeOwnershipCommand(takeOwnPath);
                }
                BringExistingToFront();
                return;
            }

            // ==============================================================================
            // OTIMIZAÇÃO EXTREMA "RUST-LIKE":
            // O lançamento dos apps do Turbo Boot foi movido para TrayIconService.Initialize()
            // (após o ícone da bandejar ficar visível), onde roda em background thread.
            // Isto destrava o WPF para carregar o mais rápido possível.
            // ==============================================================================

            // Inicia o WPF normalmente
            try
            {
                var app = new App();
                app.StartMinimized = startMinimized;
                app.InitializeComponent();
                app.Run();
            }
            finally
            {
                try { _mutex?.ReleaseMutex(); _mutex?.Dispose(); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
        }

        /// <summary>
        /// Worker headless elevado: executa o take ownership / force stop direto no processo
        /// elevado e avisa via toast do Windows. Usado quando o mutex está ocupado pela
        /// instância principal (que pode não estar elevada).
        /// </summary>
        private static void RunHeadlessFileOperation(string path, bool isTakeOwn)
        {
            try
            {
                string name = Path.GetFileName(path.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(name)) name = path;

                if (isTakeOwn)
                {
                    var result = KitLugia.Core.FileTakeOwnership.TakeOwn(path, recursive: true, (d, t, c) => { }, grantFullControlOnDirs: false);
                    string msg = result.Ok
                        ? $"✅ {name}: {result.Success} item(ns) agora são seus."
                        : $"⚠️ {name}: {result.Failed} falha(s) de {result.Total}." + (result.Errors.Count > 0 ? " " + string.Join(" | ", result.Errors.Take(2)) : "");
                    if (result.FallbackUsed) msg += " (fallback clássico takeown/icacls usado)";
                    KitLugia.Core.WindowsToastNotifier.Show("KitLugia — Take Ownership", msg);
                }
                else
                {
                    var blocking = KitLugia.Core.ForceStopUnlockService.FindBlockingProcesses(path);
                    if (blocking.Count == 0)
                    {
                        KitLugia.Core.WindowsToastNotifier.Show("KitLugia — Force Stop", $"✅ {name}: nenhum processo bloqueador encontrado.");
                        return;
                    }
                    var res = KitLugia.Core.ForceStopUnlockService.Unlock(path, blocking, deleteTarget: false);
                    string msg2 = res.Success
                        ? $"✅ {name}: {res.Message}"
                        : $"⚠️ {name}: {res.Message}" + (res.Errors.Count > 0 ? " " + string.Join(" | ", res.Errors.Take(2)) : "");
                    KitLugia.Core.WindowsToastNotifier.Show("KitLugia — Force Stop", msg2);
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"[ELEV] Worker headless falhou: {ex}");
                try { KitLugia.Core.WindowsToastNotifier.Show("KitLugia", $"Erro: {ex.Message}"); } catch { }
            }
        }

        private static void BringExistingToFront()
        {
            try
            {
                var current = Process.GetCurrentProcess();
                Process? existing = null;
                foreach (var p in Process.GetProcessesByName(current.ProcessName))
                {
                    if (p.Id == current.Id) { p.Dispose(); continue; }
                    if (existing == null && !p.HasExited) existing = p;
                    else p.Dispose();
                }

                if (existing is not null && !existing.HasExited && existing.MainWindowHandle != IntPtr.Zero)
                {
                    if (IsIconic(existing.MainWindowHandle)) ShowWindow(existing.MainWindowHandle, SW_RESTORE);
                    else ShowWindow(existing.MainWindowHandle, SW_SHOW);
                    SetForegroundWindow(existing.MainWindowHandle);
                    existing.Dispose();
                    return;
                }
                existing?.Dispose();

                // Janela oculta (tray mode) ou processo inexistente — envia sinal via named event
                try
                {
                        EventWaitHandle.OpenExisting("Global\\KitLugia_ShowWindow")?.Set();
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }
    }
}
