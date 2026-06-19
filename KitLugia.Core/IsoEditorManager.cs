using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    /// <summary>
    /// Gerenciador avançado de edição de ISOs
    /// Usa IsoManager existente para operações de ISO e DISM para customização
    /// </summary>
    public static class IsoEditorManager
    {
        // ==========================================
        // HELPER METHODS
        // ==========================================

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
        // ==========================================
        // ISO MANAGEMENT (Usando IsoManager existente)
        // ==========================================

        /// <summary>
        /// Cria uma ISO a partir de um diretório (usa IsoManager existente)
        /// </summary>
        public static async Task<(bool Success, string Message)> CreateIso(string sourceDir, string targetIso)
        {
            return await IsoManager.CreateIso(sourceDir, targetIso);
        }

        /// <summary>
        /// Monta uma ISO (usa IsoManager existente)
        /// </summary>
        public static async Task<(bool Success, string Message, string DriveLetter)> MountIso(string isoPath)
        {
            return await IsoManager.MountIso(isoPath);
        }

        /// <summary>
        /// Desmonta uma ISO (usa IsoManager existente)
        /// </summary>
        public static async Task<(bool Success, string Message)> DismountIso(string isoPath)
        {
            return await IsoManager.DismountIso(isoPath);
        }

        // ==========================================
        // DISM MANAGEMENT (via PowerShell - usando IsoManager)
        // ==========================================

        /// <summary>
        /// Monta uma imagem WIM usando DISM
        /// </summary>
        public static async Task<(bool Success, string Message)> MountWim(string wimPath, string mountPath)
        {
            return await IsoManager.MountWim(wimPath, mountPath);
        }

        /// <summary>
        /// Desmonta uma imagem WIM usando DISM e salva as alterações
        /// </summary>
        public static async Task<(bool Success, string Message)> UnmountWim(string mountPath, bool commit = true)
        {
            return await IsoManager.UnmountWim(mountPath, commit);
        }

        /// <summary>
        /// Injeta drivers em uma imagem WIM usando DISM
        /// </summary>
        public static async Task<(bool Success, string Message)> InjectDrivers(string mountPath, string driverPath)
        {
            return await IsoManager.InjectDrivers(mountPath, driverPath);
        }

        // ==========================================
        // ADVANCED ISO CUSTOMIZATION (Debloat, Features, etc.)
        // ==========================================

        /// <summary>
        /// Lista todos os provisioned apps (UWP) na imagem montada
        /// </summary>
        public static async Task<(bool Success, string Message, List<ProvisionedAppInfo> Apps)> GetProvisionedApps(string mountPath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(mountPath))
                        return (false, "Diretório de montagem não existe.", new List<ProvisionedAppInfo>());

                    // Usar dism.exe diretamente (igual ao Chris Titus WinUtil)
                    // PowerShell adiciona overhead desnecessário
                    var psi = new ProcessStartInfo
                    {
                        FileName = "dism.exe",
                        Arguments = $"/English /Image:\"{mountPath}\" /Get-ProvisionedAppxPackages /Format:Table",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi)!;
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    // Se o comando falhar, pode ser que não há apps provisioned (isso é normal)
                    if (process.ExitCode != 0)
                    {
                        Logger.Log($"Aviso: Não foi possível listar provisioned apps (pode não haver apps): {error}");
                        // Retornar sucesso com lista vazia em vez de falha
                        return (true, "Nenhum app provisioned encontrado na imagem.", new List<ProvisionedAppInfo>());
                    }

                    // Parse do output do DISM
                    var apps = ParseProvisionedApps(output);
                    return (true, $"Apps listados com sucesso. Total: {apps.Count}", apps);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Erro ao listar provisioned apps: {ex.Message}");
                    // Retornar sucesso com lista vazia em vez de falha
                    return (true, $"Não foi possível listar apps (erro ignorado): {ex.Message}", new List<ProvisionedAppInfo>());
                }
            });
        }

        /// <summary>
        /// Remove provisioned apps da imagem montada
        /// </summary>
        public static async Task<(bool Success, string Message, List<string> RemovedApps)> RemoveProvisionedApps(string mountPath, List<string> packageNames)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(mountPath))
                        return (false, "Diretório de montagem não existe.", new List<string>());

                    var removedApps = new List<string>();
                    var failedApps = new List<string>();
                    var notFoundApps = new List<string>();

                    // Primeiro, listar todos os provisioned apps disponíveis
                    var (listSuccess, _, availableApps) = GetProvisionedApps(mountPath).Result;
                    if (!listSuccess || availableApps.Count == 0)
                    {
                        return (false, "Não foi possível listar apps disponíveis na imagem.", new List<string>());
                    }

                    var availablePackageNames = availableApps.Select(a => a.PackageName).ToList();

                    foreach (var packageName in packageNames)
                    {
                        // Verificar se o pacote existe antes de tentar remover
                        var exactMatch = availablePackageNames.FirstOrDefault(p => p.Equals(packageName, StringComparison.OrdinalIgnoreCase));
                        var partialMatch = availablePackageNames.FirstOrDefault(p => p.Contains(packageName, StringComparison.OrdinalIgnoreCase) || packageName.Contains(p, StringComparison.OrdinalIgnoreCase));

                        var targetPackage = exactMatch ?? partialMatch;

                        if (targetPackage == null)
                        {
                            notFoundApps.Add(packageName);
                            Logger.Log($"Pacote não encontrado na imagem: {packageName}");
                            continue;
                        }

                        // Usar PowerShell para remover (igual ao Chris Titus mas com melhor tratamento)
                        var psi = new ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"dism /English /Image:'{mountPath}' /Remove-ProvisionedAppxPackage /PackageName:'{targetPackage}'\"",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };

                        using var process = Process.Start(psi)!;
                        string output = process.StandardOutput.ReadToEnd();
                        string error = process.StandardError.ReadToEnd();
                        process.WaitForExit();

                        if (process.ExitCode == 0)
                        {
                            removedApps.Add(packageName);
                        }
                        else
                        {
                            failedApps.Add(packageName);
                            Logger.Log($"Falha ao remover {packageName}: {error}");
                        }
                    }

                    string message = $"Removidos: {removedApps.Count}/{packageNames.Count}";
                    if (notFoundApps.Count > 0)
                        message += $"\nNão encontrados: {notFoundApps.Count}";
                    if (failedApps.Count > 0)
                        message += $"\nFalharam: {failedApps.Count}";

                    return (true, message, removedApps);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Erro ao remover provisioned apps: {ex.Message}");
                    return (false, $"Erro: {ex.Message}", new List<string>());
                }
            });
        }

        /// <summary>
        /// Lista todas as features do Windows na imagem montada
        /// </summary>
        public static async Task<(bool Success, string Message, List<WindowsFeatureInfo> Features)> GetWindowsFeatures(string mountPath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(mountPath))
                        return (false, "Diretório de montagem não existe.", new List<WindowsFeatureInfo>());

                    var psi = new ProcessStartInfo
                    {
                        FileName = "dism.exe",
                        Arguments = $"/Image:\"{mountPath}\" /Get-Features /Format:Table",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi)!;
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                        return (false, $"Erro ao listar features: {error}", new List<WindowsFeatureInfo>());

                    // Parse do output do DISM
                    var features = ParseWindowsFeatures(output);
                    return (true, $"Features listadas com sucesso. Total: {features.Count}", features);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Erro ao listar features: {ex.Message}");
                    return (false, $"Erro: {ex.Message}", new List<WindowsFeatureInfo>());
                }
            });
        }

        /// <summary>
        /// Habilita uma feature do Windows
        /// </summary>
        public static async Task<(bool Success, string Message)> EnableFeature(string mountPath, string featureName, bool all = false)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(mountPath))
                        return (false, "Diretório de montagem não existe.");

                    var allParam = all ? "/All" : "";
                    var psi = new ProcessStartInfo
                    {
                        FileName = "dism.exe",
                        Arguments = $"/Image:\"{mountPath}\" /Enable-Feature /FeatureName:\"{featureName}\" {allParam} /NoRestart",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi)!;
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode == 0)
                        return (true, $"Feature {featureName} habilitada com sucesso.");
                    else
                        return (false, $"Erro ao habilitar feature: {error}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Erro ao habilitar feature: {ex.Message}");
                    return (false, $"Erro: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Desabilita uma feature do Windows
        /// </summary>
        public static async Task<(bool Success, string Message)> DisableFeature(string mountPath, string featureName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(mountPath))
                        return (false, "Diretório de montagem não existe.");

                    var psi = new ProcessStartInfo
                    {
                        FileName = "dism.exe",
                        Arguments = $"/Image:\"{mountPath}\" /Disable-Feature /FeatureName:\"{featureName}\" /NoRestart",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi)!;
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode == 0)
                        return (true, $"Feature {featureName} desabilitada com sucesso.");
                    else
                        return (false, $"Erro ao desabilitar feature: {error}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Erro ao desabilitar feature: {ex.Message}");
                    return (false, $"Erro: {ex.Message}");
                }
            });
        }

        // ==========================================
        // ISO SIZE REDUCTION (WinSxS, Compression, etc.)
        // ==========================================

        /// <summary>
        /// Limpa o WinSxS (Component Store) da imagem montada
        /// /ResetBase remove versões superadas (não pode desinstalar updates)
        /// </summary>
        public static async Task<(bool Success, string Message, long SpaceSaved)> CleanupWinSxS(string mountPath, bool resetBase = false)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(mountPath))
                        return (false, "Diretório de montagem não existe.", 0);

                    var sizeBefore = GetDirectorySize(new DirectoryInfo($"{mountPath}\\Windows\\WinSxS"));

                    var resetBaseParam = resetBase ? "/ResetBase" : "";
                    // Usar dism.exe diretamente (igual ao Chris Titus WinUtil)
                    // PowerShell adiciona overhead desnecessário
                    var psi = new ProcessStartInfo
                    {
                        FileName = "dism.exe",
                        Arguments = $"/English /Image:\"{mountPath}\" /Cleanup-Image /StartComponentCleanup {resetBaseParam}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi)!;
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                        return (false, $"Erro ao limpar WinSxS: {error}", 0);

                    var sizeAfter = GetDirectorySize(new DirectoryInfo($"{mountPath}\\Windows\\WinSxS"));
                    var spaceSaved = sizeBefore - sizeAfter;

                    var message = $"WinSxS limpo com sucesso. Economia: {FormatBytes(spaceSaved)}";
                    if (resetBase)
                        message += "\n⚠️ /ResetBase usado: Updates antigos não podem ser desinstalados.";

                    return (true, message, spaceSaved);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Erro ao limpar WinSxS: {ex.Message}");
                    return (false, $"Erro: {ex.Message}", 0);
                }
            });
        }

        /// <summary>
        /// Lista todas as capabilities disponíveis
        /// </summary>
        public static async Task<(bool Success, string Message, List<string> Capabilities)> GetCapabilities(string mountPath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(mountPath))
                        return (false, "Diretório de montagem não existe.", new List<string>());

                    var psi = new ProcessStartInfo
                    {
                        FileName = "dism.exe",
                        Arguments = $"/Image:\"{mountPath}\" /Get-Capabilities",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi)!;
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                        return (false, $"Erro ao listar capabilities: {error}", new List<string>());

                    // Parse do output do DISM
                    var capabilities = new List<string>();
                    var lines = output.Split('\n');
                    foreach (var line in lines)
                    {
                        var match = Regex.Match(line, @"Capability Identity\s*:\s*(.+)");
                        if (match.Success)
                        {
                            capabilities.Add(match.Groups[1].Value.Trim());
                        }
                    }

                    return (true, $"Capabilities listadas com sucesso. Total: {capabilities.Count}", capabilities);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Erro ao listar capabilities: {ex.Message}");
                    return (false, $"Erro: {ex.Message}", new List<string>());
                }
            });
        }

        /// <summary>
        /// Remove capabilities da imagem montada
        /// </summary>
        public static async Task<(bool Success, string Message, List<string> Removed)> RemoveCapabilities(string mountPath, List<string> capabilities)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(mountPath))
                        return (false, "Diretório de montagem não existe.", new List<string>());

                    var removed = new List<string>();
                    var failed = new List<string>();

                    foreach (var capability in capabilities)
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "dism.exe",
                            Arguments = $"/Image:\"{mountPath}\" /Remove-Capability /CapabilityName:\"{capability}\"",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };

                        using var process = Process.Start(psi)!;
                        string output = process.StandardOutput.ReadToEnd();
                        string error = process.StandardError.ReadToEnd();
                        process.WaitForExit();

                        if (process.ExitCode == 0)
                            removed.Add(capability);
                        else
                            failed.Add(capability);
                    }

                    string message = $"Removidas: {removed.Count}/{capabilities.Count}";
                    if (failed.Count > 0)
                        message += $"\nFalharam: {string.Join(", ", failed)}";

                    return (true, message, removed);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Erro ao remover capabilities: {ex.Message}");
                    return (false, $"Erro: {ex.Message}", new List<string>());
                }
            });
        }

        /// <summary>
        /// Lista todos os pacotes do sistema
        /// </summary>
        public static async Task<(bool Success, string Message, List<string> Packages)> GetPackages(string mountPath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(mountPath))
                        return (false, "Diretório de montagem não existe.", new List<string>());

                    var psi = new ProcessStartInfo
                    {
                        FileName = "dism.exe",
                        Arguments = $"/Image:\"{mountPath}\" /Get-Packages",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi)!;
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                        return (false, $"Erro ao listar pacotes: {error}", new List<string>());

                    // Parse do output do DISM
                    var packages = new List<string>();
                    var lines = output.Split('\n');
                    foreach (var line in lines)
                    {
                        var match = Regex.Match(line, @"Package Identity\s*:\s*(.+)");
                        if (match.Success)
                        {
                            packages.Add(match.Groups[1].Value.Trim());
                        }
                    }

                    return (true, $"Pacotes listados com sucesso. Total: {packages.Count}", packages);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Erro ao listar pacotes: {ex.Message}");
                    return (false, $"Erro: {ex.Message}", new List<string>());
                }
            });
        }

        /// <summary>
        /// Remove pacotes do sistema (OneDrive, Edge, etc.)
        /// </summary>
        public static async Task<(bool Success, string Message, List<string> Removed)> RemovePackages(string mountPath, List<string> packages)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(mountPath))
                        return (false, "Diretório de montagem não existe.", new List<string>());

                    var removed = new List<string>();
                    var failed = new List<string>();

                    foreach (var package in packages)
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "dism.exe",
                            Arguments = $"/Image:\"{mountPath}\" /Remove-Package /PackageName:\"{package}\"",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };

                        using var process = Process.Start(psi)!;
                        string output = process.StandardOutput.ReadToEnd();
                        string error = process.StandardError.ReadToEnd();
                        process.WaitForExit();

                        if (process.ExitCode == 0)
                            removed.Add(package);
                        else
                            failed.Add(package);
                    }

                    string message = $"Removidos: {removed.Count}/{packages.Count}";
                    if (failed.Count > 0)
                        message += $"\nFalharam: {string.Join(", ", failed)}";

                    return (true, message, removed);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Erro ao remover pacotes: {ex.Message}");
                    return (false, $"Erro: {ex.Message}", new List<string>());
                }
            });
        }

        /// <summary>
        /// Lista idiomas instalados
        /// </summary>
        public static async Task<(bool Success, string Message, List<string> Languages)> GetLanguages(string mountPath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(mountPath))
                        return (false, "Diretório de montagem não existe.", new List<string>());

                    var psi = new ProcessStartInfo
                    {
                        FileName = "dism.exe",
                        Arguments = $"/Image:\"{mountPath}\" /Get-Intl",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi)!;
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                        return (false, $"Erro ao listar idiomas: {error}", new List<string>());

                    // Parse do output do DISM
                    var languages = new List<string>();
                    var lines = output.Split('\n');
                    foreach (var line in lines)
                    {
                        var match = Regex.Match(line, @"Default system UI language\s*:\s*(.+)");
                        if (match.Success)
                        {
                            languages.Add(match.Groups[1].Value.Trim());
                        }
                    }

                    return (true, $"Idiomas listados com sucesso. Total: {languages.Count}", languages);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Erro ao listar idiomas: {ex.Message}");
                    return (false, $"Erro: {ex.Message}", new List<string>());
                }
            });
        }

        /// <summary>
        /// Remove idiomas não usados
        /// </summary>
        public static async Task<(bool Success, string Message, List<string> Removed)> RemoveLanguages(string mountPath, List<string> languages)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(mountPath))
                        return (false, "Diretório de montagem não existe.", new List<string>());

                    var removed = new List<string>();
                    var failed = new List<string>();

                    foreach (var language in languages)
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "dism.exe",
                            Arguments = $"/Image:\"{mountPath}\" /Remove-Language /Language:\"{language}\"",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };

                        using var process = Process.Start(psi)!;
                        string output = process.StandardOutput.ReadToEnd();
                        string error = process.StandardError.ReadToEnd();
                        process.WaitForExit();

                        if (process.ExitCode == 0)
                            removed.Add(language);
                        else
                            failed.Add(language);
                    }

                    string message = $"Removidos: {removed.Count}/{languages.Count}";
                    if (failed.Count > 0)
                        message += $"\nFalharam: {string.Join(", ", failed)}";

                    return (true, message, removed);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Erro ao remover idiomas: {ex.Message}");
                    return (false, $"Erro: {ex.Message}", new List<string>());
                }
            });
        }

        /// <summary>
        /// Exporta WIM para ESD com compressão LZMS (recovery)
        /// Economia significativa de espaço (~1-2GB)
        /// </summary>
        public static async Task<(bool Success, string Message, long SizeReduction)> ExportToESD(string wimPath, string esdPath, int index = 1)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(wimPath))
                        return (false, "Arquivo WIM não existe.", 0);

                    var sizeBefore = new FileInfo(wimPath).Length;

                    var psi = new ProcessStartInfo
                    {
                        FileName = "dism.exe",
                        Arguments = $"/Export-Image /SourceImageFile:\"{wimPath}\" /SourceIndex:{index} /DestinationImageFile:\"{esdPath}\" /Compress:recovery",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi)!;
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                        return (false, $"Erro ao exportar para ESD: {error}", 0);

                    var sizeAfter = new FileInfo(esdPath).Length;
                    var reduction = sizeBefore - sizeAfter;

                    return (true, $"ESD criado com sucesso. Economia: {FormatBytes(reduction)}\n⚠️ ESD não pode ser montado/modificado.", reduction);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Erro ao exportar para ESD: {ex.Message}");
                    return (false, $"Erro: {ex.Message}", 0);
                }
            });
        }

        // ==========================================
        // HELPER METHODS (Parsing & Utilities)
        // ==========================================

        private static long GetDirectorySize(DirectoryInfo dir)
        {
            if (!dir.Exists)
                return 0;

            long size = 0;
            try
            {
                size = dir.EnumerateFiles().Sum(fi => fi.Length);
                size += dir.EnumerateDirectories().Sum(di => GetDirectorySize(di));
            }
            catch
            {
                // Ignora erros de acesso
            }
            return size;
        }

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private static List<ProvisionedAppInfo> ParseProvisionedApps(string dismOutput)
        {
            var apps = new List<ProvisionedAppInfo>();
            var lines = dismOutput.Split('\n');

            foreach (var line in lines)
            {
                // Exemplo de linha: Package Identity : Microsoft.BingWeather_4.52.3522.0_neutral__~_8wekyb3d8bbwe
                var match = Regex.Match(line, @"Package Identity\s*:\s*(.+)");
                if (match.Success)
                {
                    var packageName = match.Groups[1].Value.Trim();
                    apps.Add(new ProvisionedAppInfo
                    {
                        PackageName = packageName,
                        DisplayName = GetDisplayName(packageName)
                    });
                }
            }

            return apps;
        }

        private static List<WindowsFeatureInfo> ParseWindowsFeatures(string dismOutput)
        {
            var features = new List<WindowsFeatureInfo>();
            var lines = dismOutput.Split('\n');

            foreach (var line in lines)
            {
                // Exemplo de linha: Feature Name : TelnetClient
                var match = Regex.Match(line, @"Feature Name\s*:\s*(.+)");
                if (match.Success)
                {
                    var featureName = match.Groups[1].Value.Trim();
                    features.Add(new WindowsFeatureInfo
                    {
                        FeatureName = featureName,
                        DisplayName = GetFeatureDisplayName(featureName)
                    });
                }
            }

            return features;
        }

        private static string GetDisplayName(string packageName)
        {
            // Extrai nome legível do package name
            // Ex: Microsoft.BingWeather_4.52.3522.0_neutral__~_8wekyb3d8bbwe -> Bing Weather
            var parts = packageName.Split('_');
            if (parts.Length > 0)
            {
                var name = parts[0].Replace("Microsoft.", "").Replace("MicrosoftCorporationII.", "");
                // Converte CamelCase para espaços
                return Regex.Replace(name, "(?<!^)(?=[A-Z])", " ");
            }
            return packageName;
        }

        private static string GetFeatureDisplayName(string featureName)
        {
            // Mapeamento de features para nomes legíveis
            var featureMap = new Dictionary<string, string>
            {
                { "TelnetClient", "Telnet Client" },
                { "TFTP", "TFTP Client" },
                { "NetFx3", ".NET Framework 3.5" },
                { "Microsoft-Windows-NetFX3-OC-Package", ".NET Framework 3.5 (includes .NET 2.0 and 3.0)" },
                { "Internet-Explorer-Optional-amd64", "Internet Explorer 11" },
                { "MediaPlayback", "Windows Media Player" },
                { "WindowsMediaPlayer", "Windows Media Player" },
                { "MicrosoftWindowsPowerShellV2", "PowerShell 2.0" },
                { "Microsoft-Windows-NetFx4-US-OC-Package", ".NET Framework 4.5" }
            };

            return featureMap.TryGetValue(featureName, out var displayName) ? displayName : featureName;
        }
    }

    // ==========================================
    // DATA MODELS
    // ==========================================

    public class ProvisionedAppInfo
    {
        public string PackageName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public bool IsSelected { get; set; } = false;
    }

    public class WindowsFeatureInfo
    {
        public string FeatureName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string State { get; set; } = "Unknown";
        public bool IsSelected { get; set; } = false;
    }
}
