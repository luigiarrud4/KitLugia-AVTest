using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace KitLugia.Core;

/// <summary>
/// Servidor Relay do KitLugia - Similar ao e4mc mas independente
/// Funciona com QUALQUER jogo, não apenas Minecraft
/// </summary>
public sealed class KitLugiaRelayServer : IDisposable
{
    private readonly TcpListener _tcpListener;
    private readonly ConcurrentDictionary<string, RelaySession> _sessions;
    private bool _isRunning = false;

    public event Action<string>? OnLogMessage;

    public KitLugiaRelayServer(int port = 25575)
    {
        _tcpListener = new TcpListener(IPAddress.Any, port);
        _sessions = new ConcurrentDictionary<string, RelaySession>();
    }

    /// <summary>
    /// Inicia o servidor relay
    /// </summary>
    public async Task StartAsync()
    {
        try
        {
            OnLogMessage?.Invoke("🚀 Iniciando KitLugia Relay Server...");
            
            _tcpListener.Start();
            _isRunning = true;

            OnLogMessage?.Invoke($"✅ Relay Server rodando na porta {_tcpListener.LocalEndpoint}");

            // Aceitar conexões continuamente
            while (_isRunning)
            {
                try
                {
                    var client = await _tcpListener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                    {
                        OnLogMessage?.Invoke($"⚠️ Erro ao aceitar cliente: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"❌ Erro ao iniciar relay: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Para o servidor relay
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
        _tcpListener.Stop();
        
        // Fechar todas as sessões
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();
        
        OnLogMessage?.Invoke("⏹️ Relay Server parado");
    }

    /// <summary>
    /// Cria uma nova sessão de relay
    /// </summary>
    public string CreateSession(string hostGame, int hostPort)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var session = new RelaySession(sessionId, hostGame, hostPort);
        
        _sessions.TryAdd(sessionId, session);
        OnLogMessage?.Invoke($"🎮 Sessão criada: {sessionId} para {hostGame}:{hostPort}");
        
        return sessionId;
    }

    /// <summary>
    /// Obtém URL pública para uma sessão
    /// </summary>
    public string GetSessionUrl(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            // Em produção, usaríamos domínio real
            // Por agora, usa IP local + porta específica
            var publicIp = GetPublicIP();
            return $"{publicIp}:25575/{sessionId}";
        }
        
        return string.Empty;
    }

    /// <summary>
    /// Manipula cliente conectado
    /// </summary>
    private async Task HandleClientAsync(TcpClient client)
    {
        var endPoint = client.Client.RemoteEndPoint as IPEndPoint;
        OnLogMessage?.Invoke($"👤 Cliente conectado: {endPoint}");

        try
        {
            using var stream = client.GetStream();
            var buffer = new byte[4096];

            // Primeiro: ler qual sessão o cliente quer
            var sessionIdBytes = new byte[8];
            var bytesRead = await stream.ReadAsync(sessionIdBytes, 0, 8);
            
            if (bytesRead < 8)
            {
                OnLogMessage?.Invoke("❌ Cliente não enviou ID da sessão");
                return;
            }

            var sessionId = System.Text.Encoding.ASCII.GetString(sessionIdBytes).Trim('\0');
            
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                OnLogMessage?.Invoke($"❌ Sessão não encontrada: {sessionId}");
                return;
            }

            // Adicionar cliente à sessão
            session.AddClient(client);
            OnLogMessage?.Invoke($"✅ Cliente adicionado à sessão {sessionId}");

            // Repassar pacotes bidirecionalmente
            await RelayPacketsAsync(session, client, stream);
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"⚠️ Erro no cliente {endPoint}: {ex.Message}");
        }
        finally
        {
            client.Close();
        }
    }

    /// <summary>
    /// Repassa pacotes entre clientes
    /// </summary>
    private async Task RelayPacketsAsync(RelaySession session, TcpClient client, NetworkStream stream)
    {
        var buffer = new byte[4096];

        try
        {
            while (client.Connected && _isRunning)
            {
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                
                if (bytesRead == 0)
                    break;

                // Repassar para todos os outros clientes da sessão
                var packet = new byte[bytesRead];
                Array.Copy(buffer, packet, bytesRead);
                
                await session.RelayToOthersAsync(client, packet);
            }
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"⚠️ Erro no relay: {ex.Message}");
        }
        finally
        {
            session.RemoveClient(client);
        }
    }

