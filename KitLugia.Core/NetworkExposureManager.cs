using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace KitLugia.Core;

/// <summary>
/// Gerenciador de exposição de rede via UPnP/NAT-PMP.
/// Permite abrir portas no roteador automaticamente para jogos LAN.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NetworkExposureManager : IDisposable
{
    private readonly List<PortMapping> _activeMappings = new();
    private CancellationTokenSource? _discoveryCts = null!;
    private bool _isInitialized = false;
    private string? _cachedPublicIp;
    private DateTime _lastDiscoveryAttempt = DateTime.MinValue;
    private readonly TimeSpan _discoveryCooldown = TimeSpan.FromSeconds(30);

    private bool IsInvalidIP(string ip)
    {
        return ip == "0.0.0.0" || ip == "127.0.0.1" || string.IsNullOrEmpty(ip);
    }

    private async Task<string?> GetExternalPublicIPAsync()
    {
        // Lista de serviços para detecção de IP público
        var services = new[]
        {
            ("https://api.ipify.org", "text/plain"),
            ("https://ipinfo.io/ip", "text/plain"),
            ("https://checkip.amazonaws.com", "text/plain"),
            ("https://icanhazip.com", "text/plain"),
            ("https://ipecho.net/plain", "text/plain")
        };

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(5);

        foreach (var (url, contentType) in services)
        {
            try
            {
                OnLogMessage?.Invoke($"🔍 Consultando serviço: {url}");
                
                var response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var ip = await response.Content.ReadAsStringAsync();
                    ip = ip.Trim();
                    
                    if (IPAddress.TryParse(ip, out var ipAddress) && !IsInvalidIP(ip))
                    {
                        OnLogMessage?.Invoke($"✅ IP válido obtido: {ip}");
                        return ip;
                    }
                }
            }
            catch (Exception ex)
            {
                OnLogMessage?.Invoke($"⚠️ Falha em {url}: {ex.Message}");
                continue;
            }
        }

        return null;
    }

    // Eventos para notificar a UI
    public event Action<string>? OnLogMessage;
    public event Action<string>? OnIpAddressCopied; // IP:PORTA copiado

    // ------------------------------------------------------------
    // 1. Inicialização e descoberta do roteador (UPnP/NAT-PMP)
    // ------------------------------------------------------------
    public async Task<(bool Success, string PublicIP, string Message)> InitializeAsync(int timeoutSeconds = 10)
    {
        try
        {
            // Verificar cooldown para não floodar
            if (DateTime.Now - _lastDiscoveryAttempt < _discoveryCooldown)
            {
                var remaining = _discoveryCooldown - (DateTime.Now - _lastDiscoveryAttempt);
                return (true, _cachedPublicIp ?? string.Empty, 
                    $"⏳ Aguarde {remaining.TotalSeconds:F0}s antes de tentar novamente.");
            }

            _lastDiscoveryAttempt = DateTime.Now;
            OnLogMessage?.Invoke("🔍 Procurando roteador com UPnP/NAT-PMP...");

            // Tentar UPnP primeiro
            var upnpResult = await TryDiscoverUPnP(timeoutSeconds);
            if (upnpResult.Success)
            {
                _isInitialized = true;
                _cachedPublicIp = upnpResult.PublicIP;
                OnLogMessage?.Invoke($"✅ Roteador UPnP encontrado! IP Público: {upnpResult.PublicIP}");
                return (true, upnpResult.PublicIP, $"✅ UPnP ativo | IP: {upnpResult.PublicIP}");
            }

            // Fallback para NAT-PMP
            var pmpResult = await TryDiscoverNatPMP(timeoutSeconds);
            if (pmpResult.Success && !IsInvalidIP(pmpResult.PublicIP))
            {
                _isInitialized = true;
                _cachedPublicIp = pmpResult.PublicIP;
                OnLogMessage?.Invoke($"✅ NAT-PMP encontrado! IP Público: {pmpResult.PublicIP}");
                return (true, pmpResult.PublicIP, $"✅ NAT-PMP ativo | IP: {pmpResult.PublicIP}");
            }

            // Fallback para serviços externos de IP
            OnLogMessage?.Invoke("🌐 UPnP/NAT-PMP falharam, tentando serviços externos...");
            var externalIP = await GetExternalPublicIPAsync();
            if (!string.IsNullOrEmpty(externalIP) && !IsInvalidIP(externalIP))
            {
                _isInitialized = true;
                _cachedPublicIp = externalIP;
                OnLogMessage?.Invoke($"✅ IP público via serviço externo: {externalIP}");
                return (true, externalIP, $"✅ IP detectado via serviço externo | IP: {externalIP}");
            }

            OnLogMessage?.Invoke("❌ Não foi possível detectar IP público");
            return (false, string.Empty, 
                "❌ Falha na detecção de IP público.\n" +
                "Verifique sua conexão com a internet.");
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"❌ Erro na descoberta: {ex.Message}");
            return (false, string.Empty, $"❌ Erro: {ex.Message}");
        }
    }

    private async Task<(bool Success, string PublicIP)> TryDiscoverUPnP(int timeoutSeconds)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            
            // SSDP discovery para UPnP
            var discoverer = new UPnPDiscoverer();
            var device = await discoverer.DiscoverDeviceAsync(cts.Token);
            
            if (device != null)
            {
                var publicIP = await device.GetExternalIPAsync();
                var ipString = publicIP?.ToString() ?? string.Empty;
                
                if (!IsInvalidIP(ipString))
                {
                    return (true, ipString);
                }
            }
        }
        catch { /* Ignorar e tentar NAT-PMP */ }
        
        return (false, string.Empty);
    }

    private async Task<(bool Success, string PublicIP)> TryDiscoverNatPMP(int timeoutSeconds)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            
            var discoverer = new NatPmpDiscoverer();
            var device = await discoverer.DiscoverDeviceAsync(cts.Token);
            
            if (device != null)
            {
                var publicIP = await device.GetExternalIPAsync();
                return (true, publicIP?.ToString() ?? string.Empty);
            }
        }
        catch { /* Falhou */ }
        
        return (false, string.Empty);
    }

    // ------------------------------------------------------------
    // 2. Expor uma porta (abrir port forwarding)
    // ------------------------------------------------------------
    public async Task<(bool Success, string ExternalEndpoint, string Message)> ExposePortAsync(
        int internalPort,
        ProtocolType protocol = ProtocolType.Tcp,
        string description = "KitLugia Server",
        int lifetimeMinutes = 120,
        int? requestedExternalPort = null)
    {
        if (!_isInitialized)
        {
            var init = await InitializeAsync();
            if (!init.Success)
                return (false, string.Empty, init.Message);
        }

        try
        {
            int externalPort = requestedExternalPort ?? internalPort;
            var localIP = GetLocalIPAddress();

            OnLogMessage?.Invoke($"🌐 Abrindo porta {externalPort} -> {localIP}:{internalPort} ({protocol})...");

            // Tentar UPnP primeiro
            var upnpResult = await TryMapPortUPnP(localIP, internalPort, externalPort, protocol, description, lifetimeMinutes);
            if (upnpResult.Success)
            {
                var mapping = new PortMapping
                {
                    InternalPort = internalPort,
                    ExternalPort = upnpResult.ActualExternalPort,
                    Protocol = protocol,
                    Description = description,
                    LocalIP = localIP,
                    ExpiresAt = DateTime.Now.AddMinutes(lifetimeMinutes),
                    IsActive = true
                };
                _activeMappings.Add(mapping);

                var endpoint = $"{_cachedPublicIp}:{upnpResult.ActualExternalPort}";
                OnLogMessage?.Invoke($"✅ Porta aberta: {endpoint}");
                OnIpAddressCopied?.Invoke(endpoint);
                
                return (true, endpoint, 
                    $"✅ Servidor público ativo!\n" +
                    $"🌐 IP externo: {endpoint}\n" +
                    $"🏠 Rede local: {localIP}:{internalPort}\n" +
                    $"⏱️ Expira em: {lifetimeMinutes} minutos");
            }

            // Fallback NAT-PMP
            var pmpResult = await TryMapPortNatPMP(localIP, internalPort, externalPort, protocol, description, lifetimeMinutes);
            if (pmpResult.Success)
            {
                var mapping = new PortMapping
                {
                    InternalPort = internalPort,
                    ExternalPort = pmpResult.ActualExternalPort,
                    Protocol = protocol,
                    Description = description,
                    LocalIP = localIP,
                    ExpiresAt = DateTime.Now.AddMinutes(lifetimeMinutes),
                    IsActive = true
                };
                _activeMappings.Add(mapping);

                var endpoint = $"{_cachedPublicIp}:{pmpResult.ActualExternalPort}";
                OnLogMessage?.Invoke($"✅ Porta aberta via NAT-PMP: {endpoint}");
                OnIpAddressCopied?.Invoke(endpoint);
                
                return (true, endpoint, 
                    $"✅ Servidor público ativo (NAT-PMP)!\n" +
                    $"🌐 IP externo: {endpoint}\n" +
                    $"🏠 Rede local: {localIP}:{internalPort}\n" +
                    $"⏱️ Expira em: {lifetimeMinutes} minutos");
            }

            return (false, string.Empty, "❌ Falha ao abrir porta. Verifique UPnP no roteador.");
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"❌ Erro ao expor porta: {ex.Message}");
            return (false, string.Empty, $"❌ Erro: {ex.Message}");
        }
    }

    private async Task<(bool Success, int ActualExternalPort)> TryMapPortUPnP(
        string localIP, int internalPort, int externalPort, ProtocolType protocol, 
        string description, int lifetimeMinutes)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var discoverer = new UPnPDiscoverer();
            var device = await discoverer.DiscoverDeviceAsync(cts.Token);
            
            if (device != null)
            {
                var success = await device.CreatePortMappingAsync(
                    localIP, internalPort, externalPort, 
                    protocol == ProtocolType.Udp ? "UDP" : "TCP",
                    description, lifetimeMinutes * 60);
                
                if (success)
                    return (true, externalPort);
            }
        }
        catch { }
        
        return (false, 0);
    }

    private async Task<(bool Success, int ActualExternalPort)> TryMapPortNatPMP(
        string localIP, int internalPort, int externalPort, ProtocolType protocol,
        string description, int lifetimeMinutes)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var discoverer = new NatPmpDiscoverer();
            var device = await discoverer.DiscoverDeviceAsync(cts.Token);
            
            if (device != null)
            {
                var actualPort = await device.CreatePortMappingAsync(
                    internalPort, externalPort, 
                    protocol == ProtocolType.Udp ? "UDP" : "TCP",
                    lifetimeMinutes * 60);
                
                if (actualPort > 0)
                    return (true, actualPort);
            }
        }
        catch { }
        
        return (false, 0);
    }

    // ------------------------------------------------------------
    // 3. Fechar portas abertas
    // ------------------------------------------------------------
    public async Task CloseAllPortsAsync()
    {
        OnLogMessage?.Invoke("🔒 Fechando portas abertas...");
        
        foreach (var mapping in _activeMappings.ToList())
        {
            try
            {
                await ClosePortAsync(mapping);
            }
            catch (Exception ex)
            {
                OnLogMessage?.Invoke($"⚠️ Erro ao fechar porta {mapping.ExternalPort}: {ex.Message}");
            }
        }
        
        _activeMappings.Clear();
        OnLogMessage?.Invoke("🔒 Todas as portas foram fechadas");
    }

    public async Task ClosePortAsync(PortMapping mapping)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            
            // Tentar UPnP
            var upnpDiscoverer = new UPnPDiscoverer();
            var upnpDevice = await upnpDiscoverer.DiscoverDeviceAsync(cts.Token);
            if (upnpDevice != null)
            {
                await upnpDevice.DeletePortMappingAsync(
                    mapping.ExternalPort,
                    mapping.Protocol == ProtocolType.Udp ? "UDP" : "TCP");
                mapping.IsActive = false;
                return;
            }

            // Tentar NAT-PMP
            var pmpDiscoverer = new NatPmpDiscoverer();
            var pmpDevice = await pmpDiscoverer.DiscoverDeviceAsync(cts.Token);
            if (pmpDevice != null)
            {
                await pmpDevice.DeletePortMappingAsync(
                    mapping.ExternalPort,
                    mapping.Protocol == ProtocolType.Udp ? "UDP" : "TCP");
                mapping.IsActive = false;
            }
        }
        catch { }
    }

    // ------------------------------------------------------------
    // 4. Detecção de CGNAT
    // ------------------------------------------------------------
    public static bool IsUnderCGNAT(string publicIP)
    {
        if (!IPAddress.TryParse(publicIP, out var ip)) return false;
        var bytes = ip.GetAddressBytes();
        
        // CGNAT range: 100.64.0.0/10
        if (bytes[0] == 100 && (bytes[1] & 0xC0) == 64) return true;
        
        // RFC 1918 ranges não são CGNAT mas também não são roteáveis diretamente
        // 10.0.0.0/8
        if (bytes[0] == 10) return true;
        // 172.16.0.0/12
        if (bytes[0] == 172 && (bytes[1] & 0xF0) == 16) return true;
        // 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        
        return false;
    }

    public string GetCGNATWarning(string publicIP)
    {
        if (IsUnderCGNAT(publicIP))
        {
            return "⚠️ ATENÇÃO: Você está em CGNAT (IP compartilhado).\n" +
                   "UPnP não funcionará. Opções:\n" +
                   "1. Contate seu provedor para liberar IP público\n" +
                   "2. Use VPN (ZeroTier/Radmin) como fallback\n" +
                   "3. Configure port forwarding manual no roteador";
        }
        return string.Empty;
    }

    // ------------------------------------------------------------
    // 5. Utilitários
    // ------------------------------------------------------------
    public static string GetLocalIPAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            var endPoint = socket.LocalEndPoint as IPEndPoint;
            return endPoint?.Address.ToString() ?? "127.0.0.1";
        }
        catch
        {
            // Fallback para primeira interface não-loopback
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    return ip.ToString();
            }
            return "127.0.0.1";
        }
    }

    public static async Task<bool> IsPortOpenAsync(string host, int port, int timeoutMs = 5000)
    {
        try
        {
            using var tcpClient = new TcpClient();
            var connectTask = tcpClient.ConnectAsync(host, port);
            var completedTask = await Task.WhenAny(connectTask, Task.Delay(timeoutMs));
            return completedTask == connectTask && tcpClient.Connected;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<PortMapping> GetActiveMappings() => _activeMappings.AsReadOnly();

    public bool HasActiveMappings => _activeMappings.Any(m => m.IsActive);

    // ------------------------------------------------------------
    // 6. Auto-renovação de portas
    // ------------------------------------------------------------
    public async Task RenewMappingsIfNeeded()
    {
        var mappingsToRenew = _activeMappings
            .Where(m => m.IsActive && m.ExpiresAt < DateTime.Now.AddMinutes(10))
            .ToList();

        foreach (var mapping in mappingsToRenew)
        {
            try
            {
                OnLogMessage?.Invoke($"🔄 Renovando porta {mapping.ExternalPort}...");
                var result = await ExposePortAsync(
                    mapping.InternalPort, 
                    mapping.Protocol, 
                    mapping.Description, 
                    120, 
                    mapping.ExternalPort);
                
                if (result.Success)
                {
                    mapping.IsActive = false; // Marcar antiga como inativa
                }
            }
            catch (Exception ex)
            {
                OnLogMessage?.Invoke($"⚠️ Falha ao renovar porta {mapping.ExternalPort}: {ex.Message}");
            }
        }

        // Limpar mapeamentos antigos
        _activeMappings.RemoveAll(m => !m.IsActive);
    }

    // ------------------------------------------------------------
    // 7. Limpeza
    // ------------------------------------------------------------
    public void Dispose()
    {
        try
        {
            CloseAllPortsAsync().GetAwaiter().GetResult();
        }
        catch { }
        
        _discoveryCts?.Cancel();
        _discoveryCts?.Dispose();
    }
}

// ------------------------------------------------------------
// Classes auxiliares para descoberta UPnP/NAT-PMP
// ------------------------------------------------------------

public class PortMapping
{
    public int InternalPort { get; set; }
    public int ExternalPort { get; set; }
    public ProtocolType Protocol { get; set; }
    public string Description { get; set; } = "";
    public string LocalIP { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public bool IsActive { get; set; }
}

// Implementação simplificada de UPnP discovery
public class UPnPDiscoverer
{
    private const string SSDPMsg = 
        "M-SEARCH * HTTP/1.1\r\n" +
        "HOST: 239.255.255.250:1900\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        "MX: 3\r\n" +
        "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n" +
        "\r\n";

    public async Task<UPnPDevice?> DiscoverDeviceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            
            var endPoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            var msgBytes = System.Text.Encoding.UTF8.GetBytes(SSDPMsg);
            
            await udpClient.SendAsync(msgBytes, msgBytes.Length, endPoint);
            
            var receiveTask = udpClient.ReceiveAsync();
            var completedTask = await Task.WhenAny(receiveTask, Task.Delay(3000, cancellationToken));
            
            if (completedTask == receiveTask)
            {
                var result = await receiveTask;
                var response = System.Text.Encoding.UTF8.GetString(result.Buffer);
                
                // Extrair LOCATION do response
                var location = ExtractHeader(response, "LOCATION");
                if (!string.IsNullOrEmpty(location))
                {
                    return new UPnPDevice(location);
                }
            }
        }
        catch { }
        
        return null;
    }

    private string ExtractHeader(string response, string headerName)
    {
        var lines = response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith(headerName + ":", StringComparison.OrdinalIgnoreCase))
            {
                return line.Substring(headerName.Length + 1).Trim();
            }
        }
        return string.Empty;
    }
}

