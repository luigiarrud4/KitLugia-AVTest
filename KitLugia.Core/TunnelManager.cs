using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Versioning;

namespace KitLugia.Core;

/// <summary>
/// Gerenciador de tunnels (FastTunnel, NSmartProxy, Ngrok alternatives)
/// Para expor serviços quando UPnP/Hole Punching falham (CGNAT, etc.)
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TunnelManager : IDisposable
{
    private readonly List<TunnelProcess> _activeTunnels = new();
    private readonly HttpClient _httpClient = new();
    private CancellationTokenSource _cts = new CancellationTokenSource();
    
    public event Action<string>? OnLogMessage;
    public event Action<string>? OnTunnelEstablished; // URL pública
    public event Action<string>? OnTunnelClosed;

    // Configurações dos serviços de tunnel
    private readonly Dictionary<TunnelType, TunnelConfig> _tunnelConfigs = new()
    {
        [TunnelType.FastTunnel] = new()
        {
            Name = "FastTunnel",
            ExecutableName = "fasttunnel",
            DefaultPort = 8080,
            DownloadUrl = "https://github.com/SpringHgui/FastTunnel/releases",
            ConfigTemplate = "{\"server\":{\"address\":\"0.0.0.0\",\"port\":{0}},\"client\":{\"proxies\":[{{\"protocol\":\"tcp\",\"localPort\":{1},\"remotePort\":{2}}}]}}"
        },
        [TunnelType.NSmartProxy] = new()
        {
            Name = "NSmartProxy",
            ExecutableName = "nsp",
            DefaultPort = 8080,
            DownloadUrl = "https://github.com/tmoonlight/NSmartProxy/releases",
            ConfigTemplate = "{{\"serverPort\":{0},\"clientPort\":{1},\"remotePort\":{2}}}"
        },
        [TunnelType.LocalTunnel] = new()
        {
            Name = "LocalTunnel",
            ExecutableName = "lt",
            DefaultPort = 8080,
            DownloadUrl = "https://github.com/localtunnel/localtunnel/releases",
            ConfigTemplate = "--port {0} --subdomain kitlugia-{1}"
        }
    };

    /// <summary>
    /// Inicia um tunnel para expor porta local
    /// </summary>
    public async Task<(bool Success, string PublicUrl, string Message)> 
        CreateTunnelAsync(TunnelType tunnelType, int localPort, string? subdomain = null)
    {
        try
        {
            OnLogMessage?.Invoke($"🚀 Iniciando tunnel {tunnelType} para porta {localPort}...");

            if (!_tunnelConfigs.TryGetValue(tunnelType, out var config))
            {
                return (false, string.Empty, "❌ Tipo de tunnel não suportado.");
            }

            // 1. Verificar se executável existe
            var executablePath = await EnsureTunnelExecutableAsync(config);
            if (string.IsNullOrEmpty(executablePath))
            {
                return (false, string.Empty, $"❌ {config.Name} não encontrado. Instale manualmente.");
            }

            // 2. Gerar configuração
            var remotePort = GetAvailableRemotePort();
            var configContent = GenerateTunnelConfig(config, remotePort, localPort, subdomain);
            var configPath = Path.Combine(Path.GetTempPath(), $"kitlugia_tunnel_{tunnelType}_{DateTime.Now:yyyyMMddHHmmss}.json");
            await File.WriteAllTextAsync(configPath, configContent);

            // 3. Iniciar processo do tunnel
            var tunnelProcess = await StartTunnelProcessAsync(config, executablePath, configPath, localPort, remotePort);
            
            if (tunnelProcess != null)
            {
                _activeTunnels.Add(tunnelProcess);
                
                // 4. Aguardar tunnel estabelecer
                var publicUrl = await WaitForTunnelUrlAsync(tunnelProcess, tunnelType, remotePort);
                
                if (!string.IsNullOrEmpty(publicUrl))
                {
                    OnLogMessage?.Invoke($"✅ Tunnel ativo: {publicUrl}");
                    OnTunnelEstablished?.Invoke(publicUrl);
                    
                    return (true, publicUrl, 
                        $"✅ Tunnel {config.Name} ativo!\n" +
                        $"🌐 URL pública: {publicUrl}\n" +
                        $"🏠 Local: localhost:{localPort}\n" +
                        $"⏰ Tunnel permanecerá ativo enquanto o app estiver aberto.");
                }
                else
                {
                    // Falha ao estabelecer tunnel
                    await KillTunnelProcessAsync(tunnelProcess);
                    _activeTunnels.Remove(tunnelProcess);
                    
                    return (false, string.Empty, 
                        $"❌ Falha ao estabelecer tunnel {config.Name}.\n" +
                        "Verifique sua conexão ou tente outro serviço.");
                }
            }

            return (false, string.Empty, "❌ Falha ao iniciar processo do tunnel.");
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"❌ Erro no tunnel: {ex.Message}");
            return (false, string.Empty, $"❌ Erro: {ex.Message}");
        }
    }

    /// <summary>
    /// Obtém URL pública de tunnel existente
    /// </summary>
    public string? GetActiveTunnelUrl()
    {
        return _activeTunnels.Count > 0 ? _activeTunnels[0].PublicUrl : null;
    }

    /// <summary>
    /// Verifica se há tunnels ativos
    /// </summary>
    public bool HasActiveTunnels => _activeTunnels.Count > 0;

    /// <summary>
    /// Encerra todos os tunnels
    /// </summary>
    public async Task CloseAllTunnelsAsync()
    {
        OnLogMessage?.Invoke("🔒 Encerrando tunnels...");

        foreach (var tunnel in _activeTunnels.ToList())
        {
            try
            {
                await KillTunnelProcessAsync(tunnel);
                OnTunnelClosed?.Invoke(tunnel.PublicUrl ?? "unknown");
            }
            catch (Exception ex)
            {
                OnLogMessage?.Invoke($"⚠️ Erro ao encerrar tunnel: {ex.Message}");
            }
        }

        _activeTunnels.Clear();
        OnLogMessage?.Invoke("🔒 Todos os tunnels encerrados");
    }

    private async Task<string?> EnsureTunnelExecutableAsync(TunnelConfig config)
    {
        // 1. Verificar em PATH do sistema
        var inPath = await IsExecutableInPathAsync(config.ExecutableName);
        if (inPath)
        {
            return config.ExecutableName;
        }

        // 2. Verificar em pasta local do KitLugia
        var kitLugiaPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KitLugia", "Tunnels");
        
        var localExecutable = Path.Combine(kitLugiaPath, $"{config.ExecutableName}.exe");
        if (File.Exists(localExecutable))
        {
            return localExecutable;
        }

        // 3. Tentar baixar automaticamente (se implementado)
        OnLogMessage?.Invoke($"⬇️ {config.Name} não encontrado. Baixe manualmente de: {config.DownloadUrl}");
        
        return null;
    }

    private async Task<bool> IsExecutableInPathAsync(string executableName)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executableName,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process != null)
            {
                await Task.Run(() => process.WaitForExit(5000));
                return process.ExitCode == 0;
            }
        }
        catch { }

        return false;
    }

    private string GenerateTunnelConfig(TunnelConfig config, int remotePort, int localPort, string? subdomain)
    {
        var configJson = config.ConfigTemplate
            .Replace("{0}", remotePort.ToString())
            .Replace("{1}", localPort.ToString())
            .Replace("{2}", remotePort.ToString());

        if (!string.IsNullOrEmpty(subdomain))
        {
            configJson = configJson.Replace("{3}", subdomain);
        }

        return configJson;
    }

    private async Task<TunnelProcess?> StartTunnelProcessAsync(
        TunnelConfig config, 
        string executablePath, 
        string configPath, 
        int localPort, 
        int remotePort)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = GetTunnelArguments(config, configPath, localPort, remotePort),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(configPath) ?? Path.GetTempPath()
            };

            var process = Process.Start(startInfo);
            if (process != null)
            {
                var tunnelProcess = new TunnelProcess
                {
                    Process = process,
                    Type = Enum.Parse<TunnelType>(config.Name.Replace(" ", "")),
                    ConfigPath = configPath,
                    LocalPort = localPort,
                    RemotePort = remotePort,
                    StartedAt = DateTime.Now
                };

                // Iniciar monitoramento de output
                _ = Task.Run(() => MonitorTunnelOutputAsync(tunnelProcess));

                return tunnelProcess;
            }
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"❌ Erro ao iniciar {config.Name}: {ex.Message}");
        }

        return null;
    }

    private string GetTunnelArguments(TunnelConfig config, string configPath, int localPort, int remotePort)
    {
        return config.Name switch
        {
            "FastTunnel" => $"--config \"{configPath}\"",
            "NSmartProxy" => $"--config \"{configPath}\"",
            "LocalTunnel" => config.ConfigTemplate.Replace("{0}", localPort.ToString()),
            _ => $"--config \"{configPath}\""
        };
    }

    private async Task MonitorTunnelOutputAsync(TunnelProcess tunnel)
    {
        try
        {
            var process = tunnel.Process;
            var output = process.StandardOutput;
            var error = process.StandardError;

            while (!process.HasExited && !tunnel.CancellationTokenSource.Token.IsCancellationRequested)
            {
                var line = await output.ReadLineAsync();
                if (!string.IsNullOrEmpty(line))
                {
                    OnLogMessage?.Invoke($"[{tunnel.Type}] {line}");
                    
                    // Tentar extrair URL pública do output
                    var url = ExtractPublicUrlFromOutput(line, tunnel.Type);
                    if (!string.IsNullOrEmpty(url))
                    {
                        tunnel.PublicUrl = url;
                    }
                }

                var errorLine = await error.ReadLineAsync();
                if (!string.IsNullOrEmpty(errorLine))
                {
                    OnLogMessage?.Invoke($"[{tunnel.Type}] ERROR: {errorLine}");
                }

                await Task.Delay(100, tunnel.CancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"⚠️ Erro no monitoramento: {ex.Message}");
        }
    }

    private string? ExtractPublicUrlFromOutput(string output, TunnelType type)
    {
        // Padrões comuns de URLs de tunnel
        var patterns = type switch
        {
            TunnelType.FastTunnel => new[] { @"https?://[\w\.-]+:\d+", @"tunnel\.([\w\.-]+)" },
            TunnelType.NSmartProxy => new[] { @"https?://[\w\.-]+:\d+", @"proxy\.([\w\.-]+)" },
            TunnelType.LocalTunnel => new[] { @"https?://([\w\.-]+)\.localtunnel\.me", @"https?://[\w\.-]+\.loca\.lt" },
            _ => new[] { @"https?://[\w\.-]+:\d+" }
        };

        foreach (var pattern in patterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(output, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Value;
            }
        }

        return null;
    }

    private async Task<string?> WaitForTunnelUrlAsync(TunnelProcess tunnel, TunnelType type, int remotePort)
    {
        var timeout = TimeSpan.FromSeconds(30);
        var startTime = DateTime.Now;

        while (DateTime.Now - startTime < timeout && !tunnel.CancellationTokenSource.Token.IsCancellationRequested)
        {
            if (!string.IsNullOrEmpty(tunnel.PublicUrl))
            {
                return tunnel.PublicUrl;
            }

            // Tentar verificar se tunnel está respondendo
            if (await TestTunnelConnectivityAsync(tunnel.PublicUrl))
            {
                return tunnel.PublicUrl;
            }

            await Task.Delay(1000, tunnel.CancellationTokenSource.Token);
        }

        return null;
    }

    private async Task<bool> TestTunnelConnectivityAsync(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;

        try
        {
            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.BadRequest; // BadRequest pode indicar que tunnel está ativo
        }
        catch { }

        return false;
    }

    private int GetAvailableRemotePort()
    {
        // Portas comuns para tunnels
        var commonPorts = new[] { 8080, 8081, 8082, 8083, 8084, 8085, 9000, 9001, 9002 };
        var random = new Random();
        
        for (int i = 0; i < 10; i++)
        {
            var port = commonPorts[random.Next(commonPorts.Length)];
            
            // Verificar se porta está disponível
            if (IsPortAvailable(port))
            {
                return port;
            }
        }

        return 8080; // Fallback
    }

    private bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch { }

        return false;
    }

    private async Task KillTunnelProcessAsync(TunnelProcess tunnel)
    {
        try
        {
            if (!tunnel.Process.HasExited)
            {
                tunnel.CancellationTokenSource.Cancel();
                
                // Tentar encerrar gracefully
                tunnel.Process.Kill();
                await Task.Run(() => tunnel.Process.WaitForExit(5000));
            }

            // Limpar arquivo de configuração
            if (File.Exists(tunnel.ConfigPath))
            {
                try
                {
                    File.Delete(tunnel.ConfigPath);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"⚠️ Erro ao encerrar tunnel: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _httpClient?.Dispose();
        _ = Task.Run(CloseAllTunnelsAsync);
    }
}

// Classes auxiliares
public enum TunnelType
{
    FastTunnel,
    NSmartProxy,
    LocalTunnel,
    Ngrok
}

public class TunnelConfig
{
    public string Name { get; set; } = "";
    public string ExecutableName { get; set; } = "";
    public int DefaultPort { get; set; }
    public string DownloadUrl { get; set; } = "";
    public string ConfigTemplate { get; set; } = "";
}

public class TunnelProcess
{
    public Process Process { get; set; } = null!;
    public TunnelType Type { get; set; }
    public string ConfigPath { get; set; } = "";
    public int LocalPort { get; set; }
    public int RemotePort { get; set; }
    public DateTime StartedAt { get; set; }
    public string? PublicUrl { get; set; }
    public CancellationTokenSource CancellationTokenSource { get; set; } = new();
}