    /// <summary>
    /// Obtém IP público (simplificado)
    /// </summary>
    private string GetPublicIP()
    {
        // Em produção, obter IP real
        // Por agora, usa localhost para testes
        return "localhost";
    }

    /// <summary>
    /// Obtém status do servidor
    /// </summary>
    public string GetStatus()
    {
        if (!_isRunning)
            return "❌ Relay Server inativo";

        return $"✅ Relay Server ativo - {_sessions.Count} sessões";
    }

    public void Dispose()
    {
        Stop();
    }
}

/// <summary>
/// Sessão de relay para um jogo específico
/// </summary>
public sealed class RelaySession : IDisposable
{
    private readonly string _sessionId;
    private readonly string _hostGame;
    private readonly int _hostPort;
    private readonly ConcurrentBag<TcpClient> _clients;
    private TcpClient? _hostConnection;

    public string SessionId => _sessionId;
    public string HostGame => _hostGame;
    public int HostPort => _hostPort;

    public RelaySession(string sessionId, string hostGame, int hostPort)
    {
        _sessionId = sessionId;
        _hostGame = hostGame;
        _hostPort = hostPort;
        _clients = new ConcurrentBag<TcpClient>();
        
        // Conectar ao jogo host
        _ = Task.Run(ConnectToHostAsync);
    }

    /// <summary>
    /// Conecta ao jogo host (localhost:porta)
    /// </summary>
    private async Task ConnectToHostAsync()
    {
        try
        {
            _hostConnection = new TcpClient();
            await _hostConnection.ConnectAsync("localhost", _hostPort);
            
            // Começar a repassar pacotes do host para os clientes
            _ = Task.Run(() => RelayFromHostAsync());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Erro ao conectar ao host {_hostGame}:{_hostPort}: {ex.Message}");
        }
    }

    /// <summary>
    /// Adiciona cliente à sessão
    /// </summary>
    public void AddClient(TcpClient client)
    {
        _clients.Add(client);
    }

    /// <summary>
    /// Remove cliente da sessão
    /// </summary>
    public void RemoveClient(TcpClient client)
    {
        try
        {
            client.Close();
        }
        catch { }
    }

    /// <summary>
    /// Repassa pacotes para todos os clientes exceto o remetente
    /// </summary>
    public async Task RelayToOthersAsync(TcpClient sender, byte[] packet)
    {
        var tasks = new List<Task>();

        foreach (var client in _clients)
        {
            if (client != sender && client.Connected)
            {
                try
                {
                    var stream = client.GetStream();
                    tasks.Add(stream.WriteAsync(packet, 0, packet.Length));
                }
                catch
                {
                    // Cliente desconectado, será removido na próxima verificação
                }
            }
        }

        // Também repassar para o host se conectado
        if (_hostConnection?.Connected == true && sender != _hostConnection)
        {
            try
            {
                var stream = _hostConnection.GetStream();
                tasks.Add(stream.WriteAsync(packet, 0, packet.Length));
            }
            catch { }
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Repassa pacotes do host para os clientes
    /// </summary>
    private async Task RelayFromHostAsync()
    {
        if (_hostConnection == null) return;

        try
        {
            var stream = _hostConnection.GetStream();
            var buffer = new byte[4096];

            while (_hostConnection.Connected)
            {
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                
                if (bytesRead == 0)
                    break;

                var packet = new byte[bytesRead];
                Array.Copy(buffer, packet, bytesRead);
                
                await RelayToOthersAsync(_hostConnection, packet);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Erro no relay do host: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _hostConnection?.Close();
        
        foreach (var client in _clients)
        {
            try
            {
                client.Close();
            }
            catch { }
        }
    }
}
