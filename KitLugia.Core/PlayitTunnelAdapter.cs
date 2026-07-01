using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace KitLugia.Core;

/// <summary>
/// Adaptador focado apenas em Playit.gg
/// Simplificado sem fallbacks para debuggar e fazer funcionar
/// </summary>
public sealed class PlayitTunnelAdapter : IDisposable
{
    private Process? _playitProcess;
    private string? _publicUrl;
    private bool _isRunning = false;

    public event Action<string>? OnLogMessage;
    public event Action<string>? OnTunnelCreated;

    /// <summary>
    /// Inicia túnel usando apenas Playit.gg
    /// </summary>
    public async Task<(bool Success, string PublicUrl, string Message)> CreatePlayitTunnelAsync(int port)
    {
        try
        {
            OnLogMessage?.Invoke($"🚀 Iniciando túnel Playit.gg para porta {port}...");

            // 1. Verificar se Playit.gg está instalado
            var playitPath = await FindPlayitPathAsync();
            if (string.IsNullOrEmpty(playitPath))
            {
                // 2. Baixar automaticamente
                OnLogMessage?.Invoke("📦 Playit.gg não encontrado. Baixando...");
                playitPath = await DownloadPlayitAsync();
                
                if (string.IsNullOrEmpty(playitPath))
                {
                    return (false, "", "Não foi possível baixar Playit.gg");
                }
            }

            // 3. Iniciar túnel
            OnLogMessage?.Invoke("🎮 Iniciando túnel Playit.gg...");
            var startResult = await StartPlayitTunnelAsync(playitPath, port);
            
            if (!startResult.Success)
            {
                return (false, "", startResult.Message);
            }

            // 4. Aguardar URL
            var tunnelUrl = await WaitForTunnelUrlAsync();
            
            if (string.IsNullOrEmpty(tunnelUrl))
            {
                return (false, "", "Não foi possível obter URL do túnel");
            }

            _publicUrl = tunnelUrl;
            OnLogMessage?.Invoke($"✅ Túnel criado: {tunnelUrl}");
            OnTunnelCreated?.Invoke(tunnelUrl);

            return (true, tunnelUrl, "Túnel Playit.gg criado com sucesso");
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"❌ Erro no Playit.gg: {ex.Message}");
            return (false, "", $"Erro: {ex.Message}");
        }
    }

    /// <summary>
    /// Encontra caminho do Playit.gg
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
            if (File.Exists(path))
            {
                OnLogMessage?.Invoke($"✅ Playit.gg encontrado: {path}");
                return path;
            }
        }

        OnLogMessage?.Invoke("⚠️ Playit.gg não encontrado");
        return null;
    }

    /// <summary>
    /// Baixa Playit.gg automaticamente
    /// </summary>
    private async Task<string?> DownloadPlayitAsync()
    {
        try
        {
            OnLogMessage?.Invoke("📥 Baixando Playit.gg...");
            
            // URL de download direto do GitHub (executável correto)
            var downloadUrl = "https://github.com/playit-cloud/playit-agent/releases/download/v0.17.1/playit-windows-x86_64.exe";
            var downloadPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Programs\playit\playit.exe");
            
            // Criar diretório
            var dir = Path.GetDirectoryName(downloadPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Remover arquivo corrompido se existir
            if (File.Exists(downloadPath))
            {
                OnLogMessage?.Invoke("🗑️ Removendo arquivo corrompido...");
                File.Delete(downloadPath);
            }

            // Baixar usando HttpClient com verificação
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(5); // 5 minutos timeout
            
            OnLogMessage?.Invoke($"📥 Baixando de: {downloadUrl}");
            var response = await client.GetAsync(downloadUrl);
            
            if (!response.IsSuccessStatusCode)
            {
                OnLogMessage?.Invoke($"❌ Falha no download: {response.StatusCode}");
                return null;
            }

            var content = await response.Content.ReadAsByteArrayAsync();
            
            // Verificar se o conteúdo é válido (tamanho mínimo)
            if (content.Length < 5000000) // Menos de 5MB provavelmente não é o executável correto
            {
                OnLogMessage?.Invoke($"❌ Arquivo muito pequeno ({content.Length} bytes) - provavelmente não é o executável correto");
                return null;
            }

            await File.WriteAllBytesAsync(downloadPath, content);

            // Verificar se o arquivo foi criado e tem tamanho válido
            if (File.Exists(downloadPath))
            {
                var fileInfo = new FileInfo(downloadPath);
                OnLogMessage?.Invoke($"✅ Playit.gg baixado: {downloadPath} ({fileInfo.Length} bytes)");
                
                // Verificar se o arquivo é executável válido
                if (IsValidExecutable(downloadPath))
                {
                    return downloadPath;
                }
                else
                {
                    OnLogMessage?.Invoke("❌ Arquivo baixado não é um executável válido");
                    File.Delete(downloadPath);
                    return null;
                }
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
    /// Verifica se o arquivo é um executável válido
    /// </summary>
    private bool IsValidExecutable(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            
            // Verificar tamanho mínimo (executáveis Windows geralmente > 1MB)
            if (fileInfo.Length < 1000000)
                return false;

            // Verificar assinatura MZ (cabeçalho de executável Windows)
            using var stream = File.OpenRead(filePath);
            var buffer = new byte[2];
            var bytesRead = stream.Read(buffer, 0, 2);
            
            if (bytesRead < 2)
                return false;

            // Assinatura MZ = 0x4D5A ("MZ")
            return buffer[0] == 0x4D && buffer[1] == 0x5A;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Inicia processo do Playit.gg
    /// </summary>
    private async Task<(bool Success, string Message)> StartPlayitTunnelAsync(string playitPath, int port)
    {
        try
        {
            OnLogMessage?.Invoke($"🚀 Executando: {playitPath} start tcp {port}");

            var startInfo = new ProcessStartInfo
            {
                FileName = playitPath,
                Arguments = $"start tcp {port}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(playitPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            };

            _playitProcess = Process.Start(startInfo);
            if (_playitProcess == null)
            {
                return (false, "Não foi possível iniciar o processo");
            }

            _isRunning = true;
            OnLogMessage?.Invoke("✅ Processo Playit.gg iniciado");

            return (true, "Processo iniciado com sucesso");
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"❌ Erro ao iniciar Playit.gg: {ex.Message}");
            return (false, $"Erro ao iniciar: {ex.Message}");
        }
    }

    /// <summary>
    /// Aguarda URL do túnel
    /// </summary>
    private async Task<string?> WaitForTunnelUrlAsync()
    {
        if (_playitProcess == null) return null;

        try
        {
            var timeout = TimeSpan.FromSeconds(60);
            var startTime = DateTime.Now;

            while (DateTime.Now - startTime < timeout)
            {
                if (_playitProcess.HasExited)
                {
                    var error = await _playitProcess.StandardError.ReadToEndAsync();
                    OnLogMessage?.Invoke($"❌ Processo encerrou: {error}");
                    return null;
                }

                // Ler output
                var output = await _playitProcess.StandardOutput.ReadToEndAsync();
                OnLogMessage?.Invoke($"📄 Output Playit.gg: {output}");

                // Procurar por URL no output
                var urlMatch = System.Text.RegularExpressions.Regex.Match(output, @"https?://[^\s]+");
                if (urlMatch.Success)
                {
                    return urlMatch.Value;
                }

                // Procurar por padrão específico do Playit.gg
                var playitMatch = System.Text.RegularExpressions.Regex.Match(output, @"playit\.gg/[^\s]+");
                if (playitMatch.Success)
                {
                    return $"https://{playitMatch.Value}";
                }

                await Task.Delay(2000); // Esperar 2 segundos
            }

            OnLogMessage?.Invoke("⏰ Timeout ao aguardar URL");
            return null;
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"⚠️ Erro ao aguardar URL: {ex.Message}");
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
            if (_playitProcess != null && !_playitProcess.HasExited)
            {
                _playitProcess.Kill();
                _playitProcess.Dispose();
                _playitProcess = null;
                OnLogMessage?.Invoke("⏹️ Túnel Playit.gg parado");
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
    /// Obtém URL pública
    /// </summary>
    public string? GetPublicUrl()
    {
        return _publicUrl;
    }

    /// <summary>
    /// Obtém status
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
