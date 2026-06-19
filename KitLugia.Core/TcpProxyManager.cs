using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    /// Proxy TCP Reverso - Cria um servidor que escuta em uma porta pública
    /// e redireciona todo tráfego para um servidor local (ex: Minecraft LAN).
    /// 
    /// Funciona como Radmin/ZeroTier: cria um "túnel" TCP direto.
    /// O amigo conecta no IP:PUBLIC_PORT e é redirecionado para localhost:MINECRAFT_PORT
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class TcpProxyManager : IDisposable
    {
        private TcpListener? _listener;
        private readonly List<ProxyConnection> _activeConnections = new();
        private CancellationTokenSource? _cts;
        private bool _isRunning = false;
        private int _publicPort;
        private int _targetPort;
        private string _targetHost = "127.0.0.1";
        private long _totalBytesTransferred = 0;

        public event Action<string>? OnLogMessage;
        public event Action<string, int>? OnClientConnected; // IP, Port
        public event Action<long>? OnBytesTransferred;

        public bool IsRunning => _isRunning;
        public int PublicPort => _publicPort;
        public long TotalBytes => _totalBytesTransferred;
        public int ActiveConnections => _activeConnections.Count;

        /// <summary>
        /// Inicia o proxy TCP reverso
        /// </summary>
        /// <param name="publicPort">Porta que ficará aberta externamente</param>
        /// <param name="targetPort">Porta do servidor local (ex: Minecraft LAN)</param>
        /// <param name="targetHost">Host do servidor local (padrão: localhost)</param>
        public async Task<(bool Success, string Message)> StartProxyAsync(
            int publicPort, 
            int targetPort, 
            string targetHost = "127.0.0.1")
        {
            try
            {
                if (_isRunning)
                {
                    await StopProxyAsync();
                }

                _publicPort = publicPort;
                _targetPort = targetPort;
                _targetHost = targetHost;

                _cts = new CancellationTokenSource();

                // Criar listener na porta pública
                _listener = new TcpListener(IPAddress.Any, publicPort);
                _listener.Start();

                _isRunning = true;

                OnLogMessage?.Invoke($"🚀 Proxy TCP iniciado em 0.0.0.0:{publicPort}");
                OnLogMessage?.Invoke($"   ↳ Redirecionando para {targetHost}:{targetPort}");
                OnLogMessage?.Invoke($"   💡 Amigos conectam em IP_PUBLICO:{publicPort}");

                // Iniciar loop de aceitação
                _ = Task.Run(() => AcceptConnectionsAsync(_cts.Token));

                // Aguardar confirmação que o listener está ativo
                await Task.Delay(100);

                return (true, $"✅ Proxy ativo em :{publicPort} → {targetHost}:{targetPort}");
            }
            catch (Exception ex)
            {
                OnLogMessage?.Invoke($"❌ Erro ao iniciar proxy: {ex.Message}");
                _isRunning = false;
                return (false, $"❌ Falha: {ex.Message}");
            }
        }

        private async Task AcceptConnectionsAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    if (client == null) continue;

                    var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
                    var clientIP = remoteEndPoint?.Address?.ToString() ?? "desconhecido";
                    var clientPort = remoteEndPoint?.Port ?? 0;

                    OnLogMessage?.Invoke($"🔗 Cliente conectado: {clientIP}:{clientPort}");
                    OnClientConnected?.Invoke(clientIP, clientPort);

                    // Iniciar proxy em background para este cliente
                    var connection = new ProxyConnection(client, _targetHost, _targetPort, ct);
                    lock (_activeConnections) _activeConnections.Add(connection);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await connection.RunAsync();
                        }
                        finally
                        {
                            lock (_activeConnections)
                            {
                                _activeConnections.Remove(connection);
                                Interlocked.Add(ref _totalBytesTransferred, connection.BytesTransferred);
                            }
                            OnLogMessage?.Invoke($"🔒 Cliente desconectado: {clientIP}:{clientPort}");
                            OnBytesTransferred?.Invoke(_totalBytesTransferred);
                        }
                    });
                }
                catch (ObjectDisposedException) { break; }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                        OnLogMessage?.Invoke($"⚠️ Erro ao aceitar conexão: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Para o proxy
        /// </summary>
        public async Task StopProxyAsync()
        {
            OnLogMessage?.Invoke("🔒 Parando proxy...");

            _cts?.Cancel();

            // Fechar listener
            try { _listener?.Stop(); } catch { }
            _listener = null;

            // Fechar todas as conexões ativas
            List<ProxyConnection> connections;
            lock (_activeConnections)
            {
                connections = _activeConnections.ToList();
                _activeConnections.Clear();
            }

            foreach (var conn in connections)
            {
                try { conn.Dispose(); } catch { }
            }

            _isRunning = false;
            OnLogMessage?.Invoke("🔒 Proxy parado");
        }

        public void Dispose()
        {
            _ = StopProxyAsync();
            _cts?.Dispose();
        }

        /// <summary>
        /// Representa uma conexão proxy entre um cliente externo e o servidor local
        /// </summary>
        private class ProxyConnection : IDisposable
        {
            private readonly TcpClient _client;
            private readonly string _targetHost;
            private readonly int _targetPort;
            private readonly CancellationToken _ct;
            private TcpClient? _targetClient;
            private long _bytesTransferred = 0;

            public long BytesTransferred => _bytesTransferred;

            public ProxyConnection(TcpClient client, string targetHost, int targetPort, CancellationToken ct)
            {
                _client = client;
                _targetHost = targetHost;
                _targetPort = targetPort;
                _ct = ct;
            }

            public async Task RunAsync()
            {
                try
                {
                    // Conectar ao servidor local (Minecraft)
                    _targetClient = new TcpClient();
                    await _targetClient.ConnectAsync(_targetHost, _targetPort);

                    var clientStream = _client.GetStream();
                    var targetStream = _targetClient.GetStream();

                    // Bidirectional copy: cliente ↔ servidor
                    var task1 = CopyStreamAsync(clientStream, targetStream, _ct);
                    var task2 = CopyStreamAsync(targetStream, clientStream, _ct);

                    await Task.WhenAny(task1, task2);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Proxy connection error: {ex.Message}");
                }
                finally
                {
                    Dispose();
                }
            }

            private async Task CopyStreamAsync(NetworkStream from, NetworkStream to, CancellationToken ct)
            {
                var buffer = new byte[81920]; // 80KB buffer
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var bytesRead = await from.ReadAsync(buffer, ct);
                        if (bytesRead == 0) break;

                        await to.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                        await to.FlushAsync(ct);

                        Interlocked.Add(ref _bytesTransferred, bytesRead);
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }

            public void Dispose()
            {
                try { _client?.Close(); } catch { }
                try { _targetClient?.Close(); } catch { }
            }
        }
    }
}