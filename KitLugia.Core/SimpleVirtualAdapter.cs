using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace KitLugia.Core;

/// <summary>
/// Adaptador de Rede Virtual Simplificado para Minecraft
/// Cria interface virtual com IP público usando netsh
/// </summary>
public sealed class SimpleVirtualAdapter : IDisposable
{
    private bool _isInstalled = false;
    private string? _virtualAdapterName;
    private string? _publicIp;

    public event Action<string>? OnLogMessage;
    public event Action<string>? OnAdapterCreated;

    /// <summary>
    /// Cria adaptador virtual com IP público
    /// </summary>
    public async Task<(bool Success, string? AdapterName, string? PublicIp, string Message)> CreateVirtualAdapterAsync()
    {
        try
        {
            OnLogMessage?.Invoke("🔧 Criando adaptador virtual simplificado...");

            // Obter IP público
            _publicIp = await GetPublicIpAsync();
            if (string.IsNullOrEmpty(_publicIp))
            {
                return (false, null, null, "❌ Não foi possível obter IP público");
            }

            // Nome do adaptador virtual
            _virtualAdapterName = "KitLugia-Minecraft";

            // Criar adaptador virtual usando netsh
            var result = await CreateAdapterWithNetsh();
            if (!result.Success)
            {
                return (false, null, null, result.Message);
            }

            // Configurar adaptador
            var configResult = await ConfigureVirtualAdapter();
            if (!configResult.Success)
            {
                return (false, null, null, configResult.Message);
            }

            _isInstalled = true;
            OnLogMessage?.Invoke($"✅ Adaptador virtual criado: {_virtualAdapterName}");
            OnLogMessage?.Invoke($"🌐 IP configurado: {_publicIp}");
            OnLogMessage?.Invoke($"🎮 Minecraft agora usará IP público!");
            OnAdapterCreated?.Invoke(_virtualAdapterName);

            return (true, _virtualAdapterName, _publicIp, "✅ Adaptador virtual criado com sucesso");
        }
        catch (Exception ex)
        {
            return (false, null, null, $"❌ Erro ao criar adaptador: {ex.Message}");
        }
    }

    /// <summary>
    /// Cria adaptador virtual usando netsh
    /// </summary>
    private async Task<(bool Success, string Message)> CreateAdapterWithNetsh()
    {
        try
        {
            OnLogMessage?.Invoke("📡 Criando adaptador virtual com netsh...");

            // Método correto: criar interface de loopback primeiro
            var createCommand = $"interface ipv4 add interface name=\"{_virtualAdapterName}\"";
            var createResult = await ExecuteCommandAsync(createCommand);
            
            if (createResult.ExitCode != 0)
            {
                OnLogMessage?.Invoke($"⚠️ Interface já existe ou falha: {createResult.Output}");
            }

            // Adicionar endereço IP à interface existente
            var command = $"interface ipv4 add address name=\"{_virtualAdapterName}\" address={_publicIp} mask=255.255.255.255";
            
            var result = await ExecuteCommandAsync(command);
            if (result.ExitCode != 0)
            {
                return (false, $"❌ Falha ao adicionar endereço: {result.Output}");
            }

            return (true, "✅ Adaptador criado");
        }
        catch (Exception ex)
        {
            return (false, $"❌ Erro no netsh: {ex.Message}");
        }
    }

    /// <summary>
    /// Configura o adaptador virtual
    /// </summary>
    private async Task<(bool Success, string Message)> ConfigureVirtualAdapter()
    {
        try
        {
            OnLogMessage?.Invoke("⚙️ Configurando adaptador virtual...");

            // Habilitar adaptador
            var enableCommand = $"interface set interface name=\"{_virtualAdapterName}\" admin=enabled";
            var enableResult = await ExecuteCommandAsync(enableCommand);

            if (enableResult.ExitCode != 0)
            {
                return (false, $"❌ Falha ao habilitar adaptador: {enableResult.Output}");
            }

            // Configurar métrica (prioridade)
            var metricCommand = $"interface ipv4 set interface name=\"{_virtualAdapterName}\" metric=1";
            var metricResult = await ExecuteCommandAsync(metricCommand);

            if (metricResult.ExitCode != 0)
            {
                return (false, $"❌ Falha ao configurar métrica: {metricResult.Output}");
            }

            return (true, "✅ Adaptador configurado");
        }
        catch (Exception ex)
        {
            return (false, $"❌ Erro na configuração: {ex.Message}");
        }
    }

    /// <summary>
    /// Remove adaptador virtual
    /// </summary>
    public async Task<(bool Success, string Message)> RemoveVirtualAdapterAsync()
    {
        try
        {
            if (!_isInstalled || string.IsNullOrEmpty(_virtualAdapterName))
            {
                return (true, "✅ Nenhum adaptador virtual para remover");
            }

            OnLogMessage?.Invoke("🗑️ Removendo adaptador virtual...");

            // Remover endereço IP
            var removeCommand = $"interface ipv4 delete address name=\"{_virtualAdapterName}\" address={_publicIp}";
            var removeResult = await ExecuteCommandAsync(removeCommand);

            if (removeResult.ExitCode != 0)
            {
                OnLogMessage?.Invoke($"⚠️ Erro ao remover endereço: {removeResult.Output}");
            }

            // Remover interface completamente
            var deleteCommand = $"interface ipv4 delete interface name=\"{_virtualAdapterName}\"";
            var deleteResult = await ExecuteCommandAsync(deleteCommand);

            if (deleteResult.ExitCode != 0)
            {
                OnLogMessage?.Invoke($"⚠️ Erro ao remover interface: {deleteResult.Output}");
            }

            _isInstalled = false;
            OnLogMessage?.Invoke($"✅ Adaptador virtual removido");

            return (true, "✅ Adaptador removido com sucesso");
        }
        catch (Exception ex)
        {
            return (false, $"❌ Erro ao remover adaptador: {ex.Message}");
        }
    }

    /// <summary>
    /// Executa comando netsh
    /// </summary>
    private async Task<(int ExitCode, string Output)> ExecuteCommandAsync(string command)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null) return (-1, "Falha ao iniciar processo");
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            return (process.ExitCode, output + error);
        }
        catch (Exception ex)
        {
            return (-1, $"Erro ao executar comando: {ex.Message}");
        }
    }

    /// <summary>
    /// Obtém IP público
    /// </summary>
    private async Task<string?> GetPublicIpAsync()
    {
        try
        {
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);

            var response = await client.GetAsync("https://api.ipify.org");
            if (response.IsSuccessStatusCode)
            {
                var ip = await response.Content.ReadAsStringAsync();
                return ip.Trim();
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Verifica se adaptador está ativo
    /// </summary>
    public bool IsAdapterActive()
    {
        if (string.IsNullOrEmpty(_virtualAdapterName))
            return false;

        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces();
            return adapters.Any(a => a.Name.Equals(_virtualAdapterName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Obtém status atual
    /// </summary>
    public string GetStatus()
    {
        if (!_isInstalled)
            return "❌ Adaptador não instalado";

        if (!IsAdapterActive())
            return "⚠️ Adaptador inativo";

        return $"✅ Adaptador ativo - IP: {_publicIp}";
    }

    public void Dispose()
    {
        if (_isInstalled)
        {
            _ = RemoveVirtualAdapterAsync().Wait(5000);
        }
    }
}
