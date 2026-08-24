using System;
using System.Diagnostics;
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

        [STAThread]
        public static void Main(string[] args)
        {
            bool startMinimized = false;
            string? unlockPath = null;

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
            }

            UnlockPath = unlockPath;

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
                // Já existe uma instância rodando
                // Se --unlock foi passado, envia o caminho via IPC para a instância existente
                if (!string.IsNullOrEmpty(unlockPath))
                {
                    Services.UnlockIpcServer.SendUnlockCommand(unlockPath);
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
