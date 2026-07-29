using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    public class DnsProvider
    {
        public string Name { get; set; } = "";
        public string Primary { get; set; } = "";
        public string Secondary { get; set; } = "";
        public string Category { get; set; } = "";
        public double LatencyMs { get; set; } = -1;
    }

    public static class DnsBenchmark
    {
        public static List<DnsProvider> GetDefaultProviders()
        {
            return new List<DnsProvider>
            {
                new() { Name = "Cloudflare",       Primary = "1.1.1.1",     Secondary = "1.0.0.1",     Category = "Padrão" },
                new() { Name = "Google DNS",        Primary = "8.8.8.8",     Secondary = "8.8.4.4",     Category = "Padrão" },
                new() { Name = "Quad9",             Primary = "9.9.9.9",     Secondary = "149.112.112.112", Category = "Segurança" },
                new() { Name = "OpenDNS",           Primary = "208.67.222.222", Secondary = "208.67.220.220", Category = "Segurança" },
                new() { Name = "AdGuard DNS",       Primary = "94.140.14.14",  Secondary = "94.140.15.15",  Category = "Anti-Propaganda" },
                new() { Name = "Comodo Secure",     Primary = "8.26.56.26",   Secondary = "8.20.247.20",   Category = "Segurança" },
                new() { Name = "DNS.WATCH",         Primary = "84.200.69.80",  Secondary = "84.200.70.40",  Category = "Privacidade" },
                new() { Name = "Verisign",          Primary = "64.6.64.6",    Secondary = "64.6.65.6",    Category = "Padrão" },
                new() { Name = "CleanBrowsing",     Primary = "185.228.168.9", Secondary = "185.228.169.9", Category = "Familiar" },
                new() { Name = "Neustar",           Primary = "156.154.70.1", Secondary = "156.154.71.1", Category = "Padrão" },
                new() { Name = "OpenNIC",           Primary = "185.121.177.177", Secondary = "169.239.202.202", Category = "Privacidade" },
                new() { Name = "UncensoredDNS",     Primary = "91.239.100.100", Secondary = "89.233.43.71",  Category = "Privacidade" },
                new() { Name = "Yandex DNS",        Primary = "77.88.8.8",    Secondary = "77.88.8.1",    Category = "Padrão" },
                new() { Name = "SafeDNS",           Primary = "195.46.39.39",  Secondary = "195.46.39.40",  Category = "Familiar" },
                new() { Name = "Gcore DNS",         Primary = "185.158.177.177", Secondary = "185.158.178.178", Category = "Privacidade" },
                new() { Name = "Alternate DNS",     Primary = "198.101.242.72", Secondary = "23.253.163.53", Category = "Padr\u00e3o" },
                new() { Name = "Level 3 DNS",       Primary = "209.244.0.3",  Secondary = "209.244.0.4",  Category = "Padr\u00e3o" },
                new() { Name = "Oracle Dyn",        Primary = "216.146.35.35", Secondary = "216.146.36.36", Category = "Padr\u00e3o" },
                new() { Name = "Cloudflare Familia", Primary = "1.1.1.3",   Secondary = "1.0.0.3",     Category = "Familiar" },
                new() { Name = "Cloudflare Malware", Primary = "1.1.1.2",  Secondary = "1.0.0.2",     Category = "Seguran\u00e7a" },
                new() { Name = "DNSForge",          Primary = "185.228.168.168", Secondary = "185.228.169.168", Category = "Seguran\u00e7a" },
                new() { Name = "Mullvad DNS",       Primary = "194.242.2.2",  Secondary = "194.242.2.3",  Category = "Privacidade" },
                new() { Name = "LibreDNS",          Primary = "37.235.1.174",  Secondary = "37.235.1.177", Category = "Privacidade" },
                new() { Name = "Hurricane Electric", Primary = "75.75.75.75", Secondary = "75.75.76.76", Category = "Padr\u00e3o" },
                new() { Name = "puntCAT",           Primary = "169.239.202.202", Secondary = "169.239.203.203", Category = "Privacidade" },
            };
        }

        public static async Task<List<DnsProvider>> BenchmarkAsync(List<DnsProvider> providers)
        {
            var tasks = providers.Select(async provider =>
            {
                var primaryMs = await PingDnsAsync(provider.Primary);
                var secondaryMs = await PingDnsAsync(provider.Secondary);
                provider.LatencyMs = primaryMs >= 0 ? primaryMs : secondaryMs;
                return provider;
            });

            var results = await Task.WhenAll(tasks);
            return results.OrderBy(p => p.LatencyMs >= 0 ? p.LatencyMs : double.MaxValue).ToList();
        }

        public static async Task<double> PingDnsAsync(string ip)
        {
            try
            {
                using var client = new System.Net.NetworkInformation.Ping();
                var reply = await client.SendPingAsync(IPAddress.Parse(ip), 3000);
                return reply.Status == System.Net.NetworkInformation.IPStatus.Success ? reply.RoundtripTime : -1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return -1; }
        }
    }
}
