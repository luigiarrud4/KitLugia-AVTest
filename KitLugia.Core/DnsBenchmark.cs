using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    /// <summary>Estado visual de cada linha durante o teste (spinner individual estilo DNS Jumper).</summary>
    public enum DnsTestState
    {
        Pending,    // ainda não testado
        Testing,    // spinner girando
        Done,       // latência obtida
        Failed      // sem resposta (ICMP + TCP)
    }

    public class DnsProvider : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string Primary { get; set; } = "";
        public string Secondary { get; set; } = "";
        public string Category { get; set; } = "";
        public double LatencyMs { get; set; } = -1;
        /// <summary>Tempo de RESOLUÇÃO REAL (query DNS UDP porta 53, tipo A). -1 = falhou.</summary>
        private double _resolveMs = -1;
        public double ResolveMs
        {
            get => _resolveMs;
            set { if (_resolveMs != value) { _resolveMs = value; OnPropertyChanged(nameof(ResolveMs)); } }
        }
        public bool IsBestOf3 { get; set; }
        public bool IsCurrent { get; set; }
        public string MeasuredVia { get; set; } = "ICMP";

        // Estado do teste por linha (spinner individual)
        private DnsTestState _testState = DnsTestState.Pending;
        public DnsTestState TestState
        {
            get => _testState;
            set { if (_testState != value) { _testState = value; OnPropertyChanged(nameof(TestState)); OnPropertyChanged(nameof(TestStateText)); } }
        }

        public string TestStateText => TestState switch
        {
            DnsTestState.Testing => "Testando...",
            DnsTestState.Done => $"{LatencyMs:F0} ms",
            DnsTestState.Failed => "Falhou",
            _ => "-- ms"
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public string TooltipText =>
            $"Primário: {Primary}   Secundário: {Secondary}\n" +
            $"Categoria: {Category}\n" +
            (ResolveMs >= 0 ? $"Resolução DNS (query real): {ResolveMs:F0} ms\n" : "") +
            (LatencyMs >= 0
                ? $"Latência ICMP: {LatencyMs:F0} ms (melhor de 3, via {MeasuredVia})"
                : "Sem resposta no teste (ICMP e TCP)");
    }

    public static class DnsBenchmark
    {
        public static List<DnsProvider> GetDefaultProviders()
        {
            return new List<DnsProvider>
            {
                // ===== Padrão / Performance =====
                new() { Name = "Cloudflare",          Primary = "1.1.1.1",          Secondary = "1.0.0.1",          Category = "Padrão" },
                new() { Name = "Google DNS",          Primary = "8.8.8.8",          Secondary = "8.8.4.4",          Category = "Padrão" },
                new() { Name = "Control D",           Primary = "76.76.2.0",        Secondary = "76.76.10.0",       Category = "Padrão" },
                new() { Name = "Quad9",               Primary = "9.9.9.9",          Secondary = "149.112.112.112",  Category = "Segurança" },
                new() { Name = "Quad9 Sem Filtro",    Primary = "9.9.9.10",         Secondary = "149.112.112.10",   Category = "Padrão" },
                new() { Name = "OpenDNS",             Primary = "208.67.222.222",   Secondary = "208.67.220.220",   Category = "Segurança" },
                new() { Name = "Cloudflare Malware",  Primary = "1.1.1.2",          Secondary = "1.0.0.2",          Category = "Segurança" },
                new() { Name = "Cloudflare Familia",  Primary = "1.1.1.3",          Secondary = "1.0.0.3",          Category = "Familiar" },
                new() { Name = "CleanBrowsing",       Primary = "185.228.168.9",    Secondary = "185.228.169.9",    Category = "Familiar" },
                new() { Name = "SafeDNS",             Primary = "195.46.39.39",     Secondary = "195.46.39.40",     Category = "Familiar" },
                new() { Name = "AdGuard DNS",         Primary = "94.140.14.14",     Secondary = "94.140.15.15",     Category = "Anti-Propaganda" },
                new() { Name = "AdGuard Sem Filtro",  Primary = "94.140.14.140",    Secondary = "94.140.14.141",    Category = "Padrão" },
                new() { Name = "Mullvad DNS",         Primary = "194.242.2.2",      Secondary = "1.1.1.1",          Category = "Privacidade" },
                new() { Name = "NextDNS Anycast",     Primary = "45.90.28.0",       Secondary = "45.90.30.0",       Category = "Privacidade" },
                new() { Name = "DNS.SB",              Primary = "185.222.222.222",  Secondary = "45.11.45.11",      Category = "Privacidade" },
                new() { Name = "dns0.eu",             Primary = "193.110.81.0",     Secondary = "185.253.5.0",      Category = "Privacidade" },
                new() { Name = "DNS.WATCH",           Primary = "84.200.69.80",     Secondary = "84.200.70.40",     Category = "Privacidade" },
                new() { Name = "Gcore DNS",           Primary = "95.85.95.85",      Secondary = "2.56.220.2",       Category = "Privacidade" },
                new() { Name = "puntCAT",             Primary = "185.121.177.177",  Secondary = "169.239.202.202",  Category = "Privacidade" },
                new() { Name = "Level 3 DNS",         Primary = "209.244.0.3",      Secondary = "209.244.0.4",      Category = "Padrão" },
                new() { Name = "Level 3 4.2.2.1",     Primary = "4.2.2.1",          Secondary = "4.2.2.2",          Category = "Padrão" },
                new() { Name = "Hurricane Electric",  Primary = "74.82.42.42",      Secondary = "216.218.221.6",    Category = "Padrão" },
                new() { Name = "Verisign",            Primary = "64.6.64.6",        Secondary = "64.6.65.6",        Category = "Padrão" },
                new() { Name = "Dyn (Oracle)",        Primary = "216.146.35.35",    Secondary = "216.146.36.36",    Category = "Padrão" },
                new() { Name = "Quad101 (Taiwan)",    Primary = "101.101.101.101",  Secondary = "101.102.103.104",  Category = "Padrão" },
                new() { Name = "Freenom World",       Primary = "80.80.80.80",      Secondary = "80.80.81.81",      Category = "Padrão" },
            };
        }

        /// <summary>
        /// Testa todos os provedores em paralelo com SPINNER INDIVIDUAL: cada linha
        /// atualiza o próprio estado ao vivo (Testing -> Done/Failed) — estilo DNS Jumper.
        /// A lista só é reordenada no fim (a UI decide quando ordenar).
        /// </summary>
        public static async Task<List<DnsProvider>> BenchmarkAsync(List<DnsProvider> providers)
        {
            var tasks = providers.Select(async provider =>
            {
                provider.TestState = DnsTestState.Testing;

                var primary = await MeasureBestOf3Async(provider.Primary);

                if (primary.Latency >= 0)
                {
                    provider.LatencyMs = primary.Latency;
                    provider.MeasuredVia = primary.Via;
                }
                else
                {
                    var secondary = await MeasureBestOf3Async(provider.Secondary);
                    provider.LatencyMs = secondary.Latency;
                    provider.MeasuredVia = secondary.Via;
                }

                // RESOLUÇÃO REAL (o que o DNS Jumper NÃO faz no teste de lista):
                // query DNS UDP tipo A na porta 53 do servidor. Mede o tempo total
                // de processamento do resolver (não só a rede, como o ping ICMP).
                var resolve = await DnsQueryMsAsync(provider.Primary) is double rp && rp >= 0
                    ? rp
                    : await DnsQueryMsAsync(provider.Secondary);
                provider.ResolveMs = resolve;

                provider.TestState = (provider.LatencyMs >= 0 || resolve >= 0) ? DnsTestState.Done : DnsTestState.Failed;
                return provider;
            });

            var results = await Task.WhenAll(tasks);
            // RANKING pela resolução REAL quando disponível (mais fiel que ICMP);
            // fallback para latência ICMP.
            return results
                .OrderBy(p => p.ResolveMs >= 0 ? p.ResolveMs : (p.LatencyMs >= 0 ? p.LatencyMs : double.MaxValue))
                .ThenBy(p => p.Name)
                .ToList();
        }

        /// <summary>Marca qual provedor da lista está atualmente em uso na interface ativa.</summary>
        public static void MarkCurrentProvider(List<DnsProvider> providers, string currentDnsIp)
        {
            if (string.IsNullOrWhiteSpace(currentDnsIp) || currentDnsIp == "N/A") return;

            foreach (var p in providers)
            {
                p.IsCurrent =
                    p.Primary.Equals(currentDnsIp, StringComparison.OrdinalIgnoreCase) ||
                    p.Secondary.Equals(currentDnsIp, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Melhor de 3 tentativas. Tentativas 1 e 2: ICMP. Tentativa 3 (só se ambas falharem):
        /// TCP connect na porta 443 — vários servidores públicos dropam ICMP mas respondem TCP.
        /// </summary>
        private static async Task<(double Latency, string Via)> MeasureBestOf3Async(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip) || !System.Net.IPAddress.TryParse(ip, out _))
                return (-1, "ICMP");

            double best = -1;

            for (int i = 0; i < 2; i++)
            {
                var ms = await PingDnsAsync(ip);
                if (ms >= 0 && (best < 0 || ms < best)) best = ms;
                if (best >= 0 && best <= 5) break; // rota já excelente, sem desperdiçar tentativas
            }

            if (best >= 0) return (best, "ICMP");

            // Fallback TCP 443 (endpoints DoH/quic respondem TCP mesmo sem ICMP)
            var tcp = await TcpConnectMsAsync(ip, 443, 1500);
            return (tcp, tcp >= 0 ? "TCP:443" : "ICMP");
        }

        /// <summary>
        /// Query DNS REAL (UDP, porta 53, tipo A por www.google.com) com tempo medido.
        /// Retorna ms ou -1 em falha/timeout. ReceiveTimeout do socket é aplicado
        /// (o ReceiveAsync não respeita timeout — por isso o Receive é síncrono aqui).
        /// </summary>
        public static double DnsQueryMsSync(string ip, int timeoutMs = 1500)
        {
            try
            {
                if (!IPAddress.TryParse(ip, out _)) return -1;

                var query = BuildDnsQuery("www.google.com");
                using var udp = new System.Net.Sockets.UdpClient();
                udp.Connect(ip, 53);
                udp.Client.ReceiveTimeout = timeoutMs;
                udp.Client.SendTimeout = timeoutMs;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                udp.Send(query, query.Length);
                var anyEp = new IPEndPoint(IPAddress.Any, 0);
                byte[] response = udp.Receive(ref anyEp);
                sw.Stop();

                // Resposta mínima de 12 bytes = header DNS válido
                return response.Length >= 12 ? sw.Elapsed.TotalMilliseconds : -1;
            }
            catch { return -1; } // timeout (SocketException) ou recusa
        }

        private static async Task<double> DnsQueryMsAsync(string ip, int timeoutMs = 1500)
            => await Task.Run(() => DnsQueryMsSync(ip, timeoutMs));

        // Random.Shared: thread-safe (instancia propria de Random NAO e, e BuildDnsQuery roda em paralelo)
        private static readonly System.Random _queryIdRng = System.Random.Shared;

        private static byte[] BuildDnsQuery(string domain)
        {
            using var ms = new System.IO.MemoryStream();
            // Header: ID aleatório, flags RD=1, QDCOUNT=1, ARCOUNT=1 (EDNS0)
            ms.WriteByte((byte)_queryIdRng.Next(256));
            ms.WriteByte((byte)_queryIdRng.Next(256));
            ms.Write(new byte[] { 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 });
            foreach (var label in domain.Split('.'))
            {
                ms.WriteByte((byte)label.Length);
                var b = System.Text.Encoding.ASCII.GetBytes(label);
                ms.Write(b, 0, b.Length);
            }
            ms.WriteByte(0);
            ms.Write(new byte[] { 0x00, 0x01, 0x00, 0x01 }); // Type A, Class IN
            // EDNS0 OPT pseudo-record (ARCOUNT=1): NAME=0, TYPE=41, CLASS=4096 (UDP size),
            // TTL=0, RDLENGTH=0. CRÍTICO: Cloudflare/Quad9 descartam queries SEM EDNS0
            // (comportamento verificado empiricamente em 07/09 — sem OPT, só Google/OpenDNS
            // respondem; com OPT, TODOS os 26 provedores respondem).
            ms.Write(new byte[] { 0x00, 0x00, 0x29, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
            return ms.ToArray();
        }

        public static async Task<double> PingDnsAsync(string ip)
        {
            try
            {
                using var client = new System.Net.NetworkInformation.Ping();
                var reply = await client.SendPingAsync(IPAddress.Parse(ip), 2000);
                return reply.Status == System.Net.NetworkInformation.IPStatus.Success ? reply.RoundtripTime : -1;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return -1; }
        }

        private static async Task<double> TcpConnectMsAsync(string ip, int port, int timeoutMs)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var client = new System.Net.Sockets.TcpClient();
                var connectTask = client.ConnectAsync(IPAddress.Parse(ip), port);
                var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs));
                sw.Stop();
                if (completed == connectTask && client.Connected)
                    return sw.Elapsed.TotalMilliseconds;
                return -1;
            }
            catch { return -1; }
        }
    }
}
