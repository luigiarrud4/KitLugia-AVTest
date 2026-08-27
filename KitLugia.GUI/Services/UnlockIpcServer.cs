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

                    if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
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
                // Navigate within the existing Kit window (like Guardian does)
                if (Application.Current.MainWindow is KitLugia.GUI.MainWindow mw)
                {
                    // Bring window to front
                    if (mw.WindowState == WindowState.Minimized)
                        mw.WindowState = WindowState.Normal;
                    mw.Activate();
                    mw.Focus();

                    // Navigate to ForceStopUnlock page and pass the path
                    mw.NavigateToUnlock(path);
                }
                else
                {
                    // Fallback: open a new window if MainWindow isn't available
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
                    if (mw.WindowState == WindowState.Minimized)
                        mw.WindowState = WindowState.Normal;
                    mw.Activate();
                    mw.Focus();
                    mw.NavigateToTakeOwn(path);
                }
                else
                {
                    // Fallback headless (sem MainWindow): executa direto e mostra toast quando possível
                    bool isDir = Directory.Exists(path);
                    string name = Path.GetFileName(path.TrimEnd('\\')) ?? path;
                    _ = Task.Run(() =>
                    {
                        var result = FileTakeOwnership.TakeOwn(path, recursive: isDir);
                        Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            if (Application.Current.MainWindow is KitLugia.GUI.MainWindow mw2)
                            {
                                if (result.Ok)
                                    mw2.ShowSuccess("TAKE OWNERSHIP", $"✅ {name}: {result.Success} item(ns) agora são seus." + (isDir ? "\n(Recursivo — incluiu todas as subpastas)" : ""));
                                else
                                    mw2.ShowError("TAKE OWNERSHIP", $"{name}: {result.Failed} falha(s) de {result.Total}.\n" + string.Join("\n", result.Errors.Take(3)));
                            }
                        });
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[IPC] Erro take ownership: {ex.Message}");
            }
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
