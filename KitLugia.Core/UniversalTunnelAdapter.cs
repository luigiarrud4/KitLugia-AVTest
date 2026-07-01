using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace KitLugia.Core;

/// <summary>
/// Adaptador Universal para Todos os Jogos
/// Usa túneis existentes (ngrok, playit, etc.) sem precisar de drivers
/// </summary>
public sealed class UniversalTunnelAdapter : IDisposable
{
    private Process? _tunnelProcess;
    private string? _publicUrl;
    private bool _isRunning = false;

    public event Action<string>? OnLogMessage;
    public event Action<string>? OnTunnelCreated;

    /// <summary>
    /// Cria túnel universal usando serviços existentes
    /// </summary>
    public async Task<(bool Success, string PublicUrl, string Message)> CreateUniversalTunnelAsync(int port, string serviceName = "playit")
    {
        try
        {
            OnLogMessage?.Invoke($"🚀 Criando túnel universal para porta {port}...");

            switch (serviceName.ToLower())
            {
                case "ngrok":
                    return await CreateNgrokTunnelAsync(port);
                case "playit":
                    return await CreatePlayitTunnelAsync(port);
                case "localtunnel":
                    return await CreateLocaltunnelTunnelAsync(port);
                default:
                    return await CreatePlayitTunnelAsync(port); // Default para playit
            }
        }
        catch (Exception ex)
        {
            return (false, "", $"Erro ao criar túnel: {ex.Message}");
        }
    }

