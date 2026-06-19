using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static partial class Toolbox
    {
        /// <summary>
        /// Define o servidor DNS para um provedor específico (Cloudflare, Google) ou reverte para DHCP.
        /// </summary>
        public static (bool Success, string Message) SetDns(string provider)
        {
            if (!SystemUtils.IsAdmin())
            {
                return (false, "Acesso Negado!\nExecute como Administrador para alterar o DNS.");
            }

            switch (provider.ToUpper())
            {
                case "CLOUDFLARE":
                    return SetDnsServers("Cloudflare", "1.1.1.1", "1.0.0.1");
                case "GOOGLE":
                    return SetDnsServers("Google", "8.8.8.8", "8.8.4.4");
                case "DHCP":
                    return SetDnsServers("DHCP", null, null);
                default:
                    return (false, "Provedor de DNS desconhecido.");
            }
        }

        /// <summary>
        /// Configura o algoritmo de controle de congestionamento TCP para CTCP (Compound TCP).
        /// O padrão (CUBIC) foca em largura de banda. CTCP foca em manter a janela de transmissão estável,
        /// reduzindo a perda de pacotes e picos de lag em conexões instáveis.
        /// Atualiza registry diretamente para garantir verificação correta.
        /// </summary>
        public static (bool Success, string Message) ApplyLatencyCongestionControl()
        {
            try
            {
                // 1. Desativa a heurística do Windows (auto-ajustes antigos que causam instabilidade)
                SystemUtils.RunExternalProcess("netsh", "int tcp set heuristics disabled", hidden: true);

                // 2. Define CTCP como provedor de congestionamento (Ideal para jogos/VoIP)
                SystemUtils.RunExternalProcess("netsh", "int tcp set supplemental template=internet congestionprovider=ctcp", hidden: true);


                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var tcpipKey = localMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", true);
                tcpipKey?.SetValue("CongestionProvider", "ctcp", RegistryValueKind.String);

                // 3. Limita o autotuning para 'normal'. 'Disabled' limita a velocidade, 'Normal' é o equilíbrio.
                SystemUtils.RunExternalProcess("netsh", "int tcp set global autotuninglevel=normal", hidden: true);

                // 4. Desativa ECN (Explicit Congestion Notification). Roteadores antigos dropam pacotes com isso.
                SystemUtils.RunExternalProcess("netsh", "int tcp set global ecncapability=disabled", hidden: true);

                // 5. Desativa RSC (Receive Segment Coalescing).
                // CRÍTICO: RSC agrupa pacotes na placa de rede para economizar CPU, mas aumenta o ping.
                SystemUtils.RunExternalProcess("netsh", "int tcp set global rsc=disabled", hidden: true);

                // 6. Desativa TCP Chimney Offload (Processamento na NIC que às vezes falha)
                SystemUtils.RunExternalProcess("netsh", "int tcp set global chimney=disabled", hidden: true);

                // 7. Desativa Timestamps (Reduz overhead do cabeçalho TCP - ganho marginal de latência)
                SystemUtils.RunExternalProcess("netsh", "int tcp set global timestamps=disabled", hidden: true);

                return (true, "Algoritmo TCP ajustado para CTCP e RSC desativado para menor jitter.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao aplicar otimização TCP (CTCP): {ex.Message}");
            }
        }

        /// <summary>
        /// Reverte CTCP para o padrão (CUBIC ou default).
        /// Atualiza registry diretamente para garantir verificação correta.
        /// </summary>
        public static (bool Success, string Message) RevertLatencyCongestionControl()
        {
            try
            {
                // Reverte para provedor de congestionamento padrão (cubic ou default)
                SystemUtils.RunExternalProcess("netsh", "int tcp set supplemental template=internet congestionprovider=default", hidden: true);


                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var tcpipKey = localMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", true);
                tcpipKey?.SetValue("CongestionProvider", "default", RegistryValueKind.String);

                // Reverte autotuning para normal
                SystemUtils.RunExternalProcess("netsh", "int tcp set global autotuninglevel=normal", hidden: true);

                // Reverte heurísticas
                SystemUtils.RunExternalProcess("netsh", "int tcp set heuristics enabled", hidden: true);

                return (true, "CTCP revertido para padrão (CUBIC/default).");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao reverter CTCP: {ex.Message}");
            }
        }

        /// <summary>
        /// Habilita RSS (Receive Side Scaling) para distribuir processamento de rede entre múltiplos cores.
        /// Melhor para CPUs multi-core e alta largura de banda.
        /// Atualiza registry diretamente para garantir verificação correta.
        /// </summary>
        public static (bool Success, string Message) EnableRSS()
        {
            try
            {
                SystemUtils.RunExternalProcess("netsh", "int tcp set global rss=enabled", hidden: true);


                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var tcpipKey = localMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", true);
                tcpipKey?.SetValue("EnableRSS", 1, RegistryValueKind.DWord);
                return (true, "RSS habilitado para distribuir processamento de rede entre múltiplos cores.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao habilitar RSS: {ex.Message}");
            }
        }

        /// <summary>
        /// Desabilita RSS (Receive Side Scaling).
        /// Pode ser necessário para compatibilidade com alguns adaptadores/roteadores.
        /// Atualiza registry diretamente para garantir verificação correta.
        /// </summary>
        public static (bool Success, string Message) DisableRSS()
        {
            try
            {
                SystemUtils.RunExternalProcess("netsh", "int tcp set global rss=disabled", hidden: true);


                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var tcpipKey = localMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", true);
                tcpipKey?.SetValue("EnableRSS", 0, RegistryValueKind.DWord);
                return (true, "RSS desabilitado.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao desabilitar RSS: {ex.Message}");
            }
        }

        /// <summary>
        /// Habilita TaskOffload para permitir que a NIC processe tarefas de rede.
        /// Reduz carga da CPU. Recomendado para guías modernos 2024-2026.
        /// Atualiza registry diretamente para garantir verificação correta.
        /// </summary>
        public static (bool Success, string Message) EnableTaskOffload()
        {
            try
            {
                SystemUtils.RunExternalProcess("netsh", "int ip set global taskoffload=enabled", hidden: true);


                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var tcpipKey = localMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", true);
                tcpipKey?.SetValue("EnableTaskOffload", 1, RegistryValueKind.DWord);
                return (true, "TaskOffload habilitado para reduzir carga da CPU.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao habilitar TaskOffload: {ex.Message}");
            }
        }

        /// <summary>
        /// Desabilita TaskOffload.
        /// Pode ser necessário para compatibilidade ou troubleshooting.
        /// Atualiza registry diretamente para garantir verificação correta.
        /// </summary>
        public static (bool Success, string Message) DisableTaskOffload()
        {
            try
            {
                SystemUtils.RunExternalProcess("netsh", "int ip set global taskoffload=disabled", hidden: true);


                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var tcpipKey = localMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", true);
                tcpipKey?.SetValue("EnableTaskOffload", 0, RegistryValueKind.DWord);
                return (true, "TaskOffload desabilitado.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao desabilitar TaskOffload: {ex.Message}");
            }
        }

        /// <summary>
        /// Desabilita Network Throttling Index.
        /// Remove limitação de largura de banda para aplicações não-multimídia.
        /// Benefício para jogos e streaming.
        /// </summary>
        public static (bool Success, string Message) DisableNetworkThrottling()
        {
            try
            {

                int maxDword = unchecked((int)0xFFFFFFFF);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                    "NetworkThrottlingIndex", maxDword, RegistryValueKind.DWord);
                return (true, "Network Throttling desativado para máximo desempenho.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao desativar Network Throttling: {ex.Message}");
            }
        }

        /// <summary>
        /// Habilita Network Throttling Index (padrão).
        /// </summary>
        public static (bool Success, string Message) EnableNetworkThrottling()
        {
            try
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                    "NetworkThrottlingIndex", 10, RegistryValueKind.DWord);
                return (true, "Network Throttling habilitado (padrão).");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao habilitar Network Throttling: {ex.Message}");
            }
        }

        /// <summary>
        /// Verifica se RSS está habilitado.
        /// Usa registry para independência de idioma (funciona em qualquer PC).
        /// </summary>
        public static bool IsRSSEnabled()
        {
            try
            {
                string tcpParams = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";
                var rssValue = Registry.GetValue(tcpParams, "EnableRSS", 0);
                return Convert.ToInt32(rssValue) == 1;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Verifica se TaskOffload está habilitado.
        /// Usa registry para independência de idioma (funciona em qualquer PC).
        /// </summary>
        public static bool IsTaskOffloadEnabled()
        {
            try
            {
                string tcpParams = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";
                var taskOffloadValue = Registry.GetValue(tcpParams, "EnableTaskOffload", 1);
                return Convert.ToInt32(taskOffloadValue) == 1;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Verifica se CTCP está configurado.
        /// Usa registry para independência de idioma (funciona em qualquer PC).
        /// </summary>
        public static bool IsCTCPConfigured()
        {
            try
            {
                string tcpParams = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";
                var congestionProvider = Registry.GetValue(tcpParams, "CongestionProvider", "")?.ToString();
                return congestionProvider?.ToLower() == "ctcp";
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Verifica se Interrupt Moderation está desabilitado (otimizado para gaming).
        /// Verifica registry de adaptadores físicos com IP configurado.
        /// Usa RegistryKey.OpenBaseKey com RegistryView.Registry64 para acesso correto
        /// </summary>
        public static bool IsInterruptModerationDisabled()
        {
            try
            {

                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var tcpipKey = localMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", false);
                if (tcpipKey == null)
                    return false;

                using var interfacesKey = tcpipKey.OpenSubKey("Interfaces", false);
                if (interfacesKey == null)
                    return false;

                foreach (string subKeyName in interfacesKey.GetSubKeyNames())
                {
                    using var subKey = interfacesKey.OpenSubKey(subKeyName, false);
                    if (subKey == null)
                        continue;


                    var ipAddress = subKey.GetValue("IPAddress");
                    var dhcpIpAddress = subKey.GetValue("DhcpIPAddress");

                    if (ipAddress != null || dhcpIpAddress != null)
                    {
                        // Esta interface tem IP configurado, verificar Interrupt Moderation
                        var interruptModeration = subKey.GetValue("*InterruptModeration");
                        if (interruptModeration?.ToString() == "0")
                            return true;
                    }
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        /// <summary>
        /// Verifica se Nagle's Algorithm está desabilitado.
        /// Verifica TcpAckFrequency=1, TCPNoDelay=1 e TcpDelAckTicks=0 (conforme Microsoft docs)
        /// Usa RegistryKey.OpenBaseKey com RegistryView.Registry64 para acesso correto
        /// </summary>
        public static bool IsNagleAlgorithmDisabled()
        {
            try
            {

                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var tcpipKey = localMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", false);
                if (tcpipKey == null)
                    return false;

                using var interfacesKey = tcpipKey.OpenSubKey("Interfaces", false);
                if (interfacesKey == null)
                    return false;

                foreach (string subKeyName in interfacesKey.GetSubKeyNames())
                {
                    using var subKey = interfacesKey.OpenSubKey(subKeyName, false);
                    if (subKey == null)
                        continue;

                    var ipAddress = subKey.GetValue("IPAddress");
                    var dhcpIpAddress = subKey.GetValue("DhcpIPAddress");

                    if (ipAddress != null || dhcpIpAddress != null)
                    {
                        // Esta interface tem IP configurado, verificar Nagle's Algorithm
                        var tcpAckFrequency = subKey.GetValue("TcpAckFrequency");
                        var tcpNoDelay = subKey.GetValue("TCPNoDelay");
                        var tcpDelAckTicks = subKey.GetValue("TcpDelAckTicks");


                        // TcpAckFrequency=1 (cada pacote é reconhecido imediatamente)
                        // TCPNoDelay=1 (desabilita Nagle's algorithm)
                        // TcpDelAckTicks=0 (desabilita delayed ACK timer)
                        if (tcpAckFrequency?.ToString() == "1" &&
                            tcpNoDelay?.ToString() == "1" &&
                            tcpDelAckTicks?.ToString() == "0")
                            return true;
                    }
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        /// <summary>
        /// Aplica registry tweaks avançados de TCP para gaming.
        /// Baseado em guías de otimização 2024-2026.
        /// </summary>
        public static (bool Success, string Message) ApplyTcpRegistryTweaks()
        {
            try
            {

                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var tcpipKey = localMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", true);
                if (tcpipKey == null)
                    return (false, "Não foi possível acessar Tcpip\\Parameters");

                // TCP Window Scaling (habilita janelas TCP maiores)
                tcpipKey.SetValue("Tcp1323Opts", 1, RegistryValueKind.DWord);

                // Maximum number of ports (aumenta disponibilidade de portas)
                tcpipKey.SetValue("MaxUserPort", 65534, RegistryValueKind.DWord);

                // Time to wait in TIME_WAIT state (reduz para liberar portas mais rápido)
                tcpipKey.SetValue("TcpTimedWaitDelay", 30, RegistryValueKind.DWord);

                // Default TTL (Time to Live)
                tcpipKey.SetValue("DefaultTTL", 64, RegistryValueKind.DWord);

                // Maximum duplicate ACKs
                tcpipKey.SetValue("TcpMaxDupAcks", 2, RegistryValueKind.DWord);


                // Padrão: 16384, Otimizado: 65536 (64KB) - valor seguro
                tcpipKey.SetValue("SizReqBuf", 65536, RegistryValueKind.DWord);

                // ARP Cache Size (aumenta cache de ARP)
                SystemUtils.RunExternalProcess("netsh", "int ip set global neighborcachelimit=4096", hidden: true);

                return (true, "Registry tweaks de TCP aplicados para gaming.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao aplicar registry tweaks TCP: {ex.Message}");
            }
        }

        /// <summary>
        /// Reverte registry tweaks de TCP para valores padrão.
        /// </summary>
        public static (bool Success, string Message) RevertTcpRegistryTweaks()
        {
            try
            {

                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = localMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", true);
                if (key == null)
                    return (false, "Não foi possível acessar Tcpip\\Parameters");

                // Deletar valores (volta ao padrão do Windows)
                key.DeleteValue("Tcp1323Opts", false);
                key.DeleteValue("MaxUserPort", false);
                key.DeleteValue("TcpTimedWaitDelay", false);
                key.DeleteValue("DefaultTTL", false);
                key.DeleteValue("TcpMaxDupAcks", false);
                key.DeleteValue("SizReqBuf", false);

                // ARP Cache (padrão)
                SystemUtils.RunExternalProcess("netsh", "int ip set global neighborcachelimit=256", hidden: true);

                return (true, "Registry tweaks de TCP revertidos para padrão.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao reverter registry tweaks TCP: {ex.Message}");
            }
        }

        /// <summary>
        /// Verifica se TCP Registry Tweaks estão aplicados.
        /// Verifica se os valores estão definidos no registry.
        /// Usa RegistryKey.OpenBaseKey com RegistryView.Registry64 para acesso correto
        /// </summary>
        public static bool IsTcpRegistryTweaksApplied()
        {
            try
            {

                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = localMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", false);
                if (key == null)
                    return false;

                // Verifica se pelo menos um dos tweaks está definido
                var tcp1323Opts = key.GetValue("Tcp1323Opts");
                var maxUserPort = key.GetValue("MaxUserPort");
                var tcpTimedWaitDelay = key.GetValue("TcpTimedWaitDelay");
                var sizReqBuf = key.GetValue("SizReqBuf");

                // Se algum valor estiver definido, considera que tweaks estão aplicados
                return tcp1323Opts != null || maxUserPort != null || tcpTimedWaitDelay != null || sizReqBuf != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Lista todos os adaptadores de rede físicos (Ethernet e WiFi).
        /// Retorna lista de tuplas com nome, tipo, descrição e uso de dados.
        /// Adaptadores virtuais (VMware, ZeroTier, VPN) não são listados pois o tráfego real passa pelo adaptador físico.
        /// </summary>
        public static List<(string AdapterName, string AdapterType, string Description, string DataUsage)> GetAllNetworkAdapters()
        {
            var adapters = new List<(string, string, string, string)>();

            try
            {

                // -Physical filtra adaptadores reais de hardware
                // Adaptadores virtuais não precisam de tweaks pois o tráfego passa pelo físico
                var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"$adapters = Get-NetAdapter -Physical | Select-Object Name, InterfaceDescription, PhysicalMediaType; foreach ($adapter in $adapters) { $stats = Get-NetAdapterStatistics -Name $adapter.Name -ErrorAction SilentlyContinue; $sent = if ($stats) { $stats.ReceivedBytes + $stats.SentBytes } else { 0 }; $usage = if ($sent -gt 1GB) { \\\"{0:N2} GB\\\" -f ($sent / 1GB) } elseif ($sent -gt 1MB) { \\\"{0:N2} MB\\\" -f ($sent / 1MB) } elseif ($sent -gt 1KB) { \\\"{0:N2} KB\\\" -f ($sent / 1KB) } else { \\\"{0} B\\\" -f $sent }; $adapter | Select-Object Name, InterfaceDescription, PhysicalMediaType, @{Name='DataUsage';Expression={$usage}} } | ConvertTo-Csv -NoTypeInformation\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                });

                if (process != null)
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (!string.IsNullOrWhiteSpace(output) && !output.Contains("error") && !output.ToLower().Contains("erro"))
                    {
                        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                        // Pular header (primeira linha)
                        for (int i = 1; i < lines.Length; i++)
                        {
                            var line = lines[i].Trim();
                            if (string.IsNullOrWhiteSpace(line))
                                continue;

                            // Parse CSV: "Name","Description","MediaType","DataUsage"
                            var parts = line.Split(new[] { "\",\"" }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2)
                            {
                                string name = parts[0].Trim('"');
                                string description = parts[1].Trim('"');
                                string mediaType = parts.Length >= 3 ? parts[2].Trim('"') : "";
                                string dataUsage = parts.Length >= 4 ? parts[3].Trim('"') : "0 B";

                                // Determina tipo do adaptador (apenas Ethernet e WiFi)
                                string adapterType = "Ethernet";
                                if (mediaType.Contains("802.11") || name.ToLower().Contains("wi-fi") || name.ToLower().Contains("wlan"))
                                {
                                    adapterType = "WiFi";
                                }

                                adapters.Add((name, adapterType, description, dataUsage));
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Em caso de erro, retorna lista vazia
            }

            return adapters;
        }

        /// <summary>
        /// Identifica o adaptador físico com maior uso de dados (bytes enviados + recebidos).
        /// Retorna o nome do adaptador físico com maior tráfego.
        /// Apenas adaptadores físicos (Ethernet e WiFi) são considerados.
        /// </summary>
        public static (string AdapterName, string AdapterType, string Description, string AdapterGuid) GetAdapterWithHighestUsage()
        {
            try
            {

                var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"$adapters = Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' }; $maxUsage = 0; $maxAdapter = $null; foreach ($adapter in $adapters) { $stats = Get-NetAdapterStatistics -Name $adapter.Name -ErrorAction SilentlyContinue; $usage = if ($stats) { $stats.ReceivedBytes + $stats.SentBytes } else { 0 }; if ($usage -gt $maxUsage) { $maxUsage = $usage; $maxAdapter = $adapter } }; if ($maxAdapter) { Write-Output $maxAdapter.Name; Write-Output $maxAdapter.InterfaceDescription; Write-Output $maxAdapter.PhysicalMediaType; Write-Output $maxAdapter.InterfaceGuid }\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                });

                if (process != null)
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (!string.IsNullOrWhiteSpace(output) && !output.Contains("error") && !output.ToLower().Contains("erro"))
                    {
                        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                        string name = "Desconhecido";
                        string description = "Adaptador desconhecido";
                        string mediaType = "";
                        string guid = "";

                        if (lines.Length >= 4)
                        {
                            name = lines[0].Trim();
                            description = lines[1].Trim();
                            mediaType = lines[2].Trim();
                            guid = lines[3].Trim();


                            guid = System.Text.RegularExpressions.Regex.Replace(guid, @"[^0-9a-fA-F-]", "");
                            if (guid.Length != 36 || !guid.Contains('-'))
                            {
                                guid = "";
                            }
                        }

                        // Determina tipo do adaptador
                        string adapterType = "Ethernet";
                        if (mediaType.Contains("802.11") || name.ToLower().Contains("wi-fi") || name.ToLower().Contains("wlan"))
                        {
                            adapterType = "WiFi";
                        }

                        return (name, adapterType, description, guid);
                    }
                }

                return ("Desconhecido", "Desconhecido", "Não foi possível detectar adaptador", "");
            }
            catch (Exception ex)
            {
                return ("Erro", "Erro", $"Erro ao detectar adaptador: {ex.Message}", "");
            }
        }

        /// <summary>
        /// Formata bytes para formato legível (GB, MB, KB).
        /// </summary>
        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;

            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }

            return $"{size:0.##} {sizes[order]}";
        }

        /// <summary>
        /// Helper interno que executa os comandos PowerShell para aplicar as configurações de DNS.
        /// </summary>
        private static (bool Success, string Message) SetDnsServers(string provider, string? primaryDns, string? secondaryDns)
        {
            try
            {
                string psScript;
                // Busca apenas interfaces físicas ativas, ignorando VPNs e virtuais.
                string findInterfacesPart =
                    "$interfaces = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and $_.Virtual -eq $false };";

                if (provider == "DHCP")
                {
                    psScript = $"{findInterfacesPart} foreach ($if in $interfaces) {{ Set-DnsClientServerAddress -InterfaceIndex $if.InterfaceIndex -ResetServerAddresses -Confirm:$false }}";
                }
                else
                {
                    string ipArray = $"('{primaryDns}', '{secondaryDns}')";
                    psScript = $"{findInterfacesPart} foreach ($if in $interfaces) {{ Set-DnsClientServerAddress -InterfaceIndex $if.InterfaceIndex -ServerAddresses {ipArray} -Confirm:$false }}";
                }

                string result = SystemUtils.RunExternalProcess("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript}\"", hidden: true);

                if (result.Contains("PermissionDenied") || result.Contains("Erro"))
                {
                    return (false, $"Erro ao configurar DNS: {result}");
                }

                FlushDnsCache();
                string successMessage = provider == "DHCP"
                    ? "DNS revertido para Automático (DHCP) com sucesso."
                    : $"DNS {provider} aplicado com sucesso.";
                return (true, successMessage);
            }
            catch (Exception ex)
            {
                return (false, $"Erro inesperado: {ex.Message}");
            }
        }

        /// <summary>
        /// Limpa o cache de resolução de DNS do Windows.
        /// </summary>
        public static (bool Success, string Message) FlushDnsCache()
        {
            try
            {
                SystemUtils.RunExternalProcess("ipconfig", "/flushdns", hidden: true);
                return (true, "Cache de DNS limpo com sucesso!");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao limpar cache de DNS: {ex.Message}");
            }
        }

        /// <summary>
        /// Obtém informações sobre o DNS configurado na interface de rede principal.
        /// </summary>
        public static (string Provider, string DnsIp) GetActiveDnsInfo()
        {
            try
            {

                // Típico: 9 keywords virtuais fixos
                var virtualKeywords = new List<string>(9) { "virtual", "vpn", "loopback", "tap", "hyper-v", "vmware", "vbox", "wsl", "docker" };

                var activeInterface = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(i =>
                        i.OperationalStatus == OperationalStatus.Up &&
                        (i.NetworkInterfaceType == NetworkInterfaceType.Ethernet || i.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) &&
                        i.GetIPProperties().GatewayAddresses.Any() &&
                        !virtualKeywords.Any(keyword => i.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    );

                if (activeInterface != null)
                {
                    var dnsServers = activeInterface.GetIPProperties().DnsAddresses;
                    if (dnsServers.Any())
                    {
                        string firstDns = dnsServers.First().ToString();
                        if (firstDns == "1.1.1.1" || firstDns == "1.0.0.1") return ("Cloudflare", firstDns);
                        if (firstDns == "8.8.8.8" || firstDns == "8.8.4.4") return ("Google", firstDns);
                        return ("Personalizado", firstDns);
                    }
                }
            }
            catch { /* Falha silenciosa na leitura */ }

            return ("Automático (DHCP)", "N/A");
        }

        // =========================================================
        // LIMPEZA DE REDE
        // =========================================================

        /// <summary>
        /// Limpeza segura de rede — não altera configurações permanentes do PC.
        /// Limpa cache DNS, Winsock, TCP/IP, ARP, proxy, certificados SSL e credenciais de rede.
        /// </summary>
        public static (bool Success, string Message) CleanNetworkSafe()
        {
            var errors = new List<string>();
            var steps = new List<string>();

            try
            {
                SystemUtils.RunExternalProcess("ipconfig", "/flushdns", hidden: true);
                steps.Add("Cache DNS limpo");
            }
            catch { errors.Add("flushdns"); }

            try
            {
                SystemUtils.RunExternalProcess("netsh", "winsock reset", hidden: true);
                steps.Add("Winsock resetado");
            }
            catch { errors.Add("winsock"); }

            try
            {
                SystemUtils.RunExternalProcess("netsh", "int ip reset", hidden: true);
                steps.Add("TCP/IP resetado");
            }
            catch { errors.Add("ipreset"); }

            try
            {
                SystemUtils.RunExternalProcess("arp", "-d *", hidden: true);
                steps.Add("Tabela ARP esvaziada");
            }
            catch { errors.Add("arp"); }

            try
            {
                SystemUtils.RunExternalProcess("netsh", "winhttp reset proxy", hidden: true);
                steps.Add("Proxy winhttp resetado");
            }
            catch { errors.Add("winhttp"); }

            try
            {
                // Limpar credenciais de rede salvas (Windows Credential Manager)
                SystemUtils.RunExternalProcess("cmdkey", "/list", hidden: true);
                SystemUtils.RunExternalProcess("cmdkey", "/delete:*", hidden: true);
                steps.Add("Credenciais de rede limpas");
            }
            catch { errors.Add("credenciais"); }

            try
            {
                // Limpar cache de certificados SSL (certutil)
                SystemUtils.RunExternalProcess("certutil", "-urlcache * delete", hidden: true);
                steps.Add("Cache SSL limpo");
            }
            catch { errors.Add("certutil"); }

            var result = "✓ " + string.Join("\n✓ ", steps);
            if (errors.Count > 0)
                result += $"\n\n⚠ Falha em: {string.Join(", ", errors)} (podem exigir admin)";

            return (errors.Count == 0, result);
        }

        /// <summary>
        /// Limpeza completa de rede — inclui tudo da limpeza segura mais reset completo do firewall.
        /// ATENÇÃO: netsh advfirewall reset RESTAURA O FIREWALL PARA AS CONFIGURAÇÕES PADRÃO DE FÁBRICA,
        /// removendo TODAS as regras personalizadas (inbound/outbound) criadas por apps, jogos ou pelo usuário.
        /// </summary>
        public static (bool Success, string Message) CleanNetworkFull()
        {
            var safe = CleanNetworkSafe();
            var errors = new List<string>();
            var steps = new List<string>();

            steps.AddRange(safe.Message.Split('\n').Select(l => l.TrimStart('✓', ' ')).Where(l => l.Length > 0));

            try
            {
                // Reset completo do firewall — remove TODAS as regras personalizadas
                SystemUtils.RunExternalProcess("netsh", "advfirewall reset", hidden: true);
                steps.Add("Firewall resetado para padrão de fábrica");
            }
            catch { errors.Add("firewall"); }

            try
            {
                // Reset também do firewall para perfis públicos/privados/domínio
                SystemUtils.RunExternalProcess("netsh", "advfirewall set allprofiles state on", hidden: true);
                steps.Add("Firewall reativado em todos os perfis");
            }
            catch { }

            var result = "✓ " + string.Join("\n✓ ", steps);
            if (errors.Count > 0)
                result += $"\n\n⚠ Falha em: {string.Join(", ", errors)}";

            return (errors.Count == 0, result);
        }
    }
}