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
            _mutex = new Mutex(true, "Global\\KitLugia_SingleInstance", out bool isNew);
            if (!isNew)
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
            var app = new App();
            app.StartMinimized = startMinimized;
            app.InitializeComponent();
            app.Run();

            // Libera o mutex ao sair
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }

        private static void BringExistingToFront()
        {
            try
            {
                var current = Process.GetCurrentProcess();
                var existing = Process.GetProcessesByName(current.ProcessName)
                    .FirstOrDefault(p => p.Id != current.Id);

                if (existing is not null && existing.MainWindowHandle != IntPtr.Zero)
                {
                    if (IsIconic(existing.MainWindowHandle)) ShowWindow(existing.MainWindowHandle, SW_RESTORE);
                    else ShowWindow(existing.MainWindowHandle, SW_SHOW);
                    SetForegroundWindow(existing.MainWindowHandle);
                }
                else
                {
                    // Janela oculta (tray mode) — envia sinal via named event
                    try
                    {
                        EventWaitHandle.OpenExisting("KitLugia_ShowWindow")?.Set();
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
