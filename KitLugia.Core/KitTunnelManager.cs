using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KitLugia.Core;

/// <summary>
/// Gerenciador de Túnel KitLugia - Cria túnel automático sem necessidade de configuração externa
/// Implementa protocolo similar a Ngrok/FRP mas 100% integrado ao KitLugia
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class KitTunnelManager : IDisposable
{
    private TcpListener? _localListener;
    private TcpClient? _tunnelClient = null!;
    private CancellationTokenSource? _cts;
    private bool _isActive = false;
    private string? _publicUrl;
    
    // Servidores relay públicos do KitLugia (podem ser substituídos por self-hosted)
    private readonly string[] RELAY_SERVERS = new[]
    {
        "relay.kitlugia.com",
        "relay1.kitlugia.net",
        "relay2.kitlugia.io"
    };
    
    public event Action<string>? OnLogMessage;
    public event Action<string>? OnPublicUrlReceived; // URL pública atribuída
    public event Action<long>? OnBytesTransferred; // bytes transferidos

    /// <summary>
    /// Inicia um túnel automático para a porta local especificada
    /// </summary>
    public async Task<(bool Success, string PublicUrl, string Message)> StartTunnelAsync(
        int localPort, 
        string? subdomain = null,
        string protocol = "tcp")
    {
        try
        {
            _cts = new CancellationTokenSource();
            
            OnLogMessage?.Invoke("🚀 Iniciando Túnel KitLugia...");
            OnLogMessage?.Invoke($"📡 Porta local: {localPort}");
            
            // Tentar conectar aos servidores relay
            var (relayHost, relayPort, relayConnected) = await ConnectToRelayAsync();
            
            if (!relayConnected)
            {
                // Fallback: criar servidor relay local/embeddado
                OnLogMessage?.Invoke("⚠️ Servidores relay offline, iniciando modo local...");
                return await StartLocalRelayModeAsync(localPort, subdomain, protocol);
            }
            
            // Registrar túnel no relay
            var registration = await RegisterTunnelAsync(relayHost!, relayPort, localPort, subdomain, protocol);
            
            if (!registration.Success)
            {
                return (false, string.Empty, $"❌ Falha ao registrar túnel: {registration.Error}");
            }
            
            _publicUrl = registration.PublicUrl;
            _isActive = true;
            
            OnPublicUrlReceived?.Invoke(_publicUrl);
            OnLogMessage?.Invoke($"✅ Túnel ativo! URL: {_publicUrl}");
            
            // Iniciar proxy de tráfego
            _ = Task.Run(() => RunTunnelProxyAsync(relayHost!, relayPort, localPort, _cts.Token));
            
            return (true, _publicUrl, 
                $"✅ Túnel KitLugia ativo!\n" +
                $"🌐 URL Pública: {_publicUrl}\n" +
                $"🏠 Redirecionando para: localhost:{localPort}\n" +
                $"📋 URL copiada para área de transferência!");
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"❌ Erro no túnel: {ex.Message}");
            return (false, string.Empty, $"❌ Falha: {ex.Message}");
        }
    }

    /// <summary>
    /// Modo local/relay embutido quando servidores externos não estão disponíveis
    /// </summary>
    private async Task<(bool Success, string PublicUrl, string Message)> StartLocalRelayModeAsync(
        int localPort, string? subdomain, string protocol)
    {
        try
        {
            OnLogMessage?.Invoke("🔧 Iniciando Relay Local Embutido...");
            
            // Obter IP público
            var publicIP = await GetPublicIPAsync();
            if (string.IsNullOrEmpty(publicIP))
            {
                return (false, string.Empty, "❌ Não foi possível obter IP público para relay local");
            }
            
            // Tentar usar UPnP para abrir porta no roteador (novamente)
            var networkExposure = new NetworkExposureManager();
            var init = await networkExposure.InitializeAsync(5);
            
            if (init.Success)
            {
                // Usar porta aleatória alta para relay
                var random = new Random();
                int relayPort = random.Next(20000, 65000);
                
                var portResult = await networkExposure.ExposePortAsync(
                    relayPort, System.Net.Sockets.ProtocolType.Tcp, "KitLugia Relay", 240);
                
                if (portResult.Success)
                {
                    // Extrair porta do endpoint
                    var parts = portResult.ExternalEndpoint.Split(':');
                    if (parts.Length == 2 && int.TryParse(parts[1], out int externalPort))
                    {
                        _publicUrl = $"{publicIP}:{externalPort}";
                        
                        // Iniciar servidor relay local
                        _ = Task.Run(() => RunLocalRelayServerAsync(externalPort, localPort, _cts!.Token));
                        
                        _isActive = true;
                        OnPublicUrlReceived?.Invoke(_publicUrl);
                        
                        return (true, _publicUrl,
                            $"✅ Relay Local ativo via UPnP!\n" +
                            $"🌐 Endpoint: {_publicUrl}\n" +
                            $"🏠 Redirecionando para porta local: {localPort}");
                    }
                }
            }
            
            // Último fallback: usar IP local e informar usuário
            var localIP = NetworkExposureManager.GetLocalIPAddress();
            _publicUrl = $"{localIP}:{localPort}";
            
            OnLogMessage?.Invoke("⚠️ Usando modo LAN apenas (sem exposição externa)");
            
            return (false, string.Empty, 
                "❌ Não foi possível criar túnel externo.\n" +
                "Verifique:\n" +
                "1. UPnP deve estar ativo no roteador\n" +
                "2. Ou configure port forwarding manual\n" +
                "3. Ou use método Hole Punching");
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"❌ Erro no relay local: {ex.Message}");
            return (false, string.Empty, $"❌ Falha: {ex.Message}");
        }
    }

    private async Task<(string? Host, int Port, bool Connected)> ConnectToRelayAsync()
    {
        foreach (var server in RELAY_SERVERS)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(server, 443);
                tcp.Close();
                
                return (server, 443, true);
            }
            catch
            {
                OnLogMessage?.Invoke($"⚠️ Servidor {server} indisponível");
            }
        }
        
        return (null, 0, false);
    }

    private async Task<(bool Success, string PublicUrl, string Error)> RegisterTunnelAsync(
        string relayHost, int relayPort, int localPort, string? subdomain, string protocol)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(relayHost, relayPort);
            
            var stream = client.GetStream();
            
            // Protocolo de registro simples
            var registration = new
            {
                Action = "register",
                Protocol = protocol,
                LocalPort = localPort,
                Subdomain = subdomain,
                ClientVersion = "1.0",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            
            var json = JsonSerializer.Serialize(registration);
            var data = Encoding.UTF8.GetBytes(json);
            var lengthBytes = BitConverter.GetBytes(data.Length);
            
            await stream.WriteAsync(lengthBytes);
            await stream.WriteAsync(data);
            
            // Ler resposta
            var responseLengthBytes = new byte[4];
            await stream.ReadExactlyAsync(responseLengthBytes);
            var responseLength = BitConverter.ToInt32(responseLengthBytes);
            
            var responseBytes = new byte[responseLength];
            await stream.ReadExactlyAsync(responseBytes);
            
            var response = JsonSerializer.Deserialize<TunnelRegistrationResponse>(
                Encoding.UTF8.GetString(responseBytes));
            
            if (response?.Success == true && !string.IsNullOrEmpty(response.PublicUrl))
            {
                return (true, response.PublicUrl, string.Empty);
            }
            
            return (false, string.Empty, response?.Error ?? "Resposta inválida do servidor");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }

    private async Task RunTunnelProxyAsync(string relayHost, int relayPort, int localPort, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var relayClient = new TcpClient();
                    await relayClient.ConnectAsync(relayHost, relayPort, ct);
                    
                    var relayStream = relayClient.GetStream();
                    
                    // Autenticar como cliente de túnel
                    var auth = new { Action = "tunnel_client", TunnelId = _publicUrl };
                    var authJson = JsonSerializer.Serialize(auth);
                    var authData = Encoding.UTF8.GetBytes(authJson);
                    await relayStream.WriteAsync(BitConverter.GetBytes(authData.Length), ct);
                    await relayStream.WriteAsync(authData, ct);
                    
                    // Conectar à porta local
                    using var localClient = new TcpClient();
                    await localClient.ConnectAsync("127.0.0.1", localPort, ct);
                    var localStream = localClient.GetStream();
                    
                    OnLogMessage?.Invoke("🔄 Nova conexão tunelada estabelecida");
                    
                    // Bidirectional proxy
                    await Task.WhenAny(
                        CopyStreamAsync(relayStream, localStream, ct),
                        CopyStreamAsync(localStream, relayStream, ct)
                    );
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        OnLogMessage?.Invoke($"⚠️ Erro na conexão do túnel: {ex.Message}");
                        await Task.Delay(1000, ct);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunLocalRelayServerAsync(int relayPort, int localPort, CancellationToken ct)
    {
        try
        {
            _localListener = new TcpListener(IPAddress.Any, relayPort);
            _localListener.Start();
            
            OnLogMessage?.Invoke($"🔧 Relay local ouvindo na porta {relayPort}");
            
            while (!ct.IsCancellationRequested)
            {
                var client = await _localListener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleRelayConnectionAsync(client, localPort, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"❌ Erro no relay local: {ex.Message}");
        }
    }

    private async Task HandleRelayConnectionAsync(TcpClient client, int localPort, CancellationToken ct)
    {
        try
        {
            using var localClient = new TcpClient();
            await localClient.ConnectAsync("127.0.0.1", localPort, ct);
            
            var clientStream = client.GetStream();
            var localStream = localClient.GetStream();
            
            OnLogMessage?.Invoke("🔄 Conexão relay → local estabelecida");
            
            await Task.WhenAny(
                CopyStreamAsync(clientStream, localStream, ct),
                CopyStreamAsync(localStream, clientStream, ct)
            );
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"⚠️ Erro na conexão relay: {ex.Message}");
        }
        finally
        {
            client.Close();
        }
    }

    private async Task CopyStreamAsync(Stream source, Stream destination, CancellationToken ct)
    {
        try
        {
            var buffer = new byte[8192];
            long totalBytes = 0;
            
            while (!ct.IsCancellationRequested)
            {
                var read = await source.ReadAsync(buffer, ct);
                if (read == 0) break;
                
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                totalBytes += read;
            }
            
            OnBytesTransferred?.Invoke(totalBytes);
        }
        catch (OperationCanceledException) { }
    }

    private async Task<string?> GetPublicIPAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            
            var services = new[]
            {
                "https://api.ipify.org",
                "https://icanhazip.com",
                "https://ifconfig.me/ip",
                "https://ident.me"
            };
            
            foreach (var service in services)
            {
                try
                {
                    var ip = await client.GetStringAsync(service);
                    ip = ip.Trim();
                    if (!string.IsNullOrEmpty(ip) && IPAddress.TryParse(ip, out _))
                    {
                        return ip;
                    }
                }
                catch { }
            }
        }
        catch { }
        
        return null;
    }

    public void Stop()
    {
        _isActive = false;
        _cts?.Cancel();
        _localListener?.Stop();
        _tunnelClient?.Close();
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _localListener?.Dispose();
        _tunnelClient?.Dispose();
    }

    public bool IsActive => _isActive;
    public string? PublicUrl => _publicUrl;

    private class TunnelRegistrationResponse
    {
        public bool Success { get; set; }
        public string? PublicUrl { get; set; }
        public string? Error { get; set; }
        public long ExpiresAt { get; set; }
    }
}
