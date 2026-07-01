using System;
using System.Diagnostics;
using System.Management;
using System.Net;
using System.Threading.Tasks;

namespace KitLugia.Core;

/// <summary>
/// Adaptador Virtual usando WMI para Minecraft
/// Implementa solução sugerida pelo DeepSeek
/// </summary>
public sealed class WMIAdapter : IDisposable
{
    private string? _adapterId;
    private string? _virtualIp;
    private bool _isCreated = false;

    public event Action<string>? OnLogMessage;

    /// <summary>
    /// Cria adaptador virtual usando WMI
    /// </summary>
    public async Task<(bool Success, string Message)> CreateVirtualAdapterAsync()
    {
        try
        {
            OnLogMessage?.Invoke("🔧 Criando adaptador virtual via WMI...");

            // 1. Verificar se adaptador já existe
            var existingAdapter = await FindLoopbackAdapterAsync();
            if (existingAdapter != null)
            {
                _adapterId = existingAdapter;
                OnLogMessage?.Invoke("✅ Adaptador loopback já existe");
                return (true, "Adaptador já existe");
            }

            // 2. Instalar adaptador loopback via devcon
            var installResult = await InstallLoopbackAdapterAsync();
            if (!installResult.Success)
            {
                return (false, installResult.Message);
            }

            // 3. Aguardar instalação completar
            await Task.Delay(3000);

            // 4. Encontrar adaptador recém-criado
            _adapterId = await FindLoopbackAdapterAsync();
            if (string.IsNullOrEmpty(_adapterId))
            {
                return (false, "Não foi possível encontrar adaptador criado");
            }

            _isCreated = true;
            OnLogMessage?.Invoke($"✅ Adaptador criado: {_adapterId}");

            return (true, "Adaptador virtual criado com sucesso");
        }
        catch (Exception ex)
        {
            return (false, $"Erro ao criar adaptador: {ex.Message}");
        }
    }

    /// <summary>
    /// Configura IP no adaptador virtual
    /// </summary>
    public async Task<(bool Success, string Message)> ConfigureAdapterAsync(string ipAddress)
    {
        try
        {
            if (string.IsNullOrEmpty(_adapterId))
            {
                return (false, "Adaptador não encontrado");
            }

            OnLogMessage?.Invoke($"⚙️ Configurando IP {ipAddress} no adaptador...");

            // Usar netsh para configurar IP
            var command = $"int ip set address name=\"{_adapterId}\" static {ipAddress} 255.255.255.255";
            var result = await ExecuteCommandAsync("netsh.exe", command);

            if (result.ExitCode == 0)
            {
                _virtualIp = ipAddress;
                OnLogMessage?.Invoke($"✅ IP configurado: {ipAddress}");
                return (true, "IP configurado com sucesso");
            }
            else
            {
                return (false, $"Falha ao configurar IP: {result.Output}");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Erro ao configurar IP: {ex.Message}");
        }
    }

    /// <summary>
    /// Configura roteamento para usar adaptador virtual
    /// </summary>
    public async Task<(bool Success, string Message)> ConfigureRoutingAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_virtualIp))
            {
                return (false, "IP virtual não configurado");
            }

            OnLogMessage?.Invoke("🛣️ Configurando roteamento...");

            // Adicionar rota específica para tráfego do Minecraft
            var command = $"add {GetWarpSubnet()} mask 255.255.255.255 {_virtualIp}";
            var result = await ExecuteCommandAsync("route.exe", command);

            if (result.ExitCode == 0)
            {
                OnLogMessage?.Invoke("✅ Roteamento configurado");
                return (true, "Roteamento configurado com sucesso");
            }
            else
            {
                return (false, $"Falha ao configurar roteamento: {result.Output}");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Erro ao configurar roteamento: {ex.Message}");
        }
    }

    /// <summary>
    /// Encontra adaptador loopback existente
    /// </summary>
    private async Task<string?> FindLoopbackAdapterAsync()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_NetworkAdapter WHERE Name LIKE '%Loopback%' OR Description LIKE '%Microsoft KM-TEST%'");

            foreach (ManagementObject adapter in searcher.Get())
            {
                var name = adapter["Name"]?.ToString();
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"⚠️ Erro ao buscar adaptador: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Instala adaptador loopback via devcon
    /// </summary>
    private async Task<(bool Success, string Message)> InstallLoopbackAdapterAsync()
    {
        try
        {
            var devconPath = await FindDevconPathAsync();
            if (string.IsNullOrEmpty(devconPath))
            {
                return (false, "Devcon não encontrado");
            }

            OnLogMessage?.Invoke("📦 Instalando adaptador loopback...");

            var command = "install \"C:\\Windows\\inf\\netloop.inf\" *MSLOOP";
            var result = await ExecuteCommandAsync(devconPath, command);

            if (result.ExitCode == 0)
            {
                OnLogMessage?.Invoke("✅ Adaptador instalado");
                return (true, "Adaptador instalado com sucesso");
            }
            else
            {
                return (false, $"Falha na instalação: {result.Output}");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Erro na instalação: {ex.Message}");
        }
    }

    /// <summary>
    /// Encontra caminho do devcon.exe
    /// </summary>
    private async Task<string?> FindDevconPathAsync()
    {
        // Procura no PATH
        try
        {
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                string candidate = Path.Combine(dir.Trim(), "devcon.exe");
                if (System.IO.File.Exists(candidate))
                    return candidate;
            }
        }
        catch { }

        var possiblePaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Windows Kits\10\Tools\x64\devcon.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Windows Kits\10\Tools\x64\devcon.exe"),
            "devcon.exe"
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
    /// Obtém sub-rede do WARP
    /// </summary>
    private string GetWarpSubnet()
    {
        // WARP geralmente usa sub-rede 100.64.0.0/10
        // Para testes, usaremos uma sub-rede específica
        return "100.64.0.0";
    }

    /// <summary>
    /// Executa comando externo
    /// </summary>
    private async Task<(int ExitCode, string Output)> ExecuteCommandAsync(string fileName, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
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
    /// Remove adaptador virtual
    /// </summary>
    public async Task<(bool Success, string Message)> RemoveVirtualAdapterAsync()
    {
        try
        {
            if (!_isCreated || string.IsNullOrEmpty(_adapterId))
            {
                return (true, "Adaptador não foi criado");
            }

            OnLogMessage?.Invoke("🗑️ Removendo adaptador virtual...");

            var devconPath = await FindDevconPathAsync();
            if (string.IsNullOrEmpty(devconPath))
            {
                return (false, "Devcon não encontrado");
            }

            var command = $"remove \"{_adapterId}\"";
            var result = await ExecuteCommandAsync(devconPath, command);

            if (result.ExitCode == 0)
            {
                _isCreated = false;
                _adapterId = null;
                _virtualIp = null;
                OnLogMessage?.Invoke("✅ Adaptador removido");
                return (true, "Adaptador removido com sucesso");
            }
            else
            {
                return (false, $"Falha ao remover: {result.Output}");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Erro ao remover: {ex.Message}");
        }
    }

    /// <summary>
    /// Obtém status atual
    /// </summary>
    public string GetStatus()
    {
        if (!_isCreated)
            return "❌ Adaptador não criado";

        return $"✅ Adaptador ativo - IP: {_virtualIp ?? "Não configurado"}";
    }

    /// <summary>
    /// Obtém IP virtual atual
    /// </summary>
    public string? GetVirtualIp()
    {
        return _virtualIp;
    }

    public void Dispose()
    {
        if (_isCreated)
        {
            _ = RemoveVirtualAdapterAsync().Wait(5000);
        }
    }
}