public class UPnPDevice
{
    private readonly string _controlUrl;
    private string? _serviceType;
    private string? _serviceControlUrl;

    public UPnPDevice(string location)
    {
        _controlUrl = location;
    }

    public async Task<IPAddress?> GetExternalIPAsync()
    {
        try
        {
            await DiscoverServiceAsync();
            if (string.IsNullOrEmpty(_serviceControlUrl)) return null;

            var soapBody = "<?xml version=\"1.0\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                "<s:Body><u:GetExternalIPAddress xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\" />" +
                "</s:Body></s:Envelope>";

            using var client = new HttpClient();
            var content = new StringContent(soapBody);
            content.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:WANIPConnection:1#GetExternalIPAddress\"");
            
            var response = await client.PostAsync(_serviceControlUrl, content);
            var responseBody = await response.Content.ReadAsStringAsync();
            
            // Extrair IP da resposta XML
            var match = System.Text.RegularExpressions.Regex.Match(responseBody, 
                "<NewExternalIPAddress>([^<]+)</NewExternalIPAddress>");
            
            if (match.Success && IPAddress.TryParse(match.Groups[1].Value, out var ip))
            {
                return ip;
            }
        }
        catch { }
        
        return null;
    }

    public async Task<bool> CreatePortMappingAsync(string localIP, int internalPort, int externalPort, 
        string protocol, string description, int leaseDuration)
    {
        try
        {
            await DiscoverServiceAsync();
            if (string.IsNullOrEmpty(_serviceControlUrl)) return false;

            var soapBody = "<?xml version=\"1.0\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                "<s:Body><u:AddPortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">" +
                "<NewRemoteHost></NewRemoteHost>" +
                $"<NewExternalPort>{externalPort}</NewExternalPort>" +
                $"<NewProtocol>{protocol}</NewProtocol>" +
                $"<NewInternalPort>{internalPort}</NewInternalPort>" +
                $"<NewInternalClient>{localIP}</NewInternalClient>" +
                "<NewEnabled>1</NewEnabled>" +
                $"<NewPortMappingDescription>{System.Security.SecurityElement.Escape(description)}</NewPortMappingDescription>" +
                $"<NewLeaseDuration>{leaseDuration}</NewLeaseDuration>" +
                "</u:AddPortMapping></s:Body></s:Envelope>";

            using var client = new HttpClient();
            var content = new StringContent(soapBody);
            content.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:WANIPConnection:1#AddPortMapping\"");
            
            var response = await client.PostAsync(_serviceControlUrl, content);
            var responseBody = await response.Content.ReadAsStringAsync();
            
            return response.IsSuccessStatusCode && !responseBody.Contains("error");
        }
        catch { }
        
        return false;
    }

