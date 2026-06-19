using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Text;
using System.Linq;

namespace KitLugia.Core
{
    public static class IsoManager
    {
        public static async Task<(bool Success, string Message, string DriveLetter)> MountIso(string isoPath)
        {
            if (!File.Exists(isoPath)) return (false, "Arquivo ISO não encontrado.", "");

            return await Task.Run(() =>
            {
                try
                {
                    // Comando PowerShell corrigido e robusto para obter a letra da unidade
                    string psCommand = $"$m = Mount-DiskImage -ImagePath '{isoPath}' -PassThru; ($m | Get-Volume).DriveLetter";
                    
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psCommand}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(startInfo)!;
                    string output = process.StandardOutput?.ReadToEnd()?.Trim() ?? "";
                    string error = process.StandardError?.ReadToEnd() ?? "";
                    process.WaitForExit();

                    // Validação robusta da saída com regex
                    if (!string.IsNullOrEmpty(output) && Regex.IsMatch(output, "^[A-Za-z]$"))
                    {
                        return (true, "ISO montada com sucesso.", output + ":\\");
                    }
                    else
                    {
                        Logger.Log($"Falha ao montar ISO: {error}");
                        return (false, $"Falha ao montar. Erro: {error}", "");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Erro ao montar ISO: {ex.Message}");
                    return (false, $"Erro crítico: {ex.Message}", "");
                }
            });
        }