    /// <summary>
    /// Cria túnel com Playit.gg (recomendado para jogos)
    /// </summary>
    private async Task<(bool Success, string PublicUrl, string Message)> CreatePlayitTunnelAsync(int port)
    {
        try
        {
            OnLogMessage?.Invoke("🎮 Usando Playit.gg (ótimo para jogos)...");

            // Verificar se playit está instalado
            var playitPath = await FindPlayitPathAsync();
            if (string.IsNullOrEmpty(playitPath))
            {
                // Baixar automaticamente
                OnLogMessage?.Invoke("📦 Baixando Playit.gg automaticamente...");
                playitPath = await DownloadPlayitAsync();
                if (string.IsNullOrEmpty(playitPath))
                {
                    return (false, "", "Não foi possível baixar Playit.gg");
                }
            }

            // Iniciar túnel
            var startInfo = new ProcessStartInfo
            {
                FileName = playitPath,
                Arguments = $"start tcp {port}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _tunnelProcess = Process.Start(startInfo);
            if (_tunnelProcess == null)
            {
                return (false, "", "Não foi possível iniciar o túnel");
            }

            // Aguardar e obter URL
            _isRunning = true;
            var publicUrl = await WaitForTunnelUrlAsync();
            
            if (!string.IsNullOrEmpty(publicUrl))
            {
                _publicUrl = publicUrl;
                OnLogMessage?.Invoke($"✅ Túnel criado: {publicUrl}");
                OnTunnelCreated?.Invoke(publicUrl);
                return (true, publicUrl, "Túnel criado com sucesso");
            }
            else
            {
                return (false, "", "Não foi possível obter URL do túnel");
            }
        }
        catch (Exception ex)
        {
            return (false, "", $"Erro no Playit.gg: {ex.Message}");
        }
    }

    /// <summary>
    /// Cria túnel com ngrok
    /// </summary>
    private async Task<(bool Success, string PublicUrl, string Message)> CreateNgrokTunnelAsync(int port)
    {
        try
        {
            OnLogMessage?.Invoke("🌐 Usando ngrok...");

            var ngrokPath = await FindNgrokPathAsync();
            if (string.IsNullOrEmpty(ngrokPath))
            {
                return (false, "", "ngrok não encontrado");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ngrokPath,
                Arguments = $"tcp {port}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _tunnelProcess = Process.Start(startInfo);
            if (_tunnelProcess == null)
            {
                return (false, "", "Não foi possível iniciar ngrok");
            }

            _isRunning = true;
            var publicUrl = await WaitForNgrokUrlAsync();
            
            if (!string.IsNullOrEmpty(publicUrl))
            {
                _publicUrl = publicUrl;
                OnLogMessage?.Invoke($"✅ Túnel ngrok: {publicUrl}");
                OnTunnelCreated?.Invoke(publicUrl);
                return (true, publicUrl, "Túnel ngrok criado");
            }
            else
            {
                return (false, "", "Não foi possível obter URL do ngrok");
            }
        }
        catch (Exception ex)
        {
            return (false, "", $"Erro no ngrok: {ex.Message}");
        }
    }

    /// <summary>
    /// Cria túnel com localtunnel
    /// </summary>
    private async Task<(bool Success, string PublicUrl, string Message)> CreateLocaltunnelTunnelAsync(int port)
    {
        try
        {
            OnLogMessage?.Invoke("🔌 Usando localtunnel...");

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c lt --port {port} --subdomain kitlugia-{Guid.NewGuid():N}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _tunnelProcess = Process.Start(startInfo);
            if (_tunnelProcess == null)
            {
                return (false, "", "Não foi possível iniciar localtunnel");
            }

            _isRunning = true;
            var publicUrl = await WaitForLocaltunnelUrlAsync();
            
            if (!string.IsNullOrEmpty(publicUrl))
            {
                _publicUrl = publicUrl;
                OnLogMessage?.Invoke($"✅ Túnel localtunnel: {publicUrl}");
                OnTunnelCreated?.Invoke(publicUrl);
                return (true, publicUrl, "Túnel localtunnel criado");
            }
            else
            {
                return (false, "", "Não foi possível obter URL do localtunnel");
            }
        }
        catch (Exception ex)
        {
            return (false, "", $"Erro no localtunnel: {ex.Message}");
        }
    }

    /// <summary>
    /// Aguarda URL do túnel (método genérico)
    /// </summary>
    private async Task<string?> WaitForTunnelUrlAsync()
    {
        if (_tunnelProcess == null) return null;

        try
        {
            var timeout = TimeSpan.FromSeconds(30);
            var startTime = DateTime.Now;

            while (DateTime.Now - startTime < timeout)
            {
                if (_tunnelProcess.HasExited)
                {
                    var error = await _tunnelProcess.StandardError.ReadToEndAsync();
                    OnLogMessage?.Invoke($"❌ Processo encerrou: {error}");
                    return null;
                }

                // Ler output
                var output = await _tunnelProcess.StandardOutput.ReadToEndAsync();
                
                // Procurar por URL no output
                var urlMatch = System.Text.RegularExpressions.Regex.Match(output, @"https?://[^\s]+");
                if (urlMatch.Success)
                {
                    return urlMatch.Value;
                }

                await Task.Delay(1000);
            }

            return null;
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"⚠️ Erro ao aguardar URL: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Aguarda URL específica do ngrok
    /// </summary>
    private async Task<string?> WaitForNgrokUrlAsync()
    {
        // ngrok tem formato específico: tcp://x.tcp.ngrok.io:port
        if (_tunnelProcess == null) return null;

        try
        {
            var timeout = TimeSpan.FromSeconds(30);
            var startTime = DateTime.Now;

            while (DateTime.Now - startTime < timeout)
            {
                if (_tunnelProcess.HasExited) return null;

                var output = await _tunnelProcess.StandardOutput.ReadToEndAsync();
                
                // ngrok: tcp://6.tcp.ngrok.io:12345
                var urlMatch = System.Text.RegularExpressions.Regex.Match(output, @"tcp://[^\s]+");
                if (urlMatch.Success)
                {
                    return urlMatch.Value;
                }

                await Task.Delay(1000);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Aguarda URL específica do localtunnel
    /// </summary>
    private async Task<string?> WaitForLocaltunnelUrlAsync()
    {
        if (_tunnelProcess == null) return null;

        try
        {
            var timeout = TimeSpan.FromSeconds(30);
            var startTime = DateTime.Now;

            while (DateTime.Now - startTime < timeout)
            {
                if (_tunnelProcess.HasExited) return null;

                var output = await _tunnelProcess.StandardOutput.ReadToEndAsync();
                
                // localtunnel: https://kitlugia-abc123.loca.lt
                var urlMatch = System.Text.RegularExpressions.Regex.Match(output, @"https://[^\s]*\.loca\.lt");
                if (urlMatch.Success)
                {
                    return urlMatch.Value;
                }

                await Task.Delay(1000);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Encontra caminho do playit
    /// </summary>
    private async Task<string?> FindPlayitPathAsync()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var possiblePaths = new[]
        {
            Path.Combine(localAppData, @"Programs\playit\playit.exe"),
            @"C:\Program Files\playit\playit.exe",
            "playit.exe"
        };

        foreach (var path in possiblePaths)
        {
            if (System.IO.File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Encontra caminho do ngrok
    /// </summary>
    private async Task<string?> FindNgrokPathAsync()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var possiblePaths = new[]
        {
            Path.Combine(localAppData, @"ngrok\ngrok.exe"),
            @"C:\Program Files\ngrok\ngrok.exe",
            "ngrok.exe"
        };

        foreach (var path in possiblePaths)
        {
            if (System.IO.File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Baixa playit automaticamente
    /// </summary>
    private async Task<string?> DownloadPlayitAsync()
    {
        try
        {
            OnLogMessage?.Invoke("📥 Baixando Playit.gg...");
            
            // URL de download do playit
            var downloadUrl = "https://playit.gg/playit-windows-exe";
            var downloadPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Programs\playit\playit.exe");
            
            // Criar diretório
            var dir = System.IO.Path.GetDirectoryName(downloadPath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            // Baixar usando HttpClient
            using var client = new HttpClient();
            var response = await client.GetAsync(downloadUrl);
            var content = await response.Content.ReadAsByteArrayAsync();
            await System.IO.File.WriteAllBytesAsync(downloadPath, content);

            if (System.IO.File.Exists(downloadPath))
            {
                OnLogMessage?.Invoke("✅ Playit.gg baixado com sucesso");
                return downloadPath;
            }

            return null;
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"❌ Erro ao baixar Playit.gg: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Para o túnel
    /// </summary>
    public void StopTunnel()
    {
        try
        {
            if (_tunnelProcess != null && !_tunnelProcess.HasExited)
            {
                _tunnelProcess.Kill();
                _tunnelProcess.Dispose();
                _tunnelProcess = null;
                OnLogMessage?.Invoke("⏹️ Túnel parado");
            }
            _isRunning = false;
            _publicUrl = null;
        }
        catch { }
    }

    /// <summary>
    /// Verifica se túnel está ativo
    /// </summary>
    public bool IsTunnelActive()
    {
        return _isRunning && !string.IsNullOrEmpty(_publicUrl);
    }

    /// <summary>
    /// Obtém URL pública atual
    /// </summary>
    public string? GetPublicUrl()
    {
        return _publicUrl;
    }

    /// <summary>
    /// Obtém status atual
    /// </summary>
    public string GetStatus()
    {
        if (!_isRunning)
            return "❌ Túnel inativo";

        return $"✅ Túnel ativo - URL: {_publicUrl}";
    }

    public void Dispose()
    {
        StopTunnel();
    }
}
