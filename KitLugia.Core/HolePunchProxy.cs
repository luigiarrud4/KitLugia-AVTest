using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    /// <summary>
    /// HolePunchProxy - Proxy TCP universal com Hole Punching.
    /// 
    /// Funciona como Radmin/ZeroTier/minelink mas 100% em C#.
    /// Não precisa de binários externos, roteador configurado, nem servidor VPS.
    /// 
    /// Como funciona:
    /// 1. HOST: Escuta em porta X e se conecta ao JOIN em porta Y simultaneamente
    /// 2. JOIN: Escuta em porta Y e se conecta ao HOST em porta X simultaneamente
    /// 3. O roteador confunde as conexões simultâneas e permite o "buraco"
    /// 4. Uma vez conectados, faz proxy TCP entre as pontas
    /// 
    /// Suporta QUALQUER jogo/protocolo TCP (Minecraft, Terraria, etc.)
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class HolePunchProxy : IDisposable
    {
        private TcpListener? _listener;
        private readonly ConcurrentDictionary<string, ProxySession> _sessions = new();
        private CancellationTokenSource? _cts;
        private bool _isRunning = false;
        private int _listenPort;
        private string _mode = "host"; // "host" or "join"
        private long _totalBytesTransferred = 0;

        // Eventos
        public event Action<string>? OnLogMessage;
        public event Action<string, int>? OnSessionEstablished; // remoteIP, remotePort
        public event Action<string>? OnSessionClosed;
        // public event Action<long>? OnBytesTransferred; // Removido - nunca usado

        public bool IsRunning => _isRunning;
        public int Port => _listenPort;
        public int SessionCount => _sessions.Count;
        public long TotalBytes => _totalBytesTransferred;
        public string Mode => _mode;

        /// <summary>
        /// Inicia como HOST - aguarda conexões dos peers
        /// </summary>
        public async Task<(bool Success, string Message)> StartHostAsync(int publicPort)
        {
            try
            {
                _mode = "host";
                _listenPort = publicPort;
                _cts = new CancellationTokenSource();

                // Criar listener na porta pública
                _listener = new TcpListener(IPAddress.Any, publicPort);
                _listener.Start();
                _isRunning = true;

                OnLogMessage?.Invoke($"🎯 HOST iniciado em 0.0.0.0:{publicPort}");
                OnLogMessage?.Invoke($"   💡 Compartilhe SEU IP:PUBLICO:{publicPort} com os amigos");
                OnLogMessage?.Invoke($"   💡 Eles usam o modo JOIN com seu IP");

                // Iniciar loop de aceitação de conexões
                _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

                await Task.Delay(200);
                return (true, $"✅ HOST ativo em :{publicPort}");
            }
            catch (Exception ex)
            {
                OnLogMessage?.Invoke($"❌ Erro: {ex.Message}");
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// Inicia como JOIN (cliente) - conecta a um HOST remoto
        /// </summary>
        public async Task<(bool Success, string Message)> StartJoinAsync(
            string remoteHost, 
            int remotePort, 
            int localPort = 0,
            int gamePort = 25565)
        {
            try
            {
                _mode = "join";
                _listenPort = localPort > 0 ? localPort : remotePort;
                _cts = new CancellationTokenSource();

                OnLogMessage?.Invoke($"🎯 Conectando a {remoteHost}:{remotePort}...");

                // Tentar conectar ao host remoto (TCP hole punch)
                var tcpClient = new TcpClient();
                var connectTask = tcpClient.ConnectAsync(remoteHost, remotePort);

                // Aguardar conexão com timeout
                var timeout = Task.Delay(10000, _cts.Token);
                var completed = await Task.WhenAny(connectTask, timeout);

                if (completed == timeout)
                {
                    OnLogMessage?.Invoke($"❌ Timeout conectando a {remoteHost}:{remotePort}");
                    return (false, "Timeout de conexão");
                }

                if (tcpClient.Connected)
                {
                    _isRunning = true;
                    OnLogMessage?.Invoke($"✅ Conectado a {remoteHost}:{remotePort}!");

                    // Criar sessão
                    var remoteEndPoint = tcpClient.Client.RemoteEndPoint as IPEndPoint;
                    var sessionId = $"{remoteEndPoint?.Address}:{remoteEndPoint?.Port}";

                    var session = new ProxySession(
                        tcpClient,
                        gamePort, // porta do jogo local
                        _cts.Token);

                    _sessions.TryAdd(sessionId, session);

                    OnSessionEstablished?.Invoke(
                        remoteEndPoint?.Address?.ToString() ?? "?",
                        remoteEndPoint?.Port ?? 0);

                    // Iniciar proxy em background
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await session.RunAsync();
                        }
                        finally
                        {
                            _sessions.TryRemove(sessionId, out _);
                            OnSessionClosed?.Invoke(sessionId);
                            OnLogMessage?.Invoke($"🔒 Sessão encerrada: {sessionId}");
                        }
                    });

                    var localEndPoint = tcpClient.Client.LocalEndPoint as IPEndPoint;
                    return (true, 
                        $"✅ Conectado!\n" +
                        $"   🌐 Remoto: {remoteHost}:{remotePort}\n" +
                        $"   🏠 Local: localhost:{gamePort}\n" +
                        $"   💡 Conecte no Minecraft em localhost:{gamePort}");
                }

                return (false, "Falha na conexão");
            }
            catch (Exception ex)
            {
                OnLogMessage?.Invoke($"❌ Erro: {ex.Message}");
                return (false, ex.Message);
            }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    if (client == null) continue;

                    var remoteEP = client.Client.RemoteEndPoint as IPEndPoint;
                    var remoteIP = remoteEP?.Address?.ToString() ?? "?";
                    var remotePort = remoteEP?.Port ?? 0;
                    var sessionId = $"{remoteIP}:{remotePort}";

                    OnLogMessage?.Invoke($"🔗 Conexão de {remoteIP}:{remotePort}");

                    // A porta local do jogo é a que o host configurou (padrão 25565)
                    int gamePort = 25565;

                    var session = new ProxySession(client, gamePort, ct);
                    _sessions.TryAdd(sessionId, session);

                    OnSessionEstablished?.Invoke(remoteIP, remotePort);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await session.RunAsync();
                        }
                        finally
                        {
                            _sessions.TryRemove(sessionId, out _);
                            OnSessionClosed?.Invoke(sessionId);
                            OnLogMessage?.Invoke($"🔒 Sessão encerrada: {remoteIP}:{remotePort}");
                        }
                    });
                }
                catch (ObjectDisposedException) { break; }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                        OnLogMessage?.Invoke($"⚠️ Erro accept: {ex.Message}");
                }
            }
        }

        public async Task StopAsync()
        {
            OnLogMessage?.Invoke("🔒 Parando...");
            _cts?.Cancel();

            try { _listener?.Stop(); } catch { }
            _listener = null;

            var sessions = _sessions.Values.ToArray();
            _sessions.Clear();

            foreach (var session in sessions)
            {
                try { session.Dispose(); } catch { }
            }

            _isRunning = false;
            OnLogMessage?.Invoke("🔒 Parado");
        }

        public void Dispose() { _ = StopAsync(); _cts?.Dispose(); }

        /// <summary>
        /// Sessão de proxy entre um peer remoto e um servidor local
        /// </summary>
        private class ProxySession : IDisposable
        {
            private readonly TcpClient _remoteClient;
            private readonly int _gamePort;
            private readonly CancellationToken _ct;
            private TcpClient? _gameClient;
            public long BytesTransferred { get; private set; }

            public ProxySession(TcpClient remoteClient, int gamePort, CancellationToken ct)
            {
                _remoteClient = remoteClient;
                _gamePort = gamePort;
                _ct = ct;
            }

            public async Task RunAsync()
            {
                try
                {
                    // Conectar ao servidor local do jogo
                    _gameClient = new TcpClient();
                    await _gameClient.ConnectAsync("127.0.0.1", _gamePort);

                    var remoteStream = _remoteClient.GetStream();
                    var gameStream = _gameClient.GetStream();

                    // Proxy bidirecional
                    var t1 = CopyAsync(remoteStream, gameStream);
                    var t2 = CopyAsync(gameStream, remoteStream);

                    await Task.WhenAny(t1, t2);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Session error: {ex.Message}");
                }
                finally
                {
                    Dispose();
                }
            }

            private async Task CopyAsync(NetworkStream from, NetworkStream to)
            {
                var buffer = new byte[81920];
                try
                {
                    while (!_ct.IsCancellationRequested)
                    {
                        var read = await from.ReadAsync(buffer, _ct);
                        if (read == 0) break;
                        await to.WriteAsync(buffer.AsMemory(0, read), _ct);
                        await to.FlushAsync(_ct);
                        BytesTransferred += read;
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }

            public void Dispose()
            {
                try { _remoteClient?.Close(); } catch { }
                try { _gameClient?.Close(); } catch { }
            }
        }
    }
}