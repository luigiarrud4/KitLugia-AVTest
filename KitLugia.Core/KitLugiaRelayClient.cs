using System;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace KitLugia.Core;

/// <summary>
/// Cliente Relay do KitLugia - Conecta jogos ao nosso relay
/// Funciona como ponte transparente entre qualquer jogo e o relay
/// </summary>
public sealed class KitLugiaRelayClient : IDisposable
{
    private readonly string _relayHost;
    private readonly int _relayPort;
    private TcpClient? _relayConnection;
    private TcpClient? _gameConnection;
    private bool _isRunning = false;

    public event Action<string>? OnLogMessage;
    public event Action<string>? OnSessionCreated;

    public KitLugiaRelayClient(string relayHost = "localhost", int relayPort = 25575)
    {
        _relayHost = relayHost;
        _relayPort = relayPort;
    }

    /// <summary>
    /// Inicia relay para um jogo específico
    /// </summary>
    public async Task<(bool Success, string SessionUrl, string Message)> StartRelayAsync(string gameHost, int gamePort)
    {
        try
        {
            OnLogMessage?.Invoke($"🚀 Iniciando relay para {gameHost}:{gamePort}...");

            // 1. Conectar ao jogo local
            _gameConnection = new TcpClient();
            await _gameConnection.ConnectAsync(gameHost, gamePort);
            OnLogMessage?.Invoke($"✅ Conectado ao jogo: {gameHost}:{gamePort}");

            // 2. Conectar ao servidor relay
            _relayConnection = new TcpClient();
            await _relayConnection.ConnectAsync(_relayHost, _relayPort);
            OnLogMessage?.Invoke($"✅ Conectado ao relay: {_relayHost}:{_relayPort}");

            // 3. Criar sessão no relay
            var sessionId = await CreateSessionAsync();
            if (string.IsNullOrEmpty(sessionId))
            {
                return (false, "", "Falha ao criar sessão no relay");
            }

            // 4. Enviar ID da sessão para o relay
            await SendSessionIdAsync(sessionId);

            // 5. Iniciar repasse de pacotes
            _isRunning = true;
            _ = Task.Run(() => RelayFromGameToRelayAsync());
            _ = Task.Run(() => RelayFromRelayToGameAsync());

            // 6. Gerar URL pública
            var sessionUrl = $"kitlugia.link:{_relayPort}/{sessionId}";
            OnSessionCreated?.Invoke(sessionUrl);

            OnLogMessage?.Invoke($"✅ Relay ativo! URL: {sessionUrl}");

            return (true, sessionUrl, "Relay iniciado com sucesso");
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"❌ Erro ao iniciar relay: {ex.Message}");
            return (false, "", $"Falha ao iniciar relay: {ex.Message}");
        }
    }

    /// <summary>
    /// Para o relay
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
        
        _gameConnection?.Close();
        _relayConnection?.Close();
        
        _gameConnection = null;
        _relayConnection = null;
        
        OnLogMessage?.Invoke("⏹️ Relay parado");
    }

    /// <summary>
    /// Cria sessão no relay
    /// </summary>
    private async Task<string?> CreateSessionAsync()
    {
        try
        {
            // Em uma implementação real, faríamos uma chamada HTTP/REST
            // Por agora, simulamos com um ID simples
            var sessionId = Guid.NewGuid().ToString("N")[..8];
            OnLogMessage?.Invoke($"🎮 Sessão criada: {sessionId}");
            return sessionId;
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"❌ Erro ao criar sessão: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Envia ID da sessão para o relay
    /// </summary>
    private async Task SendSessionIdAsync(string sessionId)
    {
        if (_relayConnection == null) return;

        try
        {
            var stream = _relayConnection.GetStream();
            var sessionIdBytes = System.Text.Encoding.ASCII.GetBytes(sessionId.PadRight(8, '\0'));
            await stream.WriteAsync(sessionIdBytes, 0, sessionIdBytes.Length);
            OnLogMessage?.Invoke($"📤 ID da sessão enviado: {sessionId}");
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"❌ Erro ao enviar ID da sessão: {ex.Message}");
        }
    }

    /// <summary>
    /// Repassa pacotes do jogo para o relay
    /// </summary>
    private async Task RelayFromGameToRelayAsync()
    {
        if (_gameConnection == null || _relayConnection == null) return;

        try
        {
            var gameStream = _gameConnection.GetStream();
            var relayStream = _relayConnection.GetStream();
            var buffer = new byte[4096];

            while (_isRunning && _gameConnection.Connected)
            {
                var bytesRead = await gameStream.ReadAsync(buffer, 0, buffer.Length);
                
                if (bytesRead == 0)
                    break;

                // Enviar para o relay
                await relayStream.WriteAsync(buffer, 0, bytesRead);
            }
        }
        catch (Exception ex)
        {
            if (_isRunning)
            {
                OnLogMessage?.Invoke($"⚠️ Erro no relay jogo→relay: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Repassa pacotes do relay para o jogo
    /// </summary>
    private async Task RelayFromRelayToGameAsync()
    {
        if (_gameConnection == null || _relayConnection == null) return;

        try
        {
            var relayStream = _relayConnection.GetStream();
            var gameStream = _gameConnection.GetStream();
            var buffer = new byte[4096];

            while (_isRunning && _relayConnection.Connected)
            {
                var bytesRead = await relayStream.ReadAsync(buffer, 0, buffer.Length);
                
                if (bytesRead == 0)
                    break;

                // Enviar para o jogo
                await gameStream.WriteAsync(buffer, 0, bytesRead);
            }
        }
        catch (Exception ex)
        {
            if (_isRunning)
            {
                OnLogMessage?.Invoke($"⚠️ Erro no relay relay→jogo: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Verifica se o relay está ativo
    /// </summary>
    public bool IsActive()
    {
        return _isRunning && 
               _gameConnection?.Connected == true && 
               _relayConnection?.Connected == true;
    }

    /// <summary>
    /// Obtém status atual
    /// </summary>
    public string GetStatus()
    {
        if (!_isRunning)
            return "❌ Relay inativo";

        var gameStatus = _gameConnection?.Connected == true ? "✅" : "❌";
        var relayStatus = _relayConnection?.Connected == true ? "✅" : "❌";

        return $"🔄 Relay ativo - Jogo: {gameStatus} - Relay: {relayStatus}";
    }

    public void Dispose()
    {
        Stop();
    }
}