        public static async Task<(bool Success, string Message)> DismountIso(string isoPath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string psCommand = $"Dismount-DiskImage -ImagePath '{isoPath}'";
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -WindowStyle Hidden -Command \"{psCommand}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var process = Process.Start(psi)!;
                    process.WaitForExit();

                    if (process.ExitCode == 0)
                        return (true, "ISO desmontada com sucesso.");
                    else
                        return (false, "Erro ao desmontar ISO. Verifique se o caminho está correto.");
                }
                catch (Exception ex) { return (false, $"Exceção ao desmontar ISO: {ex.Message}"); }
            });
        }

        /// <summary>
        /// Limpa todo o lixo do DISM/WIM (temp, logs, montagens, registro)
        /// </summary>
        public static async Task<(bool Success, string Message)> CleanupDismWim()
        {
            return await Task.Run(() =>
            {
                try
                {
                    var cleanupResults = new List<string>();
                    
                    // 1. Limpar montagens órfãs do registro
                    try
                    {
                        var psiReg = new ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Get-ChildItem -Path 'HKLM:\\SOFTWARE\\Microsoft\\WIMMount\\Mounted Images' -ErrorAction SilentlyContinue | ForEach-Object {Remove-Item -Path $_.PSPath -Force -ErrorAction SilentlyContinue}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        var regProcess = Process.Start(psiReg);
                        regProcess?.WaitForExit(30000);
                        cleanupResults.Add("✓ Registro limpo (WIMMount)");
                    }
                    catch (Exception ex)
                    {
                        cleanupResults.Add($"✗ Registro: {ex.Message}");
                    }

                    // 2. Limpar diretórios temporários do DISM em %WINDIR%\Temp
                    try
                    {
                        var psiTemp = new ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Get-ChildItem -Path '$env:WINDIR\\Temp' -Directory -Filter '[0-9a-f]*' -ErrorAction SilentlyContinue | Where-Object {$_.Name -match '^[0-9a-f]{8,}$'} | ForEach-Object {Remove-Item -Path $_.FullName -Recurse -Force -ErrorAction SilentlyContinue}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        var tempProcess = Process.Start(psiTemp);
                        tempProcess?.WaitForExit(60000);
                        cleanupResults.Add("✓ Temp do DISM limpo");
                    }
                    catch (Exception ex)
                    {
                        cleanupResults.Add($"✗ Temp: {ex.Message}");
                    }

                    // 3. Limpar logs antigos do DISM (manter apenas os últimos 10)
                    try
                    {
                        var psiLogs = new ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Get-ChildItem -Path '$env:WINDIR\\Logs\\DISM' -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -Skip 10 | ForEach-Object {Remove-Item -Path $_.FullName -Force -ErrorAction SilentlyContinue}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        var logsProcess = Process.Start(psiLogs);
                        logsProcess?.WaitForExit(30000);
                        cleanupResults.Add("✓ Logs antigos do DISM limpos");
                    }
                    catch (Exception ex)
                    {
                        cleanupResults.Add($"✗ Logs: {ex.Message}");
                    }

                    // 4. Limpar diretórios de montagem órfãos em C:\ (ou outros drives)
                    try
                    {
                        var psiMount = new ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Get-PSDrive -PSProvider FileSystem | ForEach-Object { Get-ChildItem -Path ($_.Root + '*') -Directory -Filter '*mount*' -ErrorAction SilentlyContinue | Where-Object {Test-Path (Join-Path $_.FullName 'Windows')} | ForEach-Object {Write-Host 'Montagem órfã encontrada:' $_.FullName} }\"",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true
                        };
                        var mountProcess = Process.Start(psiMount);
                        var mountOutput = mountProcess?.StandardOutput.ReadToEnd();
                        mountProcess?.WaitForExit(30000);
                        
                        if (!string.IsNullOrEmpty(mountOutput) && !mountOutput.Contains("Montagem órfã encontrada"))
                        {
                            cleanupResults.Add("✓ Nenhuma montagem órfã encontrada");
                        }
                        else if (!string.IsNullOrEmpty(mountOutput))
                        {
                            cleanupResults.Add($"⚠ Montagens órfãs encontradas (requer limpeza manual): {mountOutput}");
                        }
                    }
                    catch (Exception ex)
                    {
                        cleanupResults.Add($"⚠ Verificação de montagens: {ex.Message}");
                    }

                    // 5. Limpar pastas temporárias do KitLugia (onde ficam os GBs!)
                    try
                    {
                        var psiKitLugia = new ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"$tempPath = [System.IO.Path]::GetTempPath(); Get-ChildItem -Path $tempPath -Directory -Filter 'KitLugia_*' -ErrorAction SilentlyContinue | ForEach-Object { $size = (Get-ChildItem -Path $_.FullName -Recurse -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum / 1GB; Write-Host 'Deletando:' $_.Name '($size GB)'; Remove-Item -Path $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }\"",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true
                        };
                        var kitLugiaProcess = Process.Start(psiKitLugia);
                        var kitLugiaOutput = kitLugiaProcess?.StandardOutput.ReadToEnd();
                        kitLugiaProcess?.WaitForExit(60000); // 1 minuto para deletar arquivos grandes
                        
                        if (!string.IsNullOrEmpty(kitLugiaOutput))
                        {
                            cleanupResults.Add($"✓ Pastas KitLugia limpas:\n{kitLugiaOutput}");
                        }
                        else
                        {
                            cleanupResults.Add("✓ Nenhuma pasta KitLugia encontrada");
                        }
                    }
                    catch (Exception ex)
                    {
                        cleanupResults.Add($"✗ Pastas KitLugia: {ex.Message}");
                    }

                    string message = $"Limpeza DISM/WIM concluída:\n" + string.Join("\n", cleanupResults);
                    return (true, message);
                }
                catch (Exception ex)
                {
                    return (false, $"Erro ao limpar DISM/WIM: {ex.Message}");
                }
            });
        }

        // Métodos para compatibilidade com código existente
        public static async Task<(bool Success, string Message)> MountWim(string wimPath, string mountDir)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    // Limpar montagens órfãs antes de montar (evita conflitos)
                    try
                    {
                        var psiCleanup = new ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Get-ChildItem -Path 'HKLM:\\SOFTWARE\\Microsoft\\WIMMount\\Mounted Images' -ErrorAction SilentlyContinue | Get-ItemProperty -ErrorAction SilentlyContinue | Select -ExpandProperty 'Mount Path' -ErrorAction SilentlyContinue | ForEach-Object {Dismount-WindowsImage -Path $_ -Discard -ErrorAction SilentlyContinue}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        Process.Start(psiCleanup)?.WaitForExit(60000); // 1 minuto timeout
                    }
                    catch
                    {
                        // Ignorar erros na limpeza, não deve impedir a montagem
                    }

                    // Detectar automaticamente o índice correto do WIM
                    int imageIndex = await DetectWimIndex(wimPath);
                    if (imageIndex == -1)
                    {
                        return (false, "Não foi possível detectar o índice da imagem WIM. O arquivo pode estar corrompido.");
                    }

                    // Verificar tamanho do WIM antes de montar
                    try
                    {
                        var wimFileInfo = new FileInfo(wimPath);
                        double wimSizeGB = wimFileInfo.Length / (1024.0 * 1024.0 * 1024.0);
                        Logger.Log($"Tamanho do WIM: {wimSizeGB:F2} GB");
                        
                        if (wimSizeGB > 10)
                        {
                            Logger.Log($"AVISO: WIM muito grande ({wimSizeGB:F2} GB). Montagem pode demorar 20+ minutos.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Não foi possível verificar tamanho do WIM: {ex.Message}");
                    }

                    // Usar dism.exe diretamente (igual ao Chris Titus WinUtil)
                    // PowerShell adiciona overhead desnecessário
                    var psi = new ProcessStartInfo
                    {
                        FileName = "dism.exe",
                        Arguments = $"/English /Mount-Wim /WimFile:\"{wimPath}\" /Index:{imageIndex} /MountDir:\"{mountDir}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi)!;
                    
                    // Monitoramento em tempo real (em vez de timer cego)
                    var outputBuilder = new StringBuilder();
                    var errorBuilder = new StringBuilder();
                    var lastOutputTime = DateTime.Now;
                    var hasOutput = false;
                    
                    process.OutputDataReceived += (s, e) => 
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            outputBuilder.AppendLine(e.Data);
                            lastOutputTime = DateTime.Now;
                            hasOutput = true;
                            Logger.Log($"DISM Output: {e.Data}");
                        }
                    };
                    
                    process.ErrorDataReceived += (s, e) => 
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            errorBuilder.AppendLine(e.Data);
                            lastOutputTime = DateTime.Now;
                            hasOutput = true;
                            Logger.Log($"DISM Error: {e.Data}");
                        }
                    };
                    
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    
                    // Monitorar output em tempo real com timeout baseado em atividade
                    // Se não houver output por 5 minutos, assume que travou
                    while (!process.HasExited)
                    {
                        await Task.Delay(1000); // Verificar a cada segundo
                        
                        // Se já teve output e agora está parado há 5 minutos
                        if (hasOutput && (DateTime.Now - lastOutputTime).TotalMinutes > 5)
                        {
                            Logger.Log($"DISM parou de emitir output há 5 minutos. Índice: {imageIndex}");
                            process.Kill();
                            return (false, "DISM parou de responder (sem output por 5 minutos). A imagem pode estar corrompida. Tente usar 'Limpar Lixo' para limpar montagens órfãs.");
                        }
                        
                        // Timeout absoluto de 30 minutos (para WIMs muito grandes)
                        if ((DateTime.Now - process.StartTime).TotalMinutes > 30)
                        {
                            Logger.Log($"Timeout absoluto de 30 minutos atingido. Índice: {imageIndex}");
                            process.Kill();
                            return (false, "Timeout ao montar imagem WIM (30 minutos). A imagem é muito grande ou está corrompida.");
                        }
                    }
                    
                    await Task.Delay(500); // Dar tempo para os eventos terminarem

                    if (process.ExitCode == 0)
                    {
                        Logger.Log($"WIM montado com sucesso. Índice: {imageIndex}");
                        return (true, $"Imagem montada com sucesso (Índice: {imageIndex}).");
                    }
                    else
                    {
                        string error = errorBuilder.ToString();
                        Logger.Log($"Erro ao montar WIM (Exit Code {process.ExitCode}): {error}");
                        return (false, $"Erro ao montar imagem (Exit Code {process.ExitCode}): {error}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Exceção ao montar WIM: {ex.Message}");
                    return (false, $"Exceção ao montar imagem: {ex.Message}");
                }
            });
        }

        private static async Task<int> DetectWimIndex(string wimPath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "dism.exe",
                        Arguments = $"/Get-ImageInfo /ImageFile:\"{wimPath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi)!;
                    bool exited = process.WaitForExit(30000); // 30 segundos
                    
                    if (!exited)
                    {
                        process.Kill();
                        return 1; // Fallback para índice 1
                    }

                    string output = process.StandardOutput.ReadToEnd();
                    
                    // Procurar por "Image Index" no output
                    var match = System.Text.RegularExpressions.Regex.Match(output, @"Image Index:\s*(\d+)");
                    if (match.Success)
                    {
                        return int.Parse(match.Groups[1].Value);
                    }

                    // Se não encontrar, tenta usar o primeiro índice disponível
                    var indexMatch = System.Text.RegularExpressions.Regex.Match(output, @"Index\s*:\s*(\d+)");
                    if (indexMatch.Success)
                    {
                        return int.Parse(indexMatch.Groups[1].Value);
                    }

                    return 1; // Fallback para índice 1
                }
                catch
                {
                    return 1; // Fallback para índice 1
                }
            });
        }

        public static async Task<(bool Success, string Message)> InjectDrivers(string mountDir, string driversPath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "dism.exe",
                        Arguments = $"/Image:\"{mountDir}\" /Add-Driver /Driver:\"{driversPath}\" /Recurse /ForceUnsigned",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi)!;
                    process.WaitForExit();

                    if (process.ExitCode == 0)
                        return (true, "Drivers injetados com sucesso.");
                    else
                        return (false, $"Erro ao injetar drivers: {process.StandardError.ReadToEnd()}");
                }
                catch (Exception ex)
                {
                    return (false, $"Exceção ao injetar drivers: {ex.Message}");
                }
            });
        }

        public static async Task<(bool Success, string Message)> UnmountWim(string mountDir, bool commit)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    // Usar dism.exe diretamente (igual ao Chris Titus WinUtil)
                    // PowerShell adiciona overhead desnecessário
                    var psi = new ProcessStartInfo
                    {
                        FileName = "dism.exe",
                        Arguments = $"/English /Unmount-Wim /MountDir:\"{mountDir}\" /{(commit ? "Commit" : "Discard")}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi)!;
                    
                    // Monitoramento em tempo real (em vez de timer cego)
                    var outputBuilder = new StringBuilder();
                    var errorBuilder = new StringBuilder();
                    var lastOutputTime = DateTime.Now;
                    var hasOutput = false;
                    
                    process.OutputDataReceived += (s, e) => 
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            outputBuilder.AppendLine(e.Data);
                            lastOutputTime = DateTime.Now;
                            hasOutput = true;
                            Logger.Log($"DISM Unmount Output: {e.Data}");
                        }
                    };
                    
                    process.ErrorDataReceived += (s, e) => 
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            errorBuilder.AppendLine(e.Data);
                            lastOutputTime = DateTime.Now;
                            hasOutput = true;
                            Logger.Log($"DISM Unmount Error: {e.Data}");
                        }
                    };
                    
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    
                    // Monitorar output em tempo real com timeout baseado em atividade
                    while (!process.HasExited)
                    {
                        await Task.Delay(1000); // Verificar a cada segundo
                        
                        // Se já teve output e agora está parado há 10 minutos (desmontagem pode ser lenta com /ResetBase)
                        if (hasOutput && (DateTime.Now - lastOutputTime).TotalMinutes > 10)
                        {
                            Logger.Log($"DISM parou de emitir output há 10 minutos durante desmontagem.");
                            process.Kill();
                            
                            // Tentar desmontar sem salvar se o /Commit falhar
                            Logger.Log($"Tentando desmontar com /Discard...");
                            var psiDiscard = new ProcessStartInfo
                            {
                                FileName = "dism.exe",
                                Arguments = $"/English /Unmount-Wim /MountDir:\"{mountDir}\" /Discard",
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                CreateNoWindow = true
                            };

                            using var processDiscard = Process.Start(psiDiscard)!;
                            processDiscard.WaitForExit();
                            
                            string errorDiscard = processDiscard.StandardError.ReadToEnd();
                            if (processDiscard.ExitCode == 0)
                                return (true, "Imagem desmontada com /Discard (alterações não salvas devido a travamento).");
                            else
                                return (false, $"Erro ao desmontar com /Discard (Exit Code {processDiscard.ExitCode}): {errorDiscard}");
                        }
                        
                        // Timeout absoluto de 30 minutos (para desmontagem com /ResetBase)
                        if ((DateTime.Now - process.StartTime).TotalMinutes > 30)
                        {
                            Logger.Log($"Timeout absoluto de 30 minutos atingido na desmontagem.");
                            process.Kill();
                            return (false, "Timeout ao desmontar imagem WIM (30 minutos). A imagem pode estar corrompida ou /ResetBase está demorando muito.");
                        }
                    }
                    
                    await Task.Delay(500); // Dar tempo para os eventos terminarem

                    if (process.ExitCode == 0)
                        return (true, "Imagem desmontada com sucesso.");
                    else
                    {
                        string error = errorBuilder.ToString();
                        return (false, $"Erro ao desmontar imagem (Exit Code {process.ExitCode}): {error}");
                    }
                }
                catch (Exception ex)
                {
                    return (false, $"Exceção ao desmontar imagem: {ex.Message}");
                }
            });
        }

        public static async Task<(bool Success, string Message)> CreateIso(string sourceDir, string targetIso)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    Logger.Log($"Iniciando criação de ISO com oscdimg (UDF bootável). Origem: {sourceDir}, Destino: {targetIso}");

                    // Usar oscdimg.exe embutido para criar ISO bootável UDF (padrão Windows moderno)
                    string oscdimgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "App", "Oscdimg", "oscdimg.exe");

                    if (!File.Exists(oscdimgPath))
                    {
                        return (false, $"oscdimg.exe não encontrado em {oscdimgPath}");
                    }

                    // Contar arquivos para progresso
                    long totalFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories).Length;
                    Logger.Log($"Total de arquivos para criar ISO: {totalFiles}");

                    // Comando oscdimg para criar ISO UDF bootável:
                    // -g: Use GPT (para EFI)
                    // -h: Include hidden files
                    // -k: Include system files
                    // -m: Ignore maximum image size
                    // -u2: UDF file system (versão 2)
                    // -udfver102: UDF version 1.02
                    // -l: Volume label
                    string args = $"-g -h -k -m -u2 -udfver102 -l\"KitLugia_Custom\" \"{sourceDir}\" \"{targetIso}\"";

                    Logger.Log($"Executando oscdimg: {args}");

                    var (code, output) = await RunProcessCaptured(oscdimgPath, args);

                    if (code != 0)
                    {
                        Logger.Log($"Erro oscdimg (Código {code}): {output}");
                        return (false, $"Erro ao criar ISO com oscdimg (Código {code}): {output}");
                    }

                    // Verificar se a ISO foi criada
                    if (!File.Exists(targetIso))
                    {
                        return (false, "ISO não foi criada (arquivo não encontrado após execução).");
                    }

                    long isoSize = new FileInfo(targetIso).Length;
                    Logger.Log($"ISO criada com sucesso: {targetIso} ({isoSize / (1024.0 * 1024.0):F2} MB)");
                    return (true, $"ISO bootável criada com sucesso (UDF, {totalFiles} arquivos, {isoSize / (1024.0 * 1024.0):F2} MB).");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Exceção ao criar ISO com oscdimg: {ex.Message}");
                    return (false, $"Exceção ao criar ISO: {ex.Message}");
                }
            });
        }

        private static async Task<(int ExitCode, string Output)> RunProcessCaptured(string filename, string args)
        {
            return await Task.Run(() =>
            {
                var psi = new ProcessStartInfo(filename, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi)!;
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                return (process.ExitCode, output + (string.IsNullOrEmpty(error) ? "" : $"\n[ERROR]: {error}"));
            });
        }
    }
}
