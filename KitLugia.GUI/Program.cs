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

        [STAThread]
        public static void Main(string[] args)
        {
            bool startMinimized = false;
            foreach (var arg in args)
            {
                string lower = arg.ToLower();
                if (lower == "--tray" || lower == "-tray" || lower == "--minimized")
                {
                    startMinimized = true;
                    break;
                }
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
                // Já existe uma instância rodando — traz para frente
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
                try { _mutex?.ReleaseMutex(); _mutex?.Dispose(); } catch { }
            }
        }

        private static void BringExistingToFront()
        {
            try
            {
                var current = Process.GetCurrentProcess();
                var existing = Process.GetProcessesByName(current.ProcessName)
                    .FirstOrDefault(p => p.Id != current.Id);

                if (existing is not null && !existing.HasExited && existing.MainWindowHandle != IntPtr.Zero)
                {
                    if (IsIconic(existing.MainWindowHandle)) ShowWindow(existing.MainWindowHandle, SW_RESTORE);
                    else ShowWindow(existing.MainWindowHandle, SW_SHOW);
                    SetForegroundWindow(existing.MainWindowHandle);
                    return;
                }

                // Janela oculta (tray mode) ou processo inexistente — envia sinal via named event
                try
                {
                        EventWaitHandle.OpenExisting("Global\\KitLugia_ShowWindow")?.Set();
                }
                catch { }
            }
            catch { }
        }
    }
}
