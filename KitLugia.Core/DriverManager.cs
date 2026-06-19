using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    /// <summary>
    /// Representa um driver do Microsoft Update Catalog
    /// </summary>
    public class MicrosoftUpdateDriver : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
        }

        public string Title { get; set; } = "";
        public string DriverModel { get; set; } = "";
        public string DriverVerDate { get; set; } = "";
        public string DriverClass { get; set; } = "";
        public string DriverManufacturer { get; set; } = "";
        public string DriverVersion { get; set; } = "";
        public object? UpdateObject { get; set; }
        public bool IsDownloaded { get; set; }

        // Status de comparação com driver instalado
        public string ComparisonStatus { get; set; } = "Desconhecido"; // "Atualizado", "Antigo", "Mais Novo", "Não Instalado"
        public string InstalledVersion { get; set; } = "";

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    [SupportedOSPlatform("windows")]
    public static class DriverManager
    {
        private static List<DriverItem> _cachedDrivers = new();

        /// <summary>
        /// Obtém a lista de drivers do sistema via WMI de forma assíncrona.
        /// </summary>
        public static async Task<List<DriverItem>> GetSystemDriversAsync(bool includeMicrosoft = false)
        {
            return await Task.Run(() =>
            {

                // Típico: 50-200 drivers em sistemas comuns
                var list = new List<DriverItem>(200);
                try
                {
                    using var searcher = new ManagementObjectSearcher("SELECT DeviceName, DriverProviderName, DriverVersion, DriverDate, InfName, DeviceID FROM Win32_PnPSignedDriver");
                    using var results = searcher.Get();

                    foreach (ManagementObject item in results)
                    {
                        using (item)
                        {
                            string provider = item["DriverProviderName"]?.ToString() ?? "Genérico";
                            string name = item["DeviceName"]?.ToString() ?? "Dispositivo Desconhecido";

                            bool isMicrosoft = provider == "Microsoft" || provider == "Microsoft Corporation";

                            if (!string.IsNullOrEmpty(name) && (includeMicrosoft || !isMicrosoft))
                            {
                                string rawDate = item["DriverDate"]?.ToString() ?? "";
                                string prettyDate = rawDate;

                                if (rawDate.Length >= 8)
                                    prettyDate = $"{rawDate.Substring(6, 2)}/{rawDate.Substring(4, 2)}/{rawDate.Substring(0, 4)}";

                                list.Add(new DriverItem
                                {
                                    DeviceName = name,
                                    Provider = provider,
                                    Version = item["DriverVersion"]?.ToString() ?? "0.0.0.0",
                                    Date = prettyDate,
                                    InfName = item["InfName"]?.ToString() ?? "",
                                    HardwareId = item["DeviceID"]?.ToString() ?? ""
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError("GetSystemDrivers", ex.Message);
                }

                _cachedDrivers = list.OrderBy(x => x.DeviceName).ToList();
                return _cachedDrivers;
            });
        }

        /// <summary>
        /// Verifica drivers antigos (data > 2 anos).
        /// </summary>
        public static async Task<List<DriverItem>> CheckForOutdatedDrivers()
        {
            Logger.Log("Iniciando verificação de drivers obsoletos...");


            var sourceList = _cachedDrivers.Count > 0 ? _cachedDrivers : await GetSystemDriversAsync(false);

            return await Task.Run(() =>
            {
                var outdated = new List<DriverItem>();

                foreach (var driver in sourceList)
                {
                    if (DateTime.TryParse(driver.Date, out DateTime dDate))
                    {
                        if (dDate < DateTime.Now.AddYears(-2)) outdated.Add(driver);
                    }
                }

                Logger.Log($"[SCAN] Análise concluída. {outdated.Count} drivers parecem antigos.");
                return outdated;
            });
        }

        /// <summary>
        /// Instala drivers de arquivos compactados (CAB/ZIP), pastas ou arquivos INF diretamente.
        /// </summary>
        public static async Task<(bool Success, string Message)> SmartInstallDriver(string path)
        {
            Logger.Log($"Iniciando instalação inteligente: {path}");

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Logger.LogError("SmartInstall", "Arquivo não encontrado.");
                return (false, "Arquivo ou pasta não encontrado.");
            }

            // Se for pasta ou INF direto, manda instalar
            if (Directory.Exists(path) || path.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
            {
                string targetPath = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
                return InstallDriversFromFolder(targetPath);
            }

            // Se for arquivo compactado, extrai
            string ext = Path.GetExtension(path).ToLower();
            string tempFolder = Path.Combine(Path.GetTempPath(), "KitLugia_Driver_" + Guid.NewGuid().ToString().Substring(0, 8));

            try
            {
                Directory.CreateDirectory(tempFolder);
                Logger.Log($"Extraindo pacote para: {tempFolder}...");
                bool extracted = false;

                await Task.Run(() =>
                {
                    if (ext == ".zip")
                    {
                        ZipFile.ExtractToDirectory(path, tempFolder);
                        extracted = true;
                    }
                    else if (ext == ".cab")
                    {
                        // CAB do Windows Update precisa do comando 'expand'
                        string args = $"\"{path}\" -F:* \"{tempFolder}\"";
                        SystemUtils.RunExternalProcess("expand.exe", args, hidden: true);
                        extracted = true;
                    }
                });

                if (!extracted)
                {
                    Logger.LogError("SmartInstall", "Formato não suportado.");
                    return (false, "Formato não suportado. Use .CAB, .ZIP ou uma Pasta.");
                }

                // Instala da pasta temporária
                var result = InstallDriversFromFolder(tempFolder);

                // Limpeza
                try { Directory.Delete(tempFolder, true); } catch { }

                return result;
            }
            catch (Exception ex)
            {
                Logger.LogError("SmartInstall", ex.Message);
                try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true); } catch { }
                return (false, $"Erro na extração: {ex.Message}");
            }
        }

        /// <summary>
        /// Instala todos os drivers de uma pasta (e subpastas) usando pnputil.
        /// </summary>
        public static (bool Success, string Message) InstallDriversFromFolder(string folderPath)
        {
            if (!Directory.Exists(folderPath)) return (false, "Pasta inválida.");

            try
            {
                Logger.Log($"Executando PnPUtil na pasta: {folderPath}");
                string args = $"/add-driver \"{folderPath}\\*.inf\" /subdirs /install";

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(psi)!;
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                // pnputil retorna 0 em sucesso, ou 259/outro no failed
                if (process.ExitCode == 0 || process.ExitCode == 259)
                {
                    Logger.Log("[SUCESSO] Driver instalado e adicionado ao repositório (ou nenhuma alteração necessária).");
                    return (true, "Instalação concluída com sucesso!");
                }
                else
                {
                    Logger.Log($"[FALHA] PnPUtil Código: {process.ExitCode}. Detalhes: {output}");
                    return (false, "Nenhum driver compatível foi instalado.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("InstallDrivers", ex.Message);
                return (false, ex.Message);
            }
        }

        public static (bool Success, string Message) UninstallDriver(string infName)
        {
            if (string.IsNullOrWhiteSpace(infName)) return (false, "Driver inválido.");

            try
            {
                Logger.Log($"Tentando remover driver: {infName}...");
                string args = $"/delete-driver {infName} /uninstall /force";

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(psi)!;
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    Logger.LogError("UninstallDriver", output);
                    if (output.Contains("in use", StringComparison.OrdinalIgnoreCase) || output.Contains("em uso", StringComparison.OrdinalIgnoreCase))
                        return (false, "O driver está em uso. Reinicie e tente novamente.");

                    return (false, $"Falha ao remover. Código: {process.ExitCode}");
                }

                Logger.Log("[SUCESSO] Driver removido.");
                return (true, "Driver removido com sucesso.");
            }
            catch (Exception ex)
            {
                Logger.LogError("UninstallDriver", ex.Message);
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// Pesquisa segura no Catálogo Microsoft usando o ID de Hardware.
        /// </summary>
        public static void SearchDriverOnWeb(string deviceName, string hardwareId)
        {
            try
            {
                Logger.Log($"Abrindo navegador para buscar: {deviceName}");
                string url;

                // Detectar vendor pelo Hardware ID e abrir site oficial
                if (!string.IsNullOrEmpty(hardwareId))
                {
                    string hwIdUpper = hardwareId.ToUpper();

                    // Intel (VID_8086)
                    if (hwIdUpper.Contains("VID_8086"))
                    {
                        string query = Uri.EscapeDataString(deviceName);
                        url = $"https://www.intel.com/content/www/us/en/download-center/home.html?q={query}";
                        Logger.Log($"[INFO] Detectado Intel, abrindo site oficial");
                    }
                    // NVIDIA (VID_10DE)
                    else if (hwIdUpper.Contains("VID_10DE"))
                    {
                        url = $"https://www.nvidia.com/Download/index.aspx";
                        Logger.Log($"[INFO] Detectado NVIDIA, abrindo site oficial");
                    }
                    // AMD (VID_1002 ou VID_1022)
                    else if (hwIdUpper.Contains("VID_1002") || hwIdUpper.Contains("VID_1022"))
                    {
                        url = $"https://www.amd.com/support";
                        Logger.Log($"[INFO] Detectado AMD, abrindo site oficial");
                    }
                    // Realtek (VID_10EC)
                    else if (hwIdUpper.Contains("VID_10EC"))
                    {
                        url = $"https://www.realtek.com/Download/List?cate_id=584";
                        Logger.Log($"[INFO] Detectado Realtek, abrindo site oficial");
                    }
                    // Microsoft (VID_045E)
                    else if (hwIdUpper.Contains("VID_045E"))
                    {
                        url = $"https://support.microsoft.com/hardware";
                        Logger.Log($"[INFO] Detectado Microsoft, abrindo site oficial");
                    }
                    // Outros - usa Microsoft Update Catalog
                    else
                    {
                        string queryId = Uri.EscapeDataString(hardwareId);
                        url = $"https://www.catalog.update.microsoft.com/Search.aspx?q={queryId}";
                        Logger.Log($"[INFO] Vendor não identificado, abrindo Microsoft Update Catalog");
                    }
                }
                else
                {
                    // Fallback - Google search
                    string query = $"{deviceName} driver official download";
                    url = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}";
                    Logger.Log($"[INFO] Hardware ID vazio, usando Google search");
                }

                Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
            }
            catch (Exception ex)
            {
                Logger.LogError("WebSearch", ex.Message);
            }
        }

        public static (bool Success, string Message) BackupDrivers(string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(destinationPath)) return (false, "Caminho inválido.");
            try
            {
                Logger.Log($"Iniciando backup de drivers para: {destinationPath}");
                if (!Directory.Exists(destinationPath)) Directory.CreateDirectory(destinationPath);

                string command = $"/c dism.exe /online /export-driver /destination:\"{destinationPath}\"";
                // Roda visível para o usuário acompanhar o DISM
                SystemUtils.RunExternalProcess("cmd.exe", $"{command} & timeout 5", hidden: false, waitForExit: false);

                return (true, "Backup iniciado (janela externa).");
            }
            catch (Exception ex)
            {
                Logger.LogError("BackupDrivers", ex.Message);
                return (false, ex.Message);
            }
        }

        public static void OpenWindowsUpdateSettings()
        {
            try
            {
                Logger.Log("Abrindo configurações do Windows Update...");
                Process.Start(new ProcessStartInfo("ms-settings:windowsupdate-action") { UseShellExecute = true });
            }
            catch { }
        }

        public static void ExportDriverListToTxt(string filePath)
        {
            try
            {

                // Típico: 50-200 linhas (depende da quantidade de drivers)
                var sb = new StringBuilder(8192);
                sb.AppendLine("=== RELATÓRIO DE DRIVERS (KIT LUGIA) ===");
                sb.AppendLine($"Data: {DateTime.Now}");
                sb.AppendLine("========================================");

                var list = _cachedDrivers.Count > 0 ? _cachedDrivers : GetSystemDriversAsync(true).GetAwaiter().GetResult();
                foreach (var d in list)
                {
                    sb.AppendLine($"Dispositivo: {d.DeviceName}");
                    sb.AppendLine($"Fabricante:  {d.Provider}");
                    sb.AppendLine($"Versão:      {d.Version}");
                    sb.AppendLine($"Data:        {d.Date}");
                    sb.AppendLine($"INF:         {d.InfName}");
                    sb.AppendLine($"ID:          {d.HardwareId}");
                    sb.AppendLine("----------------------------------------");
                }
                File.WriteAllText(filePath, sb.ToString());
                Logger.Log($"Lista de drivers exportada com sucesso para: {filePath}");
            }
            catch (Exception ex)
            {
                Logger.LogError("ExportTxt", ex.Message);
            }
        }

        // =========================================================
        // MICROSOFT UPDATE CATALOG INTEGRATION
        // =========================================================

        /// <summary>
        /// Registra o Microsoft Update como fonte adicional do Windows Update Agent.
        /// GUID: 7971f918-a847-4430-9279-4a52d1efe18d
        /// </summary>
        public static (bool Success, string Message) RegisterMicrosoftUpdateSource()
        {
            try
            {
                Logger.Log("Registrando Microsoft Update como fonte...");
                var updateSvcType = Type.GetTypeFromProgID("Microsoft.Update.ServiceManager");
                if (updateSvcType == null) return (false, "Não foi possível obter o tipo Microsoft.Update.ServiceManager.");
                dynamic updateSvc = Activator.CreateInstance(updateSvcType)!;
                updateSvc.AddService2("7971f918-a847-4430-9279-4a52d1efe18d", 7, "");
                Logger.Log("[SUCESSO] Microsoft Update registrado como fonte.");
                return (true, "Microsoft Update registrado com sucesso!");
            }
            catch (Exception ex)
            {
                Logger.LogError("RegisterMicrosoftUpdate", ex.Message);
                return (false, $"Erro ao registrar Microsoft Update: {ex.Message}");
            }
        }

        /// <summary>
        /// Busca todos os drivers disponíveis no Microsoft Update Catalog (método legado WUA API).
        /// Este método é mantido como fallback caso o HTTP scraping falhe.
        /// </summary>
        public static (bool Success, string Message, List<MicrosoftUpdateDriver> Drivers) SearchDriversFromCatalog()
        {
            try
            {
                Logger.Log("Buscando drivers no Microsoft Update Catalog...");

                var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
                if (sessionType == null) return (false, "Não foi possível obter o tipo Microsoft.Update.Session.", new List<MicrosoftUpdateDriver>());
                dynamic session = Activator.CreateInstance(sessionType)!;
                dynamic searcher = session.CreateUpdateSearcher();
                searcher.SearchScope = 1; // MachineOnly
                searcher.IncludePotentiallySupersededUpdates = true;
                searcher.Online = true; // Importante: busca online, não apenas local

                // Busca TODOS os drivers (instalados e não instalados) - igual WuMgr/Cabbie
                string criteria = "Type='Driver'";
                dynamic searchResult;
                dynamic updates;

                // Tenta 1: Windows Update padrão (ServerSelection=2)
                try
                {
                    Logger.Log("[DEBUG] Tentando Windows Update padrão (ServerSelection=2)...");
                    searcher.ServerSelection = 2; // Windows Update
                    // Não define ServiceID para Windows Update padrão
                    searchResult = searcher.Search(criteria);
                    updates = searchResult.Updates;

                    int totalDrivers = updates.Count;
                    Logger.Log($"[DEBUG] Windows Update encontrou: {totalDrivers} drivers");

                    if (totalDrivers > 0)
                    {
                        // Se encontrou drivers, usa este resultado
                        var drivers = ProcessDriverUpdates(updates);
                        Logger.Log($"[SUCESSO] Encontrados {drivers.Count} drivers no Windows Update padrão.");
                        return (true, $"Encontrados {drivers.Count} drivers disponíveis.", drivers);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[DEBUG] Windows Update padrão falhou: {ex.Message}");
                }

                // Tenta 2: Microsoft Update (ServerSelection=3)
                try
                {
                    Logger.Log("[DEBUG] Tentando Microsoft Update (ServerSelection=3, ServiceID=7971f918-a847-4430-9279-4a52d1efe18d)...");
                    searcher.ServerSelection = 3; // Third Party
                    searcher.ServiceID = "7971f918-a847-4430-9279-4a52d1efe18d"; // Microsoft Update
                    searchResult = searcher.Search(criteria);
                    updates = searchResult.Updates;

                    int totalDrivers = updates.Count;
                    Logger.Log($"[DEBUG] Microsoft Update encontrou: {totalDrivers} drivers");

                    if (totalDrivers > 0)
                    {
                        // Se encontrou drivers, usa este resultado
                        var drivers = ProcessDriverUpdates(updates);
                        Logger.Log($"[SUCESSO] Encontrados {drivers.Count} drivers no Microsoft Update.");
                        return (true, $"Encontrados {drivers.Count} drivers disponíveis.", drivers);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[DEBUG] Microsoft Update falhou: {ex.Message}");
                }

                // Tenta 3: Default (ServerSelection=0)
                try
                {
                    Logger.Log("[DEBUG] Tentando Default (ServerSelection=0)...");
                    searcher.ServerSelection = 0; // Default
                    // Não define ServiceID para Default
                    searchResult = searcher.Search(criteria);
                    updates = searchResult.Updates;

                    int totalDrivers = updates.Count;
                    Logger.Log($"[DEBUG] Default encontrou: {totalDrivers} drivers");

                    if (totalDrivers > 0)
                    {
                        // Se encontrou drivers, usa este resultado
                        var drivers = ProcessDriverUpdates(updates);
                        Logger.Log($"[SUCESSO] Encontrados {drivers.Count} drivers no serviço Default.");
                        return (true, $"Encontrados {drivers.Count} drivers disponíveis.", drivers);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[DEBUG] Default falhou: {ex.Message}");
                }

                // Nenhum serviço encontrou drivers
                Logger.Log("[AVISO] Nenhum serviço encontrou drivers.");
                return (true, "Nenhum driver encontrado nos serviços disponíveis.", new List<MicrosoftUpdateDriver>());
            }
            catch (Exception ex)
            {
                Logger.LogError("SearchDriversFromCatalog", ex.Message);
                return (false, $"Erro ao buscar drivers: {ex.Message}", new List<MicrosoftUpdateDriver>());
            }
        }

        /// <summary>
        /// Processa a coleção de updates do WUA API e converte para lista de MicrosoftUpdateDriver.
        /// </summary>
        private static List<MicrosoftUpdateDriver> ProcessDriverUpdates(dynamic updates)
        {
            var drivers = new List<MicrosoftUpdateDriver>();

            foreach (dynamic update in updates)
            {
                try
                {
                    string title = update.Title ?? "";
                    string driverModel = update.DriverModel ?? "";
                    string driverManufacturer = update.DriverManufacturer ?? "";
                    string driverClass = update.DriverClass ?? "";
                    string driverVersion = update.DriverVersion ?? "";
                    string driverVerDate = update.DriverVerDate ?? "";

                    drivers.Add(new MicrosoftUpdateDriver
                    {
                        Title = title,
                        DriverModel = driverModel,
                        DriverVerDate = driverVerDate,
                        DriverClass = driverClass,
                        DriverManufacturer = driverManufacturer,
                        DriverVersion = driverVersion,
                        UpdateObject = update,
                        IsDownloaded = false
                    });
                }
                catch
                {
                    // Ignora updates que causam erro
                    continue;
                }
            }

            Logger.Log($"[DEBUG] Drivers processados com sucesso: {drivers.Count}");
            return drivers;
        }

        /// <summary>
        /// Busca TODAS as versões de drivers disponíveis para um Hardware ID específico (incluindo instaladas).
        /// Seguindo padrão WuMgr/Cabbie: busca geral + filtro por Hardware ID.
        /// </summary>
        public static (bool Success, string Message, List<MicrosoftUpdateDriver> Drivers) SearchDriverVersionsByHardwareId(string hardwareId, string installedVersion = "")
        {
            try
            {
                Logger.Log($"Buscando versões de driver para Hardware ID: {hardwareId}");

                var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
                if (sessionType == null) return (false, "Não foi possível obter o tipo Microsoft.Update.Session.", new List<MicrosoftUpdateDriver>());
                dynamic session = Activator.CreateInstance(sessionType)!;
                dynamic searcher = session.CreateUpdateSearcher();
                searcher.ServiceID = "7971f918-a847-4430-9279-4a52d1efe18d";
                searcher.SearchScope = 1; // MachineOnly
                searcher.ServerSelection = 3; // Third Party

                // Busca TODOS os drivers (instalados e não instalados) - padrão WuMgr/Cabbie
                // Não filtra por Hardware ID na query, filtra depois no código C#
                string criteria = "Type='Driver'";
                dynamic searchResult = searcher.Search(criteria);
                dynamic updates = searchResult.Updates;

                int totalDrivers = updates.Count;
                Logger.Log($"[DEBUG] Total de drivers encontrados no Microsoft Update: {totalDrivers}");

                var drivers = new List<MicrosoftUpdateDriver>();

                // Extrair partes do Hardware ID para busca mais flexível
                // Exemplo: USB\VID_1532&PID_0099&MI_02\6&28B5AAD&0&0002
                // Partes: USB, VID_1532, 1532, PID_0099, 0099
                var hwidParts = new List<string>();
                if (!string.IsNullOrEmpty(hardwareId))
                {
                    hwidParts.Add(hardwareId);
                    
                    // Extrair prefixo (USB, HID, PCI, etc.)
                    if (hardwareId.Contains("\\"))
                    {
                        var prefix = hardwareId.Split('\\')[0];
                        hwidParts.Add(prefix);
                    }

                    // Extrair VID e PID
                    if (hardwareId.Contains("VID_"))
                    {
                        var vidIndex = hardwareId.IndexOf("VID_");
                        var vidPart = hardwareId.Substring(vidIndex, 8); // VID_XXXX
                        hwidParts.Add(vidPart);
                        hwidParts.Add(vidPart.Replace("VID_", "")); // Apenas os números
                    }
                    if (hardwareId.Contains("PID_"))
                    {
                        var pidIndex = hardwareId.IndexOf("PID_");
                        var pidPart = hardwareId.Substring(pidIndex, 8); // PID_XXXX
                        hwidParts.Add(pidPart);
                        hwidParts.Add(pidPart.Replace("PID_", "")); // Apenas os números
                    }
                }

                Logger.Log($"[DEBUG] Partes do Hardware ID para busca: {string.Join(", ", hwidParts)}");

                int checkedCount = 0;
                foreach (dynamic update in updates)
                {
                    try
                    {
                        checkedCount++;
                        
                        // Extrair informações básicas
                        string title = update.Title ?? "";
                        string driverModel = update.DriverModel ?? "";
                        string driverManufacturer = update.DriverManufacturer ?? "";
                        string driverClass = update.DriverClass ?? "";
                        string driverProvider = update.DriverProvider ?? "";

                        // Verificar match com qualquer parte do Hardware ID
                        bool matchFound = false;

                        foreach (var part in hwidParts)
                        {
                            // Filtro 1: Título contém parte do Hardware ID
                            if (!string.IsNullOrEmpty(title) && title.Contains(part, StringComparison.OrdinalIgnoreCase))
                            {
                                matchFound = true;
                                break;
                            }

                            // Filtro 2: Modelo contém parte do Hardware ID
                            if (!string.IsNullOrEmpty(driverModel) && driverModel.Contains(part, StringComparison.OrdinalIgnoreCase))
                            {
                                matchFound = true;
                                break;
                            }

                            // Filtro 3: Fabricante contém parte do Hardware ID
                            if (!string.IsNullOrEmpty(driverManufacturer) && driverManufacturer.Contains(part, StringComparison.OrdinalIgnoreCase))
                            {
                                matchFound = true;
                                break;
                            }

                            // Filtro 4: Fabricante + Modelo contém parte do Hardware ID
                            if (!string.IsNullOrEmpty(driverManufacturer) && !string.IsNullOrEmpty(driverModel))
                            {
                                if ((driverManufacturer + " " + driverModel).Contains(part, StringComparison.OrdinalIgnoreCase))
                                {
                                    matchFound = true;
                                    break;
                                }
                            }

                            // Filtro 5: Provider contém parte do Hardware ID
                            if (!string.IsNullOrEmpty(driverProvider) && driverProvider.Contains(part, StringComparison.OrdinalIgnoreCase))
                            {
                                matchFound = true;
                                break;
                            }

                            // Filtro 6: Classe contém parte do Hardware ID
                            if (!string.IsNullOrEmpty(driverClass) && driverClass.Contains(part, StringComparison.OrdinalIgnoreCase))
                            {
                                matchFound = true;
                                break;
                            }
                        }

                        // Se encontrou match, adiciona o driver
                        if (matchFound)
                        {
                            Logger.Log($"[DEBUG] Match encontrado: {title} - {driverManufacturer} {driverModel}");
                            
                            var catalogDriver = new MicrosoftUpdateDriver
                            {
                                Title = title,
                                DriverModel = driverModel,
                                DriverVerDate = update.DriverVerDate ?? "",
                                DriverClass = driverClass,
                                DriverManufacturer = driverManufacturer,
                                DriverVersion = update.DriverVersion ?? "",
                                UpdateObject = update,
                                IsDownloaded = false,
                                InstalledVersion = installedVersion
                            };

                            // Comparar versões
                            if (!string.IsNullOrEmpty(installedVersion) && !string.IsNullOrEmpty(catalogDriver.DriverVersion))
                            {
                                if (catalogDriver.DriverVersion == installedVersion)
                                {
                                    catalogDriver.ComparisonStatus = "Atualizado";
                                }
                                else if (IsNewerVersion(catalogDriver.DriverVersion, installedVersion))
                                {
                                    catalogDriver.ComparisonStatus = "Mais Novo";
                                }
                                else
                                {
                                    catalogDriver.ComparisonStatus = "Antigo";
                                }
                            }
                            else
                            {
                                catalogDriver.ComparisonStatus = "Desconhecido";
                            }

                            drivers.Add(catalogDriver);
                        }
                    }
                    catch
                    {
                        // Ignora updates que causam erro
                        continue;
                    }
                }

                Logger.Log($"[DEBUG] Drivers verificados: {checkedCount} de {totalDrivers}");
                Logger.Log($"[DEBUG] Drivers com match: {drivers.Count}");

                // Ordenar por data (mais recente primeiro)
                drivers = drivers.OrderByDescending(d => d.DriverVerDate).ToList();

                Logger.Log($"[SUCESSO] Encontrados {drivers.Count} versões de driver para {hardwareId}.");
                return (true, $"Encontrados {drivers.Count} versões disponíveis.", drivers);
            }
            catch (Exception ex)
            {
                Logger.LogError("SearchDriverVersionsByHardwareId", ex.Message);
                return (false, $"Erro ao buscar versões: {ex.Message}", new List<MicrosoftUpdateDriver>());
            }
        }

        /// <summary>
        /// Compara duas versões de driver e retorna true se a primeira é mais nova.
        /// Formato esperado: X.X.X.X (ex: 31.0.15.3129)
        /// </summary>
        private static bool IsNewerVersion(string version1, string version2)
        {
            try
            {
                var v1Parts = version1.Split('.');
                var v2Parts = version2.Split('.');

                for (int i = 0; i < Math.Max(v1Parts.Length, v2Parts.Length); i++)
                {
                    int v1 = i < v1Parts.Length ? int.Parse(v1Parts[i]) : 0;
                    int v2 = i < v2Parts.Length ? int.Parse(v2Parts[i]) : 0;

                    if (v1 > v2) return true;
                    if (v1 < v2) return false;
                }
                return false; // Mesma versão
            }
            catch
            {
                return false; // Erro ao comparar, assume que não é mais novo
            }
        }

        /// <summary>
        /// Baixa drivers selecionados do Microsoft Update Catalog.
        /// </summary>
        public static (bool Success, string Message) DownloadDrivers(List<MicrosoftUpdateDriver> drivers)
        {
            try
            {
                if (drivers.Count == 0)
                    return (false, "Nenhum driver selecionado para download.");

                Logger.Log($"Baixando {drivers.Count} drivers do Microsoft Update Catalog...");

                var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
                if (sessionType == null) return (false, "Não foi possível obter o tipo Microsoft.Update.Session.");
                dynamic session = Activator.CreateInstance(sessionType)!;
                var updateCollType = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl");
                if (updateCollType == null) return (false, "Não foi possível obter o tipo Microsoft.Update.UpdateColl.");
                dynamic updatesToDownload = Activator.CreateInstance(updateCollType)!;

                foreach (var driver in drivers)
                {
                    if (driver.UpdateObject != null)
                        updatesToDownload.Add(driver.UpdateObject);
                }

                dynamic downloader = session.CreateUpdateDownloader();
                downloader.Updates = updatesToDownload;
                downloader.Download();

                // Marcar como baixado
                foreach (var driver in drivers)
                {
                    driver.IsDownloaded = true;
                }

                Logger.Log("[SUCESSO] Drivers baixados com sucesso.");
                return (true, "Drivers baixados com sucesso!");
            }
            catch (Exception ex)
            {
                Logger.LogError("DownloadDrivers", ex.Message);
                return (false, $"Erro ao baixar drivers: {ex.Message}");
            }
        }

        /// <summary>
        /// Instala drivers baixados do Microsoft Update Catalog.
        /// </summary>
        public static (bool Success, string Message, bool RebootRequired) InstallDrivers(List<MicrosoftUpdateDriver> drivers)
        {
            try
            {
                if (drivers.Count == 0)
                    return (false, "Nenhum driver selecionado para instalação.", false);

                Logger.Log($"Instalando {drivers.Count} drivers do Microsoft Update Catalog...");

                var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
                if (sessionType == null) return (false, "Não foi possível obter o tipo Microsoft.Update.Session.", false);
                dynamic session = Activator.CreateInstance(sessionType)!;
                var updateCollType = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl");
                if (updateCollType == null) return (false, "Não foi possível obter o tipo Microsoft.Update.UpdateColl.", false);
                dynamic updatesToInstall = Activator.CreateInstance(updateCollType)!;

                foreach (var driver in drivers)
                {
                    if (driver.UpdateObject != null && driver.IsDownloaded)
                        updatesToInstall.Add(driver.UpdateObject);
                }

                dynamic installer = session.CreateUpdateInstaller();
                installer.Updates = updatesToInstall;
                dynamic installResult = installer.Install();

                bool rebootRequired = installResult.RebootRequired;

                Logger.Log("[SUCESSO] Drivers instalados com sucesso.");
                return (true, "Drivers instalados com sucesso!", rebootRequired);
            }
            catch (Exception ex)
            {
                Logger.LogError("InstallDrivers", ex.Message);
                return (false, $"Erro ao instalar drivers: {ex.Message}", false);
            }
        }
    }
}