    public async Task<bool> DeletePortMappingAsync(int externalPort, string protocol)
    {
        try
        {
            await DiscoverServiceAsync();
            if (string.IsNullOrEmpty(_serviceControlUrl)) return false;

            var soapBody = "<?xml version=\"1.0\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                "<s:Body><u:DeletePortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">" +
                "<NewRemoteHost></NewRemoteHost>" +
                $"<NewExternalPort>{externalPort}</NewExternalPort>" +
                $"<NewProtocol>{protocol}</NewProtocol>" +
                "</u:DeletePortMapping></s:Body></s:Envelope>";

            using var client = new HttpClient();
            var content = new StringContent(soapBody);
            content.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:WANIPConnection:1#DeletePortMapping\"");
            
            var response = await client.PostAsync(_serviceControlUrl, content);
            return response.IsSuccessStatusCode;
        }
        catch { }
        
        return false;
    }

    private async Task DiscoverServiceAsync()
    {
        if (!string.IsNullOrEmpty(_serviceControlUrl)) return;

        try
        {
            using var client = new HttpClient();
            var response = await client.GetStringAsync(_controlUrl);
            
            // Parse XML para encontrar WANIPConnection service
            var match = System.Text.RegularExpressions.Regex.Match(response, 
                "<serviceType>(urn:schemas-upnp-org:service:WANIPConnection:[0-9]+)</serviceType>.*?<controlURL>([^<]+)</controlURL>",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            
            if (match.Success)
            {
                _serviceType = match.Groups[1].Value;
                var controlPath = match.Groups[2].Value;
                
                // Construir URL completa do control
                var baseUri = new Uri(_controlUrl);
                _serviceControlUrl = new Uri(baseUri, controlPath).ToString();
            }
        }
        catch { }
    }
}

// Implementação simplificada de NAT-PMP
public class NatPmpDiscoverer
{
    public async Task<NatPmpDevice?> DiscoverDeviceAsync(CancellationToken cancellationToken)
    {
        try
        {
            // NAT-PMP usa gateway padrão
            var gateway = GetDefaultGateway();
            if (gateway == null) return null;

            using var udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            
            var endPoint = new IPEndPoint(gateway, 5351);
            
            // NAT-PMP request para obter IP externo
            var request = new byte[] { 0, 0 }; // Versão 0, OP 0 (External IP)
            await udpClient.SendAsync(request, request.Length, endPoint);
            
            var receiveTask = udpClient.ReceiveAsync();
            var completedTask = await Task.WhenAny(receiveTask, Task.Delay(3000, cancellationToken));
            
            if (completedTask == receiveTask)
            {
                var result = await receiveTask;
                if (result.Buffer.Length >= 12 && result.Buffer[0] == 0)
                {
                    // Resposta válida
                    return new NatPmpDevice(gateway);
                }
            }
        }
        catch { }
        
        return null;
    }

