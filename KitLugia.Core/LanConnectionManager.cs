using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KitLugia.Core;

/// <summary>
/// Gerenciador completo de conexão LAN com fallback automático.
/// Seleciona roteador físico ativo e tenta métodos em sequência até conseguir.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LanConnectionManager : IDisposable
{
    private CancellationTokenSource? _cts;
    private NetworkExposureManager? _upnpManager;
    private HolePunchingManager? _holePunchingManager;
    private KitTunnelManager? _tunnelManager;
    
    public event Action<string>? OnLogMessage;
    public event Action<string, string>? OnStatusChanged; // status, cor
    public event Action<string>? OnSuccess; // IP:PORTA final
    public event Action? OnFailure;
    
    public enum ConnectionMethod
    {
        None,
        UPnP,           // Método 1: UPnP no roteador físico
        HolePunching,   // Método 2: UDP/TCP hole punching
        KitTunnel,      // Método 3: Túnel KitLugia
        RelayServer     // Método 4: Servidor relay
    }
    
    public class NetworkAdapterInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public NetworkInterfaceType Type { get; set; }
        public string LocalIP { get; set; } = "";
        public string GatewayIP { get; set; } = "";
        public bool IsUp { get; set; }
        public long Speed { get; set; }
    }
    
    public class GamePortInfo
    {
        public int Port { get; set; }
        public string ProcessName { get; set; } = "";
        public string GameName { get; set; } = "";
        public string Protocol { get; set; } = "TCP";
    }
    
    /// <summary>
    /// Obtém todos os adaptadores de rede físicos ativos com gateway - MÉTODO APRIMORADO
    /// </summary>
    public List<NetworkAdapterInfo> GetActivePhysicalAdapters()
    {
        var adapters = new List<NetworkAdapterInfo>();
        
        // Lista expandida de palavras-chave para interfaces virtuais
        var virtualKeywords = new[] { 
            "virtual", "vpn", "loopback", "tap", "hyper-v", "vmware", "vbox", "wsl", "docker", 
            "pseudo", "tunnel", "bluetooth", "teredo", "isatap", "6to4", "pptp", "l2tp", 
            "sstp", "ikev2", "openvpn", "wireguard", "nordvpn", "expressvpn", "surfshark",
            "tunnelbear", "protonvpn", "cyberghost", "hotspot", "connectify", "microsoft km-test",
            "software loopback", "microsoft isatap", "teredo tunneling", "bluetooth device"
        };
        
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                // 1. Apenas interfaces ativas
                .Where(i => i.OperationalStatus == OperationalStatus.Up)
                // 2. Apenas Ethernet ou WiFi (físicos)
                .Where(i => i.NetworkInterfaceType == NetworkInterfaceType.Ethernet || 
                           i.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                           i.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet)
                // 3. Excluir interfaces virtuais (filtro robusto)
                .Where(i => !virtualKeywords.Any(keyword => 
                    i.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    i.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    i.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                // 4. Deve ter gateway IPv4 válido
                .Where(i => i.GetIPProperties().GatewayAddresses.Any(g => 
                    g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !string.IsNullOrWhiteSpace(g.Address.ToString()) &&
                    !g.Address.ToString().StartsWith("0.") &&
                    !g.Address.ToString().StartsWith("127.") &&
                    !g.Address.ToString().StartsWith("169.254.")))
                // 5. Deve ter IP local válido
                .Where(i => i.GetIPProperties().UnicastAddresses.Any(a => 
                    a.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !string.IsNullOrWhiteSpace(a.Address.ToString()) &&
                    !a.Address.ToString().StartsWith("0.") &&
                    !a.Address.ToString().StartsWith("127.") &&
                    !a.Address.ToString().StartsWith("169.254.")))
                // 6. Priorizar por velocidade e tipo
                .OrderByDescending(i => i.Speed)
                .ThenBy(i => i.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
                .ToList();
            
            foreach (var ni in interfaces)
            {
                var props = ni.GetIPProperties();
                var ipv4 = props.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                                       !string.IsNullOrWhiteSpace(a.Address.ToString()));
                var gateway = props.GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                       !string.IsNullOrWhiteSpace(g.Address.ToString()));
                
                if (ipv4 != null && gateway != null)
                {
                    adapters.Add(new NetworkAdapterInfo
                    {
                        Id = ni.Id,
                        Name = ni.Name,
                        Description = ni.Description,
                        Type = ni.NetworkInterfaceType,
                        LocalIP = ipv4.Address.ToString(),
                        GatewayIP = gateway.Address.ToString(),
                        IsUp = ni.OperationalStatus == OperationalStatus.Up,
                        Speed = ni.Speed
                    });
                }
            }
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"❌ Erro ao listar adaptadores: {ex.Message}");
        }
        
        return adapters;
    }
    
    /// <summary>
    /// Detecta portas de jogos abertas automaticamente (Minecraft, etc.)
    /// </summary>
    public List<GamePortInfo> DetectGamePorts()
    {
        var gamePorts = new List<GamePortInfo>();
        
        try
        {
            // Mapear processos conhecidos
            var knownGames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["javaw.exe"] = "Minecraft Java",
                ["java.exe"] = "Minecraft Java",
                ["minecraft.exe"] = "Minecraft Bedrock",
                ["terraria.exe"] = "Terraria",
                ["starbound.exe"] = "Starbound",
                ["factorio.exe"] = "Factorio",
                ["dontstarve.exe"] = "Don't Starve",
                ["dst.exe"] = "Don't Starve Together",
                ["valheim.exe"] = "Valheim",
                ["rust.exe"] = "Rust",
                ["csgo.exe"] = "CS:GO",
                ["hl2.exe"] = "Source Engine",
                ["gmod.exe"] = "Garry's Mod",
                ["arma3.exe"] = "Arma 3",
                ["arma3server.exe"] = "Arma 3 Server"
            };
            
            // Obter conexões TCP ativas na porta de escuta
            var ipGlobalProps = IPGlobalProperties.GetIPGlobalProperties();
            var tcpListeners = ipGlobalProps.GetActiveTcpListeners();
            
            foreach (var endpoint in tcpListeners)
            {
                // Portas típicas de jogos: 1024-65535, excluindo portas de sistema
                if (endpoint.Port < 1024 || endpoint.Port > 65535) continue;
                
                // Verificar se é um processo de jogo
                var processInfo = GetProcessForPort(endpoint.Port);
                if (!string.IsNullOrEmpty(processInfo.ProcessName))
                {
                    string gameName = knownGames.GetValueOrDefault(processInfo.ProcessName, processInfo.ProcessName);
                    
                    gamePorts.Add(new GamePortInfo
                    {
                        Port = endpoint.Port,
                        ProcessName = processInfo.ProcessName,
                        GameName = gameName,
                        Protocol = "TCP"
                    });
                }
            }
            
            // Ordenar por nome do jogo
            gamePorts = gamePorts.OrderBy(g => g.GameName).ToList();
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"⚠️ Erro ao detectar portas de jogos: {ex.Message}");
        }
        
        return gamePorts;
    }
    
    private (string ProcessName, int ProcessId) GetProcessForPort(int port)
    {
        try
        {
            // Usar netstat para obter o PID
            var psi = new ProcessStartInfo
            {
                FileName = "netstat.exe",
                Arguments = $"-ano -p TCP | findstr :{port}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            using var proc = Process.Start(psi);
            string output = proc?.StandardOutput.ReadToEnd() ?? "";
            proc?.WaitForExit();
            
            // Parsing do output do netstat
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                // Formato: TCP    0.0.0.0:PORTA    0.0.0.0:0    LISTENING    PID
                var parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5 && parts[3] == "LISTENING")
                {
                    if (int.TryParse(parts[4], out int pid) && pid > 0)
                    {
                        var process = Process.GetProcessById(pid);
                        return (process.ProcessName, pid);
                    }
                }
            }
        }
        catch { }
        
        return (string.Empty, 0);
    }
    
    /// <summary>
    /// Inicia conexão LAN com fallback automático entre métodos
    /// </summary>
    public async Task<(bool Success, string Endpoint, ConnectionMethod MethodUsed)> 
        ConnectWithFallbackAsync(int localPort, int preferredMethod = 0)
    {
        _cts = new CancellationTokenSource();
        
        OnLogMessage?.Invoke("🚀 Iniciando KitLugia LAN Auto-Connect...");
        OnStatusChanged?.Invoke("🔍 Analisando rede...", "Yellow");
        
        // 1. Detectar adaptador ativo
        var adapters = GetActivePhysicalAdapters();
        if (!adapters.Any())
        {
            OnLogMessage?.Invoke("❌ Nenhum adaptador de rede físico encontrado");
            OnStatusChanged?.Invoke("❌ Sem adaptador de rede", "Red");
            return (false, string.Empty, ConnectionMethod.None);
        }
        
        var primaryAdapter = adapters.First();
        OnLogMessage?.Invoke($"📡 Adaptador selecionado: {primaryAdapter.Name}");
        OnLogMessage?.Invoke($"   IP Local: {primaryAdapter.LocalIP}");
        OnLogMessage?.Invoke($"   Gateway: {primaryAdapter.GatewayIP}");
        OnLogMessage?.Invoke($"   Tipo: {(primaryAdapter.Type == NetworkInterfaceType.Wireless80211 ? "WiFi" : "Ethernet")}");
        
        // MÉTODO 1: UPnP no gateway específico
        if (preferredMethod <= 0)
        {
            OnLogMessage?.Invoke("");
            OnLogMessage?.Invoke("🔧 MÉTODO 1: Tentando UPnP no roteador...");
            OnStatusChanged?.Invoke("🌐 Tentando UPnP...", "Yellow");
            
            var upnpResult = await TryUPnPAsync(primaryAdapter, localPort);
            if (upnpResult.Success)
            {
                OnLogMessage?.Invoke("✅ UPnP funcionou!");
                OnStatusChanged?.Invoke("✅ Conectado via UPnP", "Green");
                OnSuccess?.Invoke(upnpResult.Endpoint);
                return (true, upnpResult.Endpoint, ConnectionMethod.UPnP);
            }
            
            OnLogMessage?.Invoke($"⚠️ UPnP falhou: {upnpResult.Error}");
        }
        
        // MÉTODO 2: Hole Punching
        if (preferredMethod <= 1 && !_cts.IsCancellationRequested)
        {
            OnLogMessage?.Invoke("");
            OnLogMessage?.Invoke("🔧 MÉTODO 2: Tentando Conexão Direta (Hole Punching)...");
            OnStatusChanged?.Invoke("🎯 Tentando Hole Punching...", "Yellow");
            
            var punchResult = await TryHolePunchingAsync(localPort);
            if (punchResult.Success)
            {
                OnLogMessage?.Invoke("✅ Hole Punching funcionou!");
                OnStatusChanged?.Invoke("✅ Conectado via P2P", "Green");
                OnSuccess?.Invoke(punchResult.Endpoint);
                return (true, punchResult.Endpoint, ConnectionMethod.HolePunching);
            }
            
            OnLogMessage?.Invoke($"⚠️ Hole Punching falhou: {punchResult.Error}");
        }
        
        // MÉTODO 3: KitLugia Tunnel
        if (preferredMethod <= 2 && !_cts.IsCancellationRequested)
        {
            OnLogMessage?.Invoke("");
            OnLogMessage?.Invoke("🔧 MÉTODO 3: Tentando Túnel KitLugia...");
            OnStatusChanged?.Invoke("🚀 Tentando Túnel...", "Yellow");
            
            var tunnelResult = await TryKitTunnelAsync(localPort);
            if (tunnelResult.Success)
            {
                OnLogMessage?.Invoke("✅ Túnel KitLugia funcionou!");
                OnStatusChanged?.Invoke("✅ Conectado via Túnel", "Green");
                OnSuccess?.Invoke(tunnelResult.Endpoint);
                return (true, tunnelResult.Endpoint, ConnectionMethod.KitTunnel);
            }
            
            OnLogMessage?.Invoke($"⚠️ Túnel falhou: {tunnelResult.Error}");
        }
        
        // MÉTODO 4: Relay Server
        if (preferredMethod <= 3 && !_cts.IsCancellationRequested)
        {
            OnLogMessage?.Invoke("");
            OnLogMessage?.Invoke("🔧 MÉTODO 4: Tentando Servidor Relay...");
            OnStatusChanged?.Invoke("🌐 Tentando Relay...", "Yellow");
            
            var relayResult = await TryRelayServerAsync(localPort);
            if (relayResult.Success)
            {
                OnLogMessage?.Invoke("✅ Relay Server funcionou!");
                OnStatusChanged?.Invoke("✅ Conectado via Relay", "Green");
                OnSuccess?.Invoke(relayResult.Endpoint);
                return (true, relayResult.Endpoint, ConnectionMethod.RelayServer);
            }
            
            OnLogMessage?.Invoke($"⚠️ Relay falhou: {relayResult.Error}");
        }
        
        // Todos os métodos falharam
        OnLogMessage?.Invoke("");
        OnLogMessage?.Invoke("❌ Todos os métodos de conexão falharam");
        OnStatusChanged?.Invoke("❌ Falha na conexão", "Red");
        OnFailure?.Invoke();
        
        return (false, string.Empty, ConnectionMethod.None);
    }
    
    private async Task<(bool Success, string Endpoint, string Error)> TryUPnPAsync(NetworkAdapterInfo adapter, int localPort)
    {
        try
        {
            _upnpManager = new NetworkExposureManager();
            _upnpManager.OnLogMessage += msg => OnLogMessage?.Invoke("   " + msg);
            
            // Tentar descoberta específica no gateway
            OnLogMessage?.Invoke($"   Procurando UPnP em {adapter.GatewayIP}...");
            
            var init = await _upnpManager.InitializeAsync(10);
            if (!init.Success)
            {
                return (false, string.Empty, init.Message);
            }
            
            // Verificar CGNAT
            if (NetworkExposureManager.IsUnderCGNAT(init.PublicIP))
            {
                return (false, string.Empty, "CGNAT detectado - UPnP não funciona");
            }
            
            // Abrir porta
            var result = await _upnpManager.ExposePortAsync(
                localPort, ProtocolType.Tcp, "KitLugia LAN", 120);
            
            if (result.Success)
            {
                return (true, result.ExternalEndpoint, string.Empty);
            }
            
            return (false, string.Empty, result.Message);
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }
    
    private async Task<(bool Success, string Endpoint, string Error)> TryHolePunchingAsync(int localPort)
    {
        try
        {
            _holePunchingManager = new HolePunchingManager();
            _holePunchingManager.OnLogMessage += msg => OnLogMessage?.Invoke("   " + msg);
            
            var result = await _holePunchingManager.StartHolePunchingAsync(localPort);
            
            if (result.Success)
            {
                return (true, result.PublicEndpoint, string.Empty);
            }
            
            return (false, string.Empty, result.Message);
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }
    
    private async Task<(bool Success, string Endpoint, string Error)> TryKitTunnelAsync(int localPort)
    {
        try
        {
            _tunnelManager = new KitTunnelManager();
            _tunnelManager.OnLogMessage += msg => OnLogMessage?.Invoke("   " + msg);
            
            var result = await _tunnelManager.StartTunnelAsync(localPort);
            
            if (result.Success)
            {
                return (true, result.PublicUrl, string.Empty);
            }
            
            return (false, string.Empty, result.Message);
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }
    
    private async Task<(bool Success, string Endpoint, string Error)> TryRelayServerAsync(int localPort)
    {
        try
        {
            // Por enquanto, usar o mesmo tunnel manager mas forçar modo relay
            _tunnelManager = new KitTunnelManager();
            _tunnelManager.OnLogMessage += msg => OnLogMessage?.Invoke("   " + msg);
            
            // Aqui teria lógica específica de relay
            // Por enquanto, retornar falha para não enganar usuário
            return (false, string.Empty, "Servidor Relay em desenvolvimento");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }
    
    /// <summary>
    /// Força uso de método específico
    /// </summary>
    public async Task<(bool Success, string Endpoint)> ForceMethodAsync(
        ConnectionMethod method, int localPort, NetworkAdapterInfo? adapter = null)
    {
        switch (method)
        {
            case ConnectionMethod.UPnP:
                var upnp = await TryUPnPAsync(adapter ?? GetActivePhysicalAdapters().First(), localPort);
                return (upnp.Success, upnp.Endpoint);
            case ConnectionMethod.HolePunching:
                var punch = await TryHolePunchingAsync(localPort);
                return (punch.Success, punch.Endpoint);
            case ConnectionMethod.KitTunnel:
                var tunnel = await TryKitTunnelAsync(localPort);
                return (tunnel.Success, tunnel.Endpoint);
            case ConnectionMethod.RelayServer:
                var relay = await TryRelayServerAsync(localPort);
                return (relay.Success, relay.Endpoint);
            default:
                return (false, string.Empty);
        }
    }
    
    public void Disconnect()
    {
        OnLogMessage?.Invoke("🔒 Desconectando...");
        
        _cts?.Cancel();
        _upnpManager?.Dispose();
        _holePunchingManager?.Dispose();
        _tunnelManager?.Dispose();
        
        OnStatusChanged?.Invoke("🔒 Desconectado", "Gray");
    }
    
    public void Dispose()
    {
        Disconnect();
        _cts?.Dispose();
    }
}
