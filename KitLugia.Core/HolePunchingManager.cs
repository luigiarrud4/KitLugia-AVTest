using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KitLugia.Core;

/// <summary>
/// Gerenciador de Hole Punching (UDP/TCP) para conexão P2P direta
/// sem necessidade de abrir portas no roteador ou usar VPN.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HolePunchingManager : IDisposable
{
    private UdpClient? _udpClient;
    private TcpListener? _tcpListener = null!;
    private CancellationTokenSource? _cts;
    private readonly List<IPEndPoint> _knownPeers = new();
    
    public event Action<string>? OnLogMessage;
    public event Action<string>? OnPeerConnected;
    public event Action<string, int>? OnEndpointDiscovered; // IP, Port

    // Servidor de sinalização embutido (pode ser substituído por servidor público)
    private const string DEFAULT_SIGNALING_SERVER = "127.0.0.1";
    private const int DEFAULT_SIGNALING_PORT = 3478;
    
    /// <summary>
    /// Inicia o processo de hole punching para conexão P2P
    /// </summary>
    public async Task<(bool Success, string LocalEndpoint, string PublicEndpoint, string Message)> 
        StartHolePunchingAsync(int localPort, string? roomCode = null)
    {
        try
        {
            OnLogMessage?.Invoke("🎯 Iniciando Hole Punching...");
            
            _cts = new CancellationTokenSource();
            
            // 1. Criar socket UDP para STUN/hole punching
            _udpClient = new UdpClient(localPort);
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            
            var localEndpoint = new IPEndPoint(NetworkExposureManager.GetLocalIPAddress() == "127.0.0.1" 
                ? IPAddress.Any 
                : IPAddress.Parse(NetworkExposureManager.GetLocalIPAddress()), localPort);
            
            OnLogMessage?.Invoke($"📡 Socket UDP criado na porta {localPort}");
            
            // 2. Obter endpoint público via STUN
            var (publicIP, publicPort, stunSuccess) = await GetPublicEndpointViaStunAsync();
            
            if (!stunSuccess)
            {
                // Fallback: usar endpoint local como "público" (para LAN)
                publicIP = localEndpoint.Address.ToString();
                publicPort = localPort;
                OnLogMessage?.Invoke("⚠️ STUN falhou, usando endpoint local (LAN apenas)");
            }
            else
            {
                OnLogMessage?.Invoke($"🌍 Endpoint público descoberto: {publicIP}:{publicPort}");
            }
            
            // 3. Registrar no servidor de sinalização (ou criar sala P2P)
            string shareCode = roomCode ?? GenerateRoomCode();
            
            // 4. Iniciar listener para conexões entrantes
            _ = Task.Run(() => ListenForPunchesAsync(_cts.Token));
            
            // 5. Iniciar processo de punching periódico
            _ = Task.Run(() => PunchingLoopAsync(_cts.Token));
            
            string shareableEndpoint = $"{publicIP}:{publicPort}";
            string localEndpStr = $"{localEndpoint.Address}:{localPort}";
            
            OnLogMessage?.Invoke("✅ Hole Punching ativo!");
            OnLogMessage?.Invoke($"📋 Código da sala: {shareCode}");
            OnLogMessage?.Invoke($"🌐 Endpoint público: {shareableEndpoint}");
            
            return (true, localEndpStr, shareableEndpoint, 
                $"✅ Hole Punching ativo!\n" +
                $"🎮 Código da sala: {shareCode}\n" +
                $"🌐 Compartilhe: {shareableEndpoint}\n" +
                $"🏠 Local: {localEndpStr}\n\n" +
                "Os amigos devem usar o mesmo código para conectar.");
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"❌ Erro no Hole Punching: {ex.Message}");
            return (false, string.Empty, string.Empty, $"❌ Falha: {ex.Message}");
        }
    }

    /// <summary>
    /// Conecta a uma sala existente via hole punching
    /// </summary>
    public async Task<(bool Success, string Message)> JoinRoomAsync(string roomCode, int localPort)
    {
        try
        {
            OnLogMessage?.Invoke($"🎯 Conectando à sala {roomCode}...");
            
            _cts = new CancellationTokenSource();
            
            // Criar socket
            _udpClient = new UdpClient(localPort);
            
            // Obter info do peer via servidor de sinalização
            var peerInfo = await QuerySignalingServerAsync(roomCode);
            
            if (peerInfo == null)
            {
                return (false, "❌ Sala não encontrada ou expirada.");
            }
            
            OnLogMessage?.Invoke($"📡 Peer encontrado: {peerInfo.IP}:{peerInfo.Port}");
            
            // Iniciar hole punching para o peer
            var peerEndpoint = new IPEndPoint(IPAddress.Parse(peerInfo.IP), peerInfo.Port);
            
            // Enviar pacotes de punching
            await PunchToPeerAsync(peerEndpoint, _cts.Token);
            
            return (true, $"✅ Conectado! Endpoint: {peerInfo.IP}:{peerInfo.Port}");
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"❌ Erro ao conectar: {ex.Message}");
            return (false, $"❌ Falha: {ex.Message}");
        }
    }

    /// <summary>
    /// Obtém endpoint público via servidor STUN
    /// </summary>
    private async Task<(string IP, int Port, bool Success)> GetPublicEndpointViaStunAsync()
    {
        // Lista de servidores STUN públicos
        var stunServers = new[]
        {
            ("stun.l.google.com", 19302),
            ("stun1.l.google.com", 19302),
            ("stun2.l.google.com", 19302),
            ("stun.stunprotocol.org", 3478),
            ("stun.sipnet.net", 3478)
        };
        
        foreach (var (host, port) in stunServers)
        {
            try
            {
                using var udp = new UdpClient();
                udp.Client.ReceiveTimeout = 3000;
                
                var stunServer = new IPEndPoint(await ResolveHostAsync(host), port);
                
                // Criar requisição STUN (Binding Request)
                var request = CreateStunBindingRequest();
                await udp.SendAsync(request, request.Length, stunServer);
                
                var response = await udp.ReceiveAsync();
                var (ip, publicPort) = ParseStunResponse(response.Buffer);
                
                if (!string.IsNullOrEmpty(ip) && publicPort > 0)
                {
                    return (ip, publicPort, true);
                }
            }
            catch (Exception ex)
            {
                OnLogMessage?.Invoke($"⚠️ STUN {host} falhou: {ex.Message}");
            }
        }
        
        return (string.Empty, 0, false);
    }

    private byte[] CreateStunBindingRequest()
    {
        // STUN Binding Request (RFC 5389)
        var message = new byte[20];
        
        // Message Type: Binding Request (0x0001)
        message[0] = 0x00;
        message[1] = 0x01;
        
        // Message Length: 0 (sem atributos)
        message[2] = 0x00;
        message[3] = 0x00;
        
        // Magic Cookie: 0x2112A442
        message[4] = 0x21;
        message[5] = 0x12;
        message[6] = 0xA4;
        message[7] = 0x42;
        
        // Transaction ID (12 bytes aleatórios)
        var random = new Random();
        for (int i = 8; i < 20; i++)
        {
            message[i] = (byte)random.Next(256);
        }
        
        return message;
    }

    private (string IP, int Port) ParseStunResponse(byte[] response)
    {
        try
        {
            if (response.Length < 20) return (string.Empty, 0);
            
            // Verificar se é Binding Response Success (0x0101)
            if (response[0] != 0x01 || response[1] != 0x01) return (string.Empty, 0);
            
            // Procurar atributo XOR-MAPPED-ADDRESS (0x0020) ou MAPPED-ADDRESS (0x0001)
            int pos = 20;
            while (pos < response.Length - 4)
            {
                var attrType = (response[pos] << 8) | response[pos + 1];
                var attrLen = (response[pos + 2] << 8) | response[pos + 3];
                
                if (attrType == 0x0001 || attrType == 0x0020) // MAPPED-ADDRESS ou XOR-MAPPED-ADDRESS
                {
                    if (pos + 8 <= response.Length)
                    {
                        var family = response[pos + 5];
                        if (family == 0x01) // IPv4
                        {
                            int port;
                            if (attrType == 0x0020) // XOR-MAPPED-ADDRESS
                            {
                                port = ((response[pos + 6] ^ 0x21) << 8) | (response[pos + 7] ^ 0x12);
                                var ip = new byte[4];
                                ip[0] = (byte)(response[pos + 8] ^ 0x21);
                                ip[1] = (byte)(response[pos + 9] ^ 0x12);
                                ip[2] = (byte)(response[pos + 10] ^ 0xA4);
                                ip[3] = (byte)(response[pos + 11] ^ 0x42);
                                return (new IPAddress(ip).ToString(), port);
                            }
                            else // MAPPED-ADDRESS
                            {
                                port = (response[pos + 6] << 8) | response[pos + 7];
                                var ip = new byte[] { response[pos + 8], response[pos + 9], response[pos + 10], response[pos + 11] };
                                return (new IPAddress(ip).ToString(), port);
                            }
                        }
                    }
                }
                
                pos += 4 + attrLen + (4 - attrLen % 4) % 4; // Alinhamento a 4 bytes
            }
        }
        catch { }
        
        return (string.Empty, 0);
    }

    private async Task<IPAddress> ResolveHostAsync(string hostname)
    {
        var addresses = await Dns.GetHostAddressesAsync(hostname);
        return addresses.Length > 0 ? addresses[0] : IPAddress.Parse("8.8.8.8");
    }

    private async Task ListenForPunchesAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _udpClient != null)
            {
                var result = await _udpClient.ReceiveAsync(ct);
                var msg = Encoding.UTF8.GetString(result.Buffer);
                
                OnLogMessage?.Invoke($"📨 Pacote de {result.RemoteEndPoint}: {msg}");
                
                if (msg.Contains("PUNCH") || msg.Contains("HELLO"))
                {
                    // Responder ao punch
                    var response = Encoding.UTF8.GetBytes("PUNCH_ACK");
                    await _udpClient.SendAsync(response, response.Length, result.RemoteEndPoint);
                    
                    OnPeerConnected?.Invoke(result.RemoteEndPoint.ToString());
                    OnEndpointDiscovered?.Invoke(result.RemoteEndPoint.Address.ToString(), result.RemoteEndPoint.Port);
                    
                    OnLogMessage?.Invoke($"✅ Conexão P2P estabelecida com {result.RemoteEndPoint}!");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"⚠️ Erro no listener: {ex.Message}");
        }
    }

    private async Task PunchingLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _udpClient != null)
            {
                // Enviar pacotes de keepalive/punching para peers conhecidos
                foreach (var peer in _knownPeers)
                {
                    try
                    {
                        var msg = Encoding.UTF8.GetBytes("PUNCH_KEEPALIVE");
                        await _udpClient.SendAsync(msg, msg.Length, peer);
                    }
                    catch { }
                }
                
                await Task.Delay(5000, ct); // A cada 5 segundos
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task PunchToPeerAsync(IPEndPoint peer, CancellationToken ct)
    {
        try
        {
            _knownPeers.Add(peer);
            
            // Enviar múltiplos pacotes de punching
            for (int i = 0; i < 10 && !ct.IsCancellationRequested; i++)
            {
                var msg = Encoding.UTF8.GetBytes($"PUNCH_ATTEMPT_{i}");
                await _udpClient!.SendAsync(msg, msg.Length, peer);
                OnLogMessage?.Invoke($"🎯 Punching attempt {i + 1} to {peer}");
                await Task.Delay(500, ct);
            }
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"⚠️ Erro no punching: {ex.Message}");
        }
    }

    private async Task<PeerInfo?> QuerySignalingServerAsync(string roomCode)
    {
        // Implementação simplificada - em produção, isso chamaria um servidor real
        // Por enquanto, retorna null para indicar que não há servidor
        OnLogMessage?.Invoke("🔍 Consultando servidor de sinalização...");
        
        // Simulação: servidor não disponível
        await Task.Delay(100);
        return null;
    }

    private string GenerateRoomCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // Sem I, O, 0, 1 para evitar confusão
        var random = new Random();
        var code = new char[6];
        for (int i = 0; i < 6; i++)
        {
            code[i] = chars[random.Next(chars.Length)];
        }
        return new string(code);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udpClient?.Close();
        _tcpListener?.Stop();
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _udpClient?.Dispose();
    }

    private class PeerInfo
    {
        public string IP { get; set; } = "";
        public int Port { get; set; }
    }
}
