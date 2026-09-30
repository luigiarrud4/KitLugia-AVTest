using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using KitLugia.Core;
using Application = System.Windows.Application;

namespace KitLugia.GUI.Services
{
    /// <summary>
    /// Named pipe IPC server that receives --unlock commands from new Kit instances.
    /// When the user right-clicks a file → Force Stop Unlock while the Kit is already running,
    /// the new instance sends the path via this pipe and the existing instance opens the unlock window.
    /// </summary>
    public static class UnlockIpcServer
    {
        private const string PipeName = "KitLugia_UnlockIpc";
        private static CancellationTokenSource? _cts;

        /// <summary>
        /// Start listening for unlock commands from other instances.
        /// Call this once during app startup (e.g., in MainWindow.Loaded or App.OnStartup).
        /// </summary>
        public static void Start()
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            Task.Run(() => ListenLoop(token), token);
        }

        /// <summary>
        /// Stop the IPC server.
        /// </summary>
        public static void Stop()
        {
            _cts?.Cancel();
            _cts = null;
        }

        private static async Task ListenLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        1, // max 1 connection at a time
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    // Wait for a client connection
                    await server.WaitForConnectionAsync(token);

                    // Read command from the pipe: "UNLOCK|path" ou "TAKEOWN|path"
                    using var reader = new StreamReader(server);
                    string? raw = await reader.ReadLineAsync(token);

                    string cmd = "UNLOCK";
                    string path = raw ?? "";
                    int sep = path.IndexOf('|');
                    if (sep > 0)
                    {
                        cmd = path[..sep].ToUpperInvariant();
                        path = path[(sep + 1)..];
                    }

                    // PROVA-DE-TUDO: File.Exists/Directory.Exists retornam FALSE para caminhos
                    // que EXISTEM mas cuja ACL nega a leitura (C:\Windows.old, TrustedInstaller)
                    // — exatamente o alvo do menu de contexto. Sem o probe nativo o comando era
                    // descartado em SILÊNCIO ("cliquei e nada aconteceu").
                    bool pathOk = false;
                    try
                    {
                        pathOk = !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));
                        if (!pathOk && !string.IsNullOrEmpty(path))
                        {
                            FileTakeOwnership.ProbePath(path.TrimEnd('\\', '/'), out bool exists, out _, out int err);
                            pathOk = exists;
                            if (exists) Logger.Log($"[IPC] Alvo existe mas ACL nega leitura (erro {err}) — seguindo: {path}");
                        }
                    }
                    catch { pathOk = false; }

                    if (pathOk)
                    {
                        Logger.Log($"[IPC] Comando recebido: {cmd} → {path}");

                        string capturedCmd = cmd;
                        string capturedPath = path;
                        Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            if (capturedCmd == "TAKEOWN")
                                OpenTakeOwnership(capturedPath);
                            else
                                OpenUnlockWindow(capturedPath);
                        });
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.Log($"[IPC] Server error: {ex.Message}");
                    await Task.Delay(1000, token); // back off on error
                }
            }
        }

        /// <summary>
        /// Open the Force Stop Unlock window with a pre-filled path and auto-analyze.
        /// </summary>
        private static void OpenUnlockWindow(string path)
        {
            try
            {
                // Navigate within the existing Kit window.
                // IMPORTANTE (29/09): usar ShowAndActivateFromTray — a janela pode estar
                // HIDDEN na bandeja (CloseToTray), e Activate()/Focus() numa janela
                // invisível não mostra nada (era o "cliquei e o Kit não abriu").
                if (Application.Current.MainWindow is KitLugia.GUI.MainWindow mw)
                {
                    mw.ShowAndActivateFromTray();
                    mw.NavigateToUnlock(path);
                }
                else
                {
                    OpenFallbackWindow(path);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[IPC] Erro ao abrir unlock: {ex.Message}");
            }
        }

        /// <summary>
        /// Abre a página unificada na aba Take Ownership (reaproveita File Operations).
        /// Chamado pelo IPC quando o usuário clica "Take Ownership (KitLugia)" no Explorer.
        /// Reaproveita a mesma página do Force Stop — só muda a aba e pré-preenche.
        /// </summary>
        private static void OpenTakeOwnership(string path)
        {
            try
            {
                if (Application.Current.MainWindow is KitLugia.GUI.MainWindow mw)
                {
                    // Mostra a janela (mesmo escondida na bandeja) e navega para a aba
                    // Take Ownership já com o caminho — a ação começa na própria página.
                    mw.ShowAndActivateFromTray();
                    mw.NavigateToTakeOwn(path);
                }
                else
                {
                    // Sem MainWindow (não deve acontecer com o app normal): abre a janela
                    // dedicada em vez de executar a operação às escondidas — o usuário
                    // PRECISA ver que o clique fez algo.
                    OpenFallbackWindow(path);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[IPC] Erro take ownership: {ex.Message}");
            }
        }

        /// <summary>
        /// Último recurso quando não existe MainWindow: abre uma janela dedicada de
        /// Force Stop Unlock com o caminho pré-preenchido e dispara a análise.
        /// Nunca executa a operação de forma invisível.
        /// </summary>
        private static void OpenFallbackWindow(string path)
        {
            var win = new Windows.ForceStopUnlockWindow();
            win.Show();
            win.Loaded += async (s, e) =>
            {
                var txtPath = win.FindName("TxtPath") as System.Windows.Controls.TextBox;
                if (txtPath != null) txtPath.Text = path;
                await Task.Delay(200);
                var btn = win.FindName("BtnAnalyze") as System.Windows.Controls.Button;
                btn?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, btn));
            };
        }

        /// <summary>
        /// Envia comando TAKEOWN para a instância em execução (chamado pelo Program.cs).
        /// </summary>
        public static bool SendTakeOwnershipCommand(string path)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(2000);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine("TAKEOWN|" + path);
                Logger.Log($"[IPC] TakeOwnership enviado: {path}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[IPC] Falha ao enviar takeown: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Send an unlock command to the running instance via named pipe.
        /// Called by new instances when --unlock is detected and mutex is already held.
        /// Returns true if the command was sent successfully.
        /// </summary>
        public static bool SendUnlockCommand(string path)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(2000); // 2 second timeout

                using var writer = new StreamWriter(client);
                writer.WriteLine(path);
                writer.Flush();

                Logger.Log($"[IPC] Unlock command sent to existing instance: {path}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[IPC] Failed to send unlock command: {ex.Message}");
                return false;
            }
        }
    }
}
