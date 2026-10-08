using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static class AdapterManager
    {
        public class NetworkAdapterInfo
        {
            public string Id { get; set; } = "";
            public string NetCfgInstanceId { get; set; } = "";
            public string Description { get; set; } = "";
            public string ConnectionName { get; set; } = "";
            public string CurrentMac { get; set; } = "";
            public string PermanentMac { get; set; } = "";
            public string Speed { get; set; } = "";
            public bool IsUp { get; set; }
            public bool SupportsSpoofing { get; set; }
        }

        // =================================================================
        // SNAPSHOT AO VIVO (.NET) — substitui os 2 processos PowerShell por
        // adaptador que antes eram disparados a CADA chamada (load da página +
        // tick de 3s = dezenas de spawns de powershell.exe por minuto).
        // Dado equivalente: Get-NetAdapter .MacAddress / .Status -eq 'Up'.
        // =================================================================

        private static Dictionary<string, (string Mac, bool IsUp)> BuildLiveInterfaceIndex()
        {
            var index = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    string mac = "";
                    try
                    {
                        var bytes = ni.GetPhysicalAddress().GetAddressBytes();
                        if (bytes.Length == 6 && !bytes.All(b => b == 0))
                            mac = string.Concat(bytes.Select(b => b.ToString("X2")));
                    }
                    catch { /* interface sem endereço físico legível */ }

                    bool isUp = ni.OperationalStatus == OperationalStatus.Up;

                    void Add(string key)
                    {
                        if (!string.IsNullOrWhiteSpace(key) && !index.ContainsKey(key))
                            index[key] = (mac, isUp);
                    }

                    Add(ni.Name);                   // nome amigável (="Ethernet") = ConnectionName
                    Add(ni.Description);            // descrição = DriverDesc
                    Add(ni.Id.Trim('{', '}'));      // GUID = NetCfgInstanceId
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"ℹ️ AdapterManager (live index): {ex.Message}");
            }
            return index;
        }

        private static (string Mac, bool IsUp) LookupLive(
            Dictionary<string, (string Mac, bool IsUp)> index,
            string connectionName, string driverDesc, string netCfgInstanceId)
        {
            if (!string.IsNullOrWhiteSpace(connectionName) && index.TryGetValue(connectionName, out var v))
                return v;
            if (!string.IsNullOrWhiteSpace(driverDesc) && index.TryGetValue(driverDesc, out v))
                return v;
            if (!string.IsNullOrWhiteSpace(netCfgInstanceId) && index.TryGetValue(netCfgInstanceId.Trim('{', '}'), out v))
                return v;
            return ("", false);
        }

        /// <summary>
        /// Lista adaptadores de rede físicos com detecção em múltiplas camadas:
        /// 1. Registry Characteristics bitmask (NCF_PHYSICAL vs NCF_VIRTUAL/SOFTWARE_ENUMERATED)
        /// 2. PnPDeviceID (ROOT\ ou SW\ são software-only)
        /// 3. Nome do driver (lista exaustiva de palavras-chave de adaptadores virtuais)
        /// 4. Marca de hardware reconhecível como fallback
        /// </summary>
        public static List<NetworkAdapterInfo> ListPhysicalAdapters()
        {
            var adapters = new List<NetworkAdapterInfo>();
            var virtualKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "virtual", "vpn", "tap", "tun", "wintun",
                "hamachi", "zerotier", "tailscale", "cloudflare", "warp",
                "radmin", "softether", "anyconnect", "pulse", "juniper",
                "globalprotect", "wireguard", "nord", "expressvpn",
                "proton", "pia ", "private internet access", "mullvad",
                "forticlient", "fortinet", "sonicwall", "openvpn",
                "vypr", "hotspot shield", "ipvanish", "cyberghost", "surfshark",
                "vmware", "vmnet", "virtualbox", "hyper-v", "hyperv",
                "docker", "wsl", "vethernet",
                "loopback", "bluetooth", "wan miniport", "pseudo",
                "miniport", "hosted network", "wi-fi direct", "wifi direct",
                "km-test", "microsoft kernel debug", "microsoft wifi direct",
                "microsoft hosted network", "ndis", "kernel debug"
            };
            var hwBrands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "realtek", "intel", "qualcomm", "broadcom", "atheros",
                "killer", "marvell", "mediatek", "nvidia", "sis",
                "3com", "cisco", "tp-link", "d-link", "netgear",
                "asus", "gigabyte", "mellanox", "chelsio", "solarflare",
                "tehuti", "startech"
            };

            try
            {
                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                var classKeyPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
                using var classKey = localMachine.OpenSubKey(classKeyPath);
                if (classKey == null) return adapters;

                // Snapshot ao vivo UMA vez por chamada (0 PowerShell;
                // antes: 2 spawns por adaptador, a cada chamada).
                var liveIndex = BuildLiveInterfaceIndex();

                foreach (var subKeyName in classKey.GetSubKeyNames().OrderBy(x => x))
                {
                    // Subchaves como "Properties", "NetCfgInstanceId" são metadados,
                    // não adaptadores — ignorar (podem lançar SecurityException)
                    if (!int.TryParse(subKeyName, out _)) continue;

                    try
                    {
                        using var adapterKey = classKey.OpenSubKey(subKeyName);
                        if (adapterKey == null) continue;

                        var driverDesc = adapterKey.GetValue("DriverDesc")?.ToString();
                        var netCfgInstanceId = adapterKey.GetValue("NetCfgInstanceID")?.ToString();
                        var matchingDeviceId = adapterKey.GetValue("MatchingDeviceId")?.ToString() ?? "";
                        var characteristics = (int)(adapterKey.GetValue("Characteristics", 0) ?? 0);

                        if (string.IsNullOrEmpty(driverDesc) || string.IsNullOrEmpty(netCfgInstanceId))
                            continue;

                        var lowerDesc = driverDesc.ToLowerInvariant();

                        // === CAMADA 1: Characteristics bitmask ===
                        // NCF_VIRTUAL = 0x0001, NCF_SOFTWARE_ENUMERATED = 0x0002, NCF_PHYSICAL = 0x0004
                        bool isVirtualFlag = (characteristics & 0x0001) != 0;
                        bool isSoftwareEnumerated = (characteristics & 0x0002) != 0;
                        bool isPhysicalFlag = (characteristics & 0x0004) != 0;

                        if (isVirtualFlag || isSoftwareEnumerated)
                            continue;

                        // === CAMADA 2: PnPDeviceID ===
                        if (!string.IsNullOrEmpty(matchingDeviceId))
                        {
                            var lowerPnp = matchingDeviceId.ToLowerInvariant();
                            if (lowerPnp.StartsWith("root\\") || lowerPnp.StartsWith("sw\\"))
                                continue;
                        }

                        // === CAMADA 3: Nome do driver ===
                        if (virtualKeywords.Any(k => lowerDesc.Contains(k)))
                            continue;

                        // === CAMADA 4: Fallback de marca ===
                        if (!isPhysicalFlag)
                        {
                            // Sem a flag NCF_PHYSICAL → só inclui se tiver marca reconhecível
                            if (!hwBrands.Any(h => lowerDesc.Contains(h)))
                                continue;
                        }

                        // Busca o nome amigável da conexão no registro
                        var connPath = $@"SYSTEM\CurrentControlSet\Control\Network\{{4D36E972-E325-11CE-BFC1-08002BE10318}}\{netCfgInstanceId}\Connection";
                        var connectionName = driverDesc;
                        try
                        {
                            using var connKey = localMachine.OpenSubKey(connPath);
                            if (connKey != null)
                            {
                                var name = connKey.GetValue("Name")?.ToString();
                                if (!string.IsNullOrEmpty(name))
                                    connectionName = name;
                            }
                        }
                        catch { /* chave Connection ausente — usa DriverDesc como nome */ }

                        var customMac = adapterKey.GetValue("NetworkAddress")?.ToString() ?? "";
                        var speed = adapterKey.GetValue("*Speed")?.ToString() ?? "";

                        // MAC ao vivo + Status via snapshot .NET (equivalente a
                        // Get-NetAdapter .MacAddress/.Status, mas sem processo algum).
                        var (liveMac, isUp) = LookupLive(liveIndex, connectionName, driverDesc, netCfgInstanceId);

                        // Sem MAC ao vivo (adaptador desabilitada / endereço zerado):
                        // usa o NetworkAddress (custom) do registro.
                        var currentMac = !string.IsNullOrEmpty(liveMac) ? liveMac : customMac;

                        var permanentMac = GetPermanentMac(subKeyName, netCfgInstanceId, connectionName);
                        var (supportsSpoofing, _) = CheckNetworkAddressSupport(subKeyName);

                        adapters.Add(new NetworkAdapterInfo
                        {
                            Id = subKeyName,
                            NetCfgInstanceId = netCfgInstanceId,
                            Description = driverDesc,
                            ConnectionName = connectionName,
                            CurrentMac = currentMac,
                            PermanentMac = permanentMac,
                            Speed = speed,
                            IsUp = isUp,
                            SupportsSpoofing = supportsSpoofing
                        });
                    }
                    catch (Exception ex)
                    {
                        // Perfil de rede corrompido/sem permissão — pula e segue para o próximo
                        KitLugia.Core.Logger.Log($"ℹ️ AdapterManager: {subKeyName}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"ℹ️ AdapterManager (enumeração): {ex.Message}");
            }

            return adapters;
        }

        // OUIs reais de fabricantes conhecidos (primeiros 24 bits / 3 bytes)
        private static readonly string[] _realOuiPrefixes =
        {
            "0000C0", "00037F", "0003E3", // Intel
            "0011D8", "001B21", "001CB3", // Intel
            "00241D", "0024D7", "0050B7", // Intel
            "0072C9", "00AA01", "0C54A5", // Intel
            "C81F66", "C83E99", "D067E5", // Intel
            "001E4F", "001EC2", "001F29", // Realtek
            "002522", "0060B3", "00E04C", // Realtek
            "38E08E", "3C7C3F", "4062C2", // Realtek
            "4C5E0C", "5087B8", "54C80F", // Realtek
            "6CB0CE", "70602E", "746305", // Realtek
            "804B20", "80FA5B", "8C6DDB", // Realtek
            "94103E", "9CEBE8", "A433D7", // Realtek
            "AC220B", "BC03AF", "C01E9B", // Realtek
            "C80E77", "CC6DA0", "D0C7C0", // Realtek
            "001502", "0021B9", "002469", // Broadcom
            "0A0050", "1A2BC3", "282642", // Broadcom
            "848506", "8CB82C", "94857A", // Broadcom
            "B0A939", "D8C4E9", "DC2B2A", // Broadcom
            "000379", "00223F", "0050F2", // Atheros/Qualcomm
            "0057B0", "045673", "102D96", // Qualcomm
            "A8144D", "ACE47E", "C0D044", // Qualcomm
            "0012A5", "001731", "002219", // Marvell
            "0010FA", "002128", "0050F0", // VMware (virtual)
            "080027", "005056", "000569", // VMware (virtual)
        };

        /// <summary>
        /// Valida um endereço MAC: 12 hex chars, unicast + locally administered.
        /// </summary>
        public static (bool Valid, string Cleaned) ValidateMac(string mac)
        {
            if (string.IsNullOrWhiteSpace(mac))
                return (false, "");

            var clean = mac.Replace("-", "").Replace(":", "").Replace(" ", "").ToUpper();
            if (clean.Length != 12)
                return (false, clean);
            if (!clean.All(c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')))
                return (false, clean);

            // Bit 0 = 0 (unicast), Bit 1 = 1 (locally administered)
            // 0x02, 0x06, 0x0A, 0x0E, 0x12, 0x16, ...
            if (byte.TryParse(clean.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var firstByte))
            {
                if ((firstByte & 0x03) != 0x02)
                    return (false, clean);
            }

            return (true, clean);
        }

        /// <summary>
        /// Gera um endereço MAC aleatório com aparência de fabricante real.
        /// Primeiro byte = 0x02 (unicast + locally administered, obrigatório para WiFi).
        /// Bytes 2-3 usam os 4 últimos hex digits de um OUI real (parece do fabricante).
        /// Últimos 3 bytes aleatórios.
        /// Formato: 12 caracteres hexa sem separadores (ex: 021E4FA1B2C3).
        /// </summary>
        public static string GenerateRandomMac()
        {
            var rng = Random.Shared;
            var ouiPrefix = _realOuiPrefixes[rng.Next(_realOuiPrefixes.Length)];
            var ouiSuffix = ouiPrefix.Substring(2);
            var nic = rng.Next(0x1000000).ToString("X6");
            return "02" + ouiSuffix + nic;
        }

        /// <summary>
        /// Define o MAC address no registro para o adaptador especificado.
        /// </summary>
        public static (bool Success, string Message) SetMacAddress(string adapterId, string macAddress)
        {
            try
            {
                var (valid, cleaned) = ValidateMac(macAddress);
                if (!valid)
                    return (false, $"MAC inválido: {macAddress}. Use 12 caracteres hexa (unicast + locally administered).");

                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                var path = $@"SYSTEM\CurrentControlSet\Control\Class\{{4d36e972-e325-11ce-bfc1-08002be10318}}\{adapterId}";
                using var adapterKey = localMachine.OpenSubKey(path, writable: true);
                if (adapterKey == null)
                    return (false, "Chave do adaptador não encontrada no registro.");

                adapterKey.SetValue("NetworkAddress", cleaned, RegistryValueKind.String);
                return (true, $"MAC address definido para {FormatMac(cleaned)}");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao definir MAC no registro: {ex.Message}");
            }
        }

        /// <summary>
        /// Remove o MAC personalizado do registro (restaura original de fábrica).
        /// </summary>
        public static (bool Success, string Message) RestoreOriginalMac(string adapterId)
        {
            try
            {
                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                var path = $@"SYSTEM\CurrentControlSet\Control\Class\{{4d36e972-e325-11ce-bfc1-08002be10318}}\{adapterId}";
                using var adapterKey = localMachine.OpenSubKey(path, writable: true);
                if (adapterKey == null)
                    return (false, "Chave do adaptador não encontrada no registro.");

                if (adapterKey.GetValue("NetworkAddress") != null)
                    adapterKey.DeleteValue("NetworkAddress");
                return (true, "MAC original restaurado. Reinicie o adaptador para aplicar.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao restaurar MAC original: {ex.Message}");
            }
        }

        /// <summary>
        /// Habilita ou desabilita o adaptador de rede via netsh.
        /// </summary>
        public static (bool Success, string Message) SetAdapterState(string connectionName, bool enable)
        {
            try
            {
                var state = enable ? "enable" : "disable";
                var result = SystemUtils.RunExternalProcess("netsh",
                    $"interface set interface name=\"{connectionName}\" admin={state}", hidden: true);

                // Marcadores bilíngues (pt-BR/en). O netsh retorna exit0 até para nome
                // inexistente, então a saída é a única pista — o check antigo só tinha
                // "não" e perdia "No more data is available" / "not found" em inglês.
                bool failed =
                    result.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    result.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                    result.Contains("não", StringComparison.OrdinalIgnoreCase) ||
                    result.Contains("negado", StringComparison.OrdinalIgnoreCase) ||
                    result.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                    result.Contains("no more data", StringComparison.OrdinalIgnoreCase) ||
                    result.Contains("cannot", StringComparison.OrdinalIgnoreCase) ||
                    result.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                    result.Contains("denied", StringComparison.OrdinalIgnoreCase);

                if (failed)
                    return (false, $"Falha ao {(enable ? "habilitar" : "desabilitar")} adaptador: {result.Trim()}");

                return (true, $"Adaptador {(enable ? "habilitado" : "desabilitado")} com sucesso.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro: {ex.Message}");
            }
        }

        /// <summary>
        /// Restarta o adaptador de rede via netsh (disabilita e reabilita).
        /// </summary>
        public static async Task<(bool Success, string Message)> RestartAdapterAsync(string connectionName)
        {
            try
            {
                var disableResult = SetAdapterState(connectionName, false);
                if (!disableResult.Success)
                    return (false, disableResult.Message);

                await Task.Delay(2000);

                var enableResult = SetAdapterState(connectionName, true);
                if (!enableResult.Success)
                    return (false, enableResult.Message);

                return (true, $"Adaptador '{connectionName}' reiniciado com sucesso.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao reiniciar adaptador: {ex.Message}");
            }
        }

        public static string GetCurrentMac(string connectionName)
        {
            if (string.IsNullOrWhiteSpace(connectionName)) return "";
            try
            {
                // Ao vivo via .NET — mesmo dado do Get-NetAdapter .MacAddress,
                // porém sem spawn de PowerShell (era chamado várias vezes por auto-detect).
                var index = BuildLiveInterfaceIndex();
                if (index.TryGetValue(connectionName, out var live) && !string.IsNullOrEmpty(live.Mac))
                    return live.Mac;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return "";
        }

        public static (bool Changed, string LiveMac) VerifyMacChange(string connectionName, string expectedMac)
        {
            var live = GetCurrentMac(connectionName);
            if (string.IsNullOrEmpty(live))
                return (false, "");

            var expected = expectedMac.ToUpper().Replace("-", "").Replace(":", "");
            return (live == expected, live);
        }

        // Cache de PermanentAddress por adaptador (MAC de fábrica não muda).
        private static readonly ConcurrentDictionary<string, string> _permanentMacCache = new();

        // Tabela única: UM processo PowerShell para TODOS os adaptadores (antes: 1 por
        // adaptador, a cada chamada). Chaves: InterfaceGuid (NetCfgInstanceId) e Name —
        // ambos ASCII, imunes a problemas de encoding da saída do PowerShell.
        private static readonly Lazy<Dictionary<string, string>> _permanentMacTable = new(() =>
        {
            var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var (exitCode, output) = SystemUtils.RunExternalProcessWithCode("powershell",
                    "-NoProfile -Command \"Get-NetAdapter | ForEach-Object { $_.InterfaceGuid.ToString() + '|' + $_.Name + '|' + $_.PermanentAddress }\"",
                    hidden: true);
                if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = line.Split(new[] { '|' }, 3);
                        if (parts.Length != 3) continue;
                        var mac = parts[2].Trim().Replace("-", "").Replace(":", "").ToUpperInvariant();
                        if (mac.Length != 12) continue;
                        var guid = parts[0].Trim().Trim('{', '}');
                        if (guid.Length > 0) table.TryAdd(guid, mac);
                        var name = parts[1].Trim();
                        if (name.Length > 0) table.TryAdd(name, mac);
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return table;
        });

        /// <summary>
        /// Lê o MAC permanente de fábrica do adaptador.
        /// Fontes (em ordem de confiabilidade):
        /// 1. Tabela única Get-NetAdapter .PermanentAddress (1 PS por processo, cacheada)
        /// 2. Registry NetworkSetup2\Interfaces\{GUID}\Kernel\PermanentAddress
        /// 3. Registry Class key OriginalNetworkAddress
        /// </summary>
        public static string GetPermanentMac(string adapterId, string netCfgInstanceId, string connectionName)
        {
            // Cache: o MAC de fábrica é imutável. Sem ele, cada refresh de 3s
            // disparava 1 PowerShell POR adaptador só para ler PermanentAddress.
            if (_permanentMacCache.TryGetValue(adapterId, out var cached))
                return cached;

            string result = "";

            // Fonte 1: tabela única Get-NetAdapter (UM processo PS para TODOS os adaptadores)
            try
            {
                if (result.Length < 12 && !string.IsNullOrEmpty(netCfgInstanceId) &&
                    _permanentMacTable.Value.TryGetValue(netCfgInstanceId.Trim('{', '}'), out var byGuid))
                    result = byGuid;
                if (result.Length < 12 && !string.IsNullOrEmpty(connectionName) &&
                    _permanentMacTable.Value.TryGetValue(connectionName, out var byName))
                    result = byName;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            // Fonte 2: Registry NetworkSetup2 → Kernel → PermanentAddress
            if (result.Length < 12)
            {
                try
                {
                    using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    var setup2Path = $@"SYSTEM\ControlSet001\Control\NetworkSetup2\Interfaces\{netCfgInstanceId}\Kernel";
                    using var kernelKey = localMachine.OpenSubKey(setup2Path);
                    if (kernelKey != null)
                    {
                        var permAddr = kernelKey.GetValue("PermanentAddress")?.ToString();
                        if (!string.IsNullOrWhiteSpace(permAddr) && permAddr.Length >= 12)
                            result = permAddr.ToUpper().Replace("-", "").Replace(":", "");
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            // Fonte 3: OriginalNetworkAddress (backup automático do Windows antes de aplicar spoof)
            if (result.Length < 12)
            {
                try
                {
                    using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    var classPath = $@"SYSTEM\CurrentControlSet\Control\Class\{{4d36e972-e325-11ce-bfc1-08002be10318}}\{adapterId}";
                    using var adapterKey = localMachine.OpenSubKey(classPath);
                    if (adapterKey != null)
                    {
                        var original = adapterKey.GetValue("OriginalNetworkAddress")?.ToString();
                        if (!string.IsNullOrWhiteSpace(original) && original.Length >= 12)
                            result = original.ToUpper().Replace("-", "").Replace(":", "");
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            if (result.Length >= 12)
                _permanentMacCache[adapterId] = result; // cacheia só acerto (falha pode ser temporária)
            return result;
        }

        /// <summary>
        /// Detecta se o driver do adaptador suporta NetworkAddress.
        /// Verifica a existência de NDI\params\NetworkAddress no registro.
        /// </summary>
        public static (bool Supported, bool HasNDIKey) CheckNetworkAddressSupport(string adapterId)
        {
            try
            {
                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                var paramsPath = $@"SYSTEM\CurrentControlSet\Control\Class\{{4d36e972-e325-11ce-bfc1-08002be10318}}\{adapterId}\NDI\params\NetworkAddress";
                using var paramsKey = localMachine.OpenSubKey(paramsPath);
                return (paramsKey != null, paramsKey != null);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return (false, false);
        }

        /// <summary>
        /// Tenta criar a chave NDI\params\NetworkAddress para adaptadores cujo driver
        /// não expõe o campo "Network Address" no Device Manager, mas pode aceitar
        /// o valor via registro se a struct for criada manualmente.
        /// </summary>
        public static (bool Success, string Message) EnsureNetworkAddressSupport(string adapterId)
        {
            try
            {
                var (supported, _) = CheckNetworkAddressSupport(adapterId);
                if (supported)
                    return (true, "NetworkAddress já é suportado por este driver.");

                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                var paramsBasePath = $@"SYSTEM\CurrentControlSet\Control\Class\{{4d36e972-e325-11ce-bfc1-08002be10318}}\{adapterId}\NDI\params";
                using var paramsBaseKey = localMachine.OpenSubKey(paramsBasePath, writable: true);
                if (paramsBaseKey == null)
                {
                    // Tenta criar NDI\params primeiro
                    var classPath = $@"SYSTEM\CurrentControlSet\Control\Class\{{4d36e972-e325-11ce-bfc1-08002be10318}}\{adapterId}";
                    using var adapterKey = localMachine.OpenSubKey(classPath, writable: true);
                    if (adapterKey == null)
                        return (false, "Não foi possível acessar a chave do adaptador no registro.");
                    using var ndiKey = adapterKey.CreateSubKey("NDI");
                    using var ndiParamsKey = ndiKey.CreateSubKey("params");
                    using var netAddrKey = ndiParamsKey.CreateSubKey("NetworkAddress");
                    PopulateNetworkAddressParams(netAddrKey);
                    return (true, "Chave NetworkAddress criada manualmente. Pode ser necessário reiniciar.");
                }

                using var netAddrKey2 = paramsBaseKey.CreateSubKey("NetworkAddress");
                PopulateNetworkAddressParams(netAddrKey2);
                return (true, "Chave NetworkAddress criada manualmente no NDI\\params.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao criar suporte NetworkAddress: {ex.Message}");
            }
        }

        private static void PopulateNetworkAddressParams(RegistryKey key)
        {
            key.SetValue("ParamDesc", "Network Address", RegistryValueKind.String);
            key.SetValue("optional", "1", RegistryValueKind.String);
            key.SetValue("type", "edit", RegistryValueKind.String);
            key.SetValue("uppercase", "1", RegistryValueKind.String);
            key.SetValue("limittext", "12", RegistryValueKind.String);
        }

        /// <summary>
        /// Restaura o MAC original usando o PermanentAddress real (se disponível),
        /// ou remove o NetworkAddress como fallback.
        /// </summary>
        public static (bool Success, string Message) RestoreOriginalMac(string adapterId, string netCfgInstanceId, string connectionName)
        {
            try
            {
                var permanent = GetPermanentMac(adapterId, netCfgInstanceId, connectionName);
                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                var path = $@"SYSTEM\CurrentControlSet\Control\Class\{{4d36e972-e325-11ce-bfc1-08002be10318}}\{adapterId}";
                using var adapterKey = localMachine.OpenSubKey(path, writable: true);
                if (adapterKey == null)
                    return (false, "Chave do adaptador não encontrada no registro.");

                if (!string.IsNullOrEmpty(permanent))
                {
                    adapterKey.SetValue("NetworkAddress", permanent, RegistryValueKind.String);
                    return (true, $"MAC permanente restaurado: {FormatMac(permanent)}");
                }
                else
                {
                    if (adapterKey.GetValue("NetworkAddress") != null)
                        adapterKey.DeleteValue("NetworkAddress");
                    return (true, "MAC original restaurado (NetworkAddress removido).");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao restaurar MAC original: {ex.Message}");
            }
        }

        private static string FormatMac(string clean)
        {
            if (clean.Length != 12) return clean;
            return string.Join(":", Enumerable.Range(0, 6).Select(i => clean.Substring(i * 2, 2)));
        }

        // =================================================================
        // OUI MIRROR MODE — mantém os primeiros 3 bytes (fabricante real)
        // =================================================================

        // =================================================================
        // AUTO-DETECT MAC — tenta MACs até um funcionar no adaptador
        // =================================================================
        public static async Task<string?> AutoDetectMacAsync(
            string adapterId, string connectionName, Action<string>? onProgress = null)
        {
            if (!string.IsNullOrEmpty(GetCurrentMac(connectionName)))
            {
                var origRestore = RestoreOriginalMac(adapterId, "", connectionName);
                if (origRestore.Success)
                {
                    await RestartAdapterAsync(connectionName);
                    await Task.Delay(1500);
                }
            }

            var validFirstBytes = new byte[]
            {
                0x02,0x06,0x0A,0x0E,0x12,0x16,0x1A,0x1E,
                0x22,0x26,0x2A,0x2E,0x32,0x36,0x3A,0x3E,
                0x42,0x46,0x4A,0x4E,0x52,0x56,0x5A,0x5E,
                0x62,0x66,0x6A,0x6E,0x72,0x76,0x7A,0x7E,
                0x82,0x86,0x8A,0x8E,0x92,0x96,0x9A,0x9E,
                0xA2,0xA6,0xAA,0xAE,0xB2,0xB6,0xBA,0xBE,
                0xC2,0xC6,0xCA,0xCE,0xD2,0xD6,0xDA,0xDE,
                0xE2,0xE6,0xEA,0xEE,0xF2,0xF6,0xFA,0xFE
            };
            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int maxAttempts = 40;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                // Gera MAC usando um first byte variado + OUI aleatório
                var rng = Random.Shared;
                byte firstByte = validFirstBytes[rng.Next(validFirstBytes.Length)];
                var ouiPrefix = _realOuiPrefixes[rng.Next(_realOuiPrefixes.Length)];
                var suffix = ouiPrefix[2..] + rng.Next(0x1000000).ToString("X6");
                string mac = firstByte.ToString("X2") + suffix;

                if (!tried.Add(mac)) continue;

                onProgress?.Invoke($"Tentativa {attempt + 1}/{maxAttempts}: {FormatMac(mac)}...");

                var setResult = SetMacAddress(adapterId, mac);
                if (!setResult.Success)
                {
                    onProgress?.Invoke($"Tentativa {attempt + 1}/{maxAttempts}: escrita falhou — {setResult.Message}");
                    continue;
                }

                var restartResult = await RestartAdapterAsync(connectionName);
                if (!restartResult.Success)
                {
                    onProgress?.Invoke($"Tentativa {attempt + 1}/{maxAttempts}: restart falhou — {restartResult.Message}");
                    continue;
                }

                await Task.Delay(1500);

                var (changed, liveMac) = VerifyMacChange(connectionName, mac);
                if (changed)
                {
                    onProgress?.Invoke($"✅ MAC {FormatMac(mac)} funcionou!");
                    return mac;
                }

                onProgress?.Invoke($"Tentativa {attempt + 1}/{maxAttempts}: MAC rejeitado (ao vivo: {FormatMac(liveMac)})");

                // Reverte o MAC que não funcionou antes de tentar o próximo
                RestoreOriginalMac(adapterId, "", connectionName);
                await RestartAdapterAsync(connectionName);
                await Task.Delay(1500);
            }

            onProgress?.Invoke("❌ Nenhum MAC funcionou após 40 tentativas.");
            return null;
        }

        // =================================================================
        // DHCP REFRESH — libera e renova IP
        // =================================================================
    }
}