    private IPAddress? GetDefaultGateway()
    {
        try
        {
            // Usar a mesma técnica do LanConnectionManager para detectar roteadores físicos
            var virtualKeywords = new List<string> { "virtual", "vpn", "loopback", "tap", "hyper-v", "vmware", "vbox", "wsl", "docker" };
            
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == OperationalStatus.Up)
                .Where(i => i.NetworkInterfaceType == NetworkInterfaceType.Ethernet || 
                           i.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .Where(i => !virtualKeywords.Any(keyword => 
                    i.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    i.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                .Where(i => i.GetIPProperties().GatewayAddresses.Any(g => 
                    g.Address.AddressFamily == AddressFamily.InterNetwork))
                .OrderByDescending(i => i.Speed)
                .ToList();

            foreach (var ni in interfaces)
            {
                var props = ni.GetIPProperties();
                var gateway = props.GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                
                if (gateway != null)
                {
                    return gateway.Address;
                }
            }
        }
        catch { }
        return null;
    }
}

public class NatPmpDevice
{
    private readonly IPAddress _gateway;

    public NatPmpDevice(IPAddress gateway)
    {
        _gateway = gateway;
    }

    public async Task<IPAddress?> GetExternalIPAsync()
    {
        try
        {
            using var udpClient = new UdpClient();
            var endPoint = new IPEndPoint(_gateway, 5351);
            
            var request = new byte[] { 0, 0 }; // Versão 0, OP 0
            await udpClient.SendAsync(request, request.Length, endPoint);
            
            var result = await udpClient.ReceiveAsync();
            if (result.Buffer.Length >= 12 && result.Buffer[0] == 0 && result.Buffer[1] == 128)
            {
                // Extrair IP dos bytes 8-11
                var ipBytes = new byte[] { result.Buffer[8], result.Buffer[9], result.Buffer[10], result.Buffer[11] };
                return new IPAddress(ipBytes);
            }
        }
        catch { }
        
        return null;
    }

    public async Task<int> CreatePortMappingAsync(int internalPort, int requestedExternalPort, 
        string protocol, int lifetimeSeconds)
    {
        try
        {
            using var udpClient = new UdpClient();
            var endPoint = new IPEndPoint(_gateway, 5351);
            
            // NAT-PMP Port Mapping Request
            var isUdp = protocol == "UDP";
            var opCode = (byte)(isUdp ? 1 : 2); // 1=UDP, 2=TCP
            
            var request = new byte[12];
            request[0] = 0; // Versão
            request[1] = opCode;
            request[2] = 0; // Reserved
            request[3] = 0; // Reserved
            
            // Internal port (big-endian)
            request[4] = (byte)(internalPort >> 8);
            request[5] = (byte)(internalPort & 0xFF);
            
            // Requested external port (big-endian)
            request[6] = (byte)(requestedExternalPort >> 8);
            request[7] = (byte)(requestedExternalPort & 0xFF);
            
            // Lifetime (big-endian)
            request[8] = (byte)((lifetimeSeconds >> 24) & 0xFF);
            request[9] = (byte)((lifetimeSeconds >> 16) & 0xFF);
            request[10] = (byte)((lifetimeSeconds >> 8) & 0xFF);
            request[11] = (byte)(lifetimeSeconds & 0xFF);
            
            await udpClient.SendAsync(request, request.Length, endPoint);
            
            var result = await udpClient.ReceiveAsync();
            if (result.Buffer.Length >= 16 && result.Buffer[0] == 0 && result.Buffer[1] == (opCode | 128))
            {
                // Sucesso - extrair porta real atribuída (bytes 6-7)
                var actualPort = (result.Buffer[6] << 8) | result.Buffer[7];
                return actualPort > 0 ? actualPort : requestedExternalPort;
            }
        }
        catch { }
        
        return 0;
    }

    public async Task<bool> DeletePortMappingAsync(int externalPort, string protocol)
    {
        // NAT-PMP: definir lifetime=0 para deletar
        var result = await CreatePortMappingAsync(0, externalPort, protocol, 0);
        return result > 0;
    }
}
