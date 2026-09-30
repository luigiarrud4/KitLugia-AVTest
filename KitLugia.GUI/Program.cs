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

            // ── Modo WORKER (--worker): executa UMA operação de arquivo e sai ──────────
            // Usado pelo ElevatedFileOpRunner: a instância principal (que pode estar SEM
            // elevação) dispara este processo elevado com UM UAC e recebe progresso +
            // resultado de volta — assim a página nunca "para" esperando o usuário
            // relançar o app inteiro.
            bool worker = false;
            string workerAction = "";
            string workerPath = "";
            bool workerRecursive = false;
            bool workerFullControl = false;
            string? workerResult = null;
            string? workerProgress = null;

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
                else if (lower == "--worker")
                {
                    worker = true;
                }
                else if (lower == "--action" && i + 1 < args.Length)
                {
                    workerAction = args[++i];
                }
                else if (lower == "--path" && i + 1 < args.Length)
                {
                    workerPath = args[++i];
                }
                else if (lower == "--recursive")
                {
                    workerRecursive = true;
                }
                else if (lower == "--full-control")
                {
                    workerFullControl = true;
                }
                else if (lower == "--result" && i + 1 < args.Length)
                {
                    workerResult = args[++i];
                }
                else if (lower == "--progress" && i + 1 < args.Length)
                {
                    workerProgress = args[++i];
                }
            }

            // O worker roda ANTES de qualquer coisa: sem UI, sem mutex, sem relançamento.
            if (worker)
            {
                int code = RunWorker(workerAction, workerPath, workerRecursive, workerFullControl,
                    workerResult, workerProgress);
                Environment.Exit(code);
                return;
            }

            UnlockPath = unlockPath;
            TakeOwnPath = takeOwnPath;

            bool needsFileOp = !string.IsNullOrEmpty(unlockPath) || !string.IsNullOrEmpty(takeOwnPath);

            // ★ MENU DE CONTEXTO ABRE O KIT (29/09): o clique em "Take Ownership (KitLugia)"
            // ou "Force Stop Unlock (KitLugia)" deve SEMPRE abrir a janela do Kit na página
            // certa, com o caminho pré-preenchido e a análise/ação começando na hora —
            // exatamente o que o fluxo antigo fazia.
            //
            // A elevação NÃO é mais feita aqui: quem pede o UAC é a própria página
            // (ForceStopUnlockPage → RunGuaranteedAsync → ElevatedFileOpRunner), UMA vez,
            // com progresso visível, e só quando o alvo realmente exigir admin.
            //
            // O bootstrap anterior fazia:
            //   1) relançar o exe elevado (Verb=runas) e ENCERRAR esta instância;
            //   2) a instância elevada, ao encontrar o Kit já aberto (mutex ocupado),
            //      rodava um worker HEADLESS e saía — nenhuma janela, só um toast que
            //      podia falhar silenciosamente.
            // Com o Kit na bandeja (caso comum) o resultado era "cliquei e nada acontece".
            // Agora o comando segue para a instância que já existe (IPC, que mostra a
            // janela da bandeja) ou, se não houver nenhuma, esta mesma abre a UI com
            // --unlock/--takeown (tratado em App.OnStartup).

            // ★ OTIMIZAÇÃO: boost self priority to High so the tray icon + watchdog load faster.
            // Padrão é Normal — fica atrás de outros apps de boot na disputa por CPU.
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; }
            catch { /* pode falhar sem admin — não é crítico */ }

            // ★ SEM THREAD POOL STARVATION: o startup dispara muitos Task.Run (auto-start, menu de
            // contexto, updater, watchdogs, checks de runtime). Com os mínimos padrão (nº de
            // núcleos), o pool injeta 1-2 threads por segundo — na máquina carregada a fila cresce e
            // a UI fica esperando trabalho que "nunca" foi agendado (parece app travado).
            // Mínimo 8 workers (ou nº de núcleos, se maior) já sobe isso de forma conservadora.
            try
            {
                int minThreads = Math.Max(Environment.ProcessorCount, 8);
                ThreadPool.SetMinThreads(minThreads, minThreads);
            }
            catch { /* best-effort */ }

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
                // Já existe uma instância rodando: entrega o comando via IPC. A instância
                // principal MOSTRA a janela (mesmo que esteja minimizada para a bandeja —
                // ver MainWindow.ShowAndActivateFromTray) e navega para a página certa com
                // o caminho pré-preenchido, disparando a análise/ação.
                bool sent = false;
                if (!string.IsNullOrEmpty(unlockPath))
                {
                    sent |= Services.UnlockIpcServer.SendUnlockCommand(unlockPath);
                }
                if (!string.IsNullOrEmpty(takeOwnPath))
                {
                    sent |= Services.UnlockIpcServer.SendTakeOwnershipCommand(takeOwnPath);
                }

                if (needsFileOp)
                {
                    Logger.Log(sent
                        ? $"[IPC] Comando do menu de contexto entregue à instância existente: {(takeOwnPath ?? unlockPath)}"
                        : "[IPC] Instância existente sem servidor de pipe — trazendo a janela para frente.");
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
        /// Worker de operação de arquivo (--worker). Roda o pipeline garantido
        /// (<see cref="KitLugia.Core.FileOpGuarantee"/>) e publica o progresso num arquivo
        /// texto (uma linha por passo) para a UI mostrar ao vivo. Nunca abre janela.
        /// Retorna 0 quando o objetivo foi atingido (agora ou agendado para o boot).
        /// </summary>
        private static int RunWorker(string action, string path, bool recursive, bool fullControl,
            string? resultFile, string? progressFile)
        {
            try
            {
                var kind = action.ToLowerInvariant() switch
                {
                    "takeown" or "takeownership" => KitLugia.Core.GuaranteedAction.TakeOwnership,
                    "delete" => KitLugia.Core.GuaranteedAction.Delete,
                    _ => KitLugia.Core.GuaranteedAction.ForceStop,
                };

                void Progress(string line)
                {
                    try { Console.Out.WriteLine(line); Console.Out.Flush(); } catch { }
                    if (string.IsNullOrEmpty(progressFile)) return;
                    try { File.AppendAllText(progressFile, line + Environment.NewLine); } catch { }
                }

                Progress($"worker: {kind} em {path}");
                Progress($"admin: {KitLugia.Core.SystemUtils.IsRunningAsAdministrator()}");

                var result = KitLugia.Core.FileOpGuarantee.Run(path, kind, recursive, fullControl,
                    step => Progress(step.Message));

                if (!string.IsNullOrEmpty(resultFile))
                {
                    try { File.WriteAllLines(resultFile, result.ToWire()); } catch { }
                }
                return result.Ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"[WORKER] Falhou: {ex}");
                if (!string.IsNullOrEmpty(resultFile))
                {
                    try
                    {
                        var r = new KitLugia.Core.GuaranteeResult { Summary = "Erro no worker: " + ex.Message, Failed = 1 };
                        r.Errors.Add(ex.Message);
                        File.WriteAllLines(resultFile, r.ToWire());
                    }
                    catch { }
                }
                return 2;
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
