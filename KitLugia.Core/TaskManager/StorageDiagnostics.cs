using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace KitLugia.Core.TaskManager
{
    // ══════════════════════════════════════════════════════════════════════════
    //  STORAGE DIAGNOSTICS — "meu disco está em 100%. O que exatamente está
    //  fazendo isso?"
    //
    //  Não é um monitor de "Disco 100%": o motor coleta o FATO (atividade, fila,
    //  latência), ranqueia quem está fazendo E/S (HIPÓTESE) e permite TESTAR a
    //  hipótese medindo antes/depois (CORRELAÇÃO). Só depois disso o texto
    //  afirma alguma coisa.
    //
    //  Fontes (todas validadas nesta máquina pt-BR):
    //   1. PhysicalDisk ("% Idle Time", fila, latência, R/W) — PerformanceCounter
    //      persistente. "Atividade" = 100 - idle, que é o que o Gerenciador de
    //      Tarefas do Windows mostra (100% com 18 MB/s é fila/latência, não vazão).
    //   2. MSFT_PhysicalDisk (root\Microsoft\Windows\Storage) — modelo, meio
    //      (HDD/SSD/SCM), barramento (NVMe/SATA/USB) e saúde.
    //   3. Win32_Service — qual serviço roda dentro do PID (SearchIndexer →
    //      WSearch, por exemplo), para oferecer reiniciar/parar o serviço em vez
    //      de matar o processo.
    //   4. E/S por processo vem do snapshot que o TMOG já coleta a cada 1 s
    //      (ProcessIoHelper) — este motor NÃO relê os contadores destrutivos.
    // ══════════════════════════════════════════════════════════════════════════
    public static class StorageDiagnostics
    {
        private const string StorageNs = @"root\Microsoft\Windows\Storage";

        // ────────────────────────────────────────────────────────────────────
        //  MODELOS
        // ────────────────────────────────────────────────────────────────────

        /// <summary>Identidade estática do disco físico (não muda entre leituras).</summary>
        public sealed class DiskDevice
        {
            public int Index;
            public string Model = "";
            public string Media = "";      // HDD | SSD | SCM | Desconhecido
            public string Bus = "";        // NVMe | SATA | USB | RAID | ...
            public string Letters = "";    // "C:, D:" — do nome da instância PhysicalDisk
            public string Instance = "";   // "1 C:"
            public double SizeGb;
            public string Health = "";     // do MSFT_PhysicalDisk (0 = saudável)
            public bool IsSystem;

            public string Display => $"Disco {Index}" + (Letters.Length > 0 ? $" ({Letters})" : "");
            public string Describe => string.Join(" · ", new[] { Model, Media, Bus }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        /// <summary>Amostra viva de um disco físico.</summary>
        public sealed class DiskRate
        {
            public int Index;
            public string Instance = "";
            public string Letters = "";
            public double ActivityPct;      // 100 - % Idle Time (ao vivo)
            public double ReadBps;
            public double WriteBps;
            public double Queue;            // Current Disk Queue Length
            public double LatencyMs;        // Avg. Disk sec/Transfer × 1000
            public double TransfersPerSec;
            public DiskDevice? Device;
            public bool IsSystem;
            public string Error = "";

            public bool Available => Error.Length == 0;
            public double TotalBps => ReadBps + WriteBps;
            public string Display => Device?.Display ?? ("Disco " + Index + (Letters.Length > 0 ? $" ({Letters})" : ""));
        }

        /// <summary>Conjunto de discos em um instante.</summary>
        public sealed class StorageSnapshot
        {
            public List<DiskRate> Disks = new();
            public DateTime StampUtc = DateTime.UtcNow;
            public string Error = "";

            /// <summary>O disco com maior atividade agora — é ele que o usuário está "sentindo".</summary>
            public DiskRate? Busiest =>
                Disks.Where(d => d.Available).OrderByDescending(d => d.ActivityPct).ThenByDescending(d => d.TotalBps).FirstOrDefault();

            public double TotalReadBps => Disks.Where(d => d.Available).Sum(d => d.ReadBps);
            public double TotalWriteBps => Disks.Where(d => d.Available).Sum(d => d.WriteBps);
        }

        /// <summary>E/S de um processo no tick atual (vem do snapshot do TMOG, não é relido aqui).</summary>
        public sealed class ProcIoRank
        {
            public int Pid;
            public string Name = "";
            public string Path = "";
            public double ReadBps;
            public double WriteBps;
            public double Iops;
            public ulong TotalBytes;
            public bool IsSystem;          // pid < 100 ou caminho em \Windows\
            public bool IsCritical;        // não pode ser suspenso/morto
            public string CriticalReason = "";
            public bool SuspendedByKit;
            public List<ServiceRef> Services = new();

            public double TotalBps => ReadBps + WriteBps;
        }

        /// <summary>Serviço do Windows hospedado no PID.</summary>
        public sealed class ServiceRef
        {
            public string Name = "";
            public string DisplayName = "";
            public string State = "";
            public override string ToString() => DisplayName.Length > 0 ? DisplayName : Name;
        }

        // ────────────────────────────────────────────────────────────────────
        //  IDENTIDADE DOS DISCOS (cacheada — não muda em runtime)
        // ────────────────────────────────────────────────────────────────────
        private static List<DiskDevice>? _deviceCache;
        private static DateTime _deviceCacheStamp = DateTime.MinValue;
        private static readonly object _deviceLock = new();

        private static readonly Dictionary<int, string> _busNames = new()
        {
            [0] = "Desconhecido", [1] = "SCSI", [2] = "ATAPI", [3] = "ATA", [4] = "IEEE1394",
            [5] = "SSA", [6] = "Fibre", [7] = "USB", [8] = "RAID", [9] = "iSCSI", [10] = "SAS",
            [11] = "SATA", [12] = "SD", [13] = "MMC", [14] = "Virtual", [15] = "FileBackedVirtual",
            [16] = "Spaces", [17] = "NVMe", [18] = "SCM", [19] = "UFS"
        };

        private static string MediaName(object? v)
        {
            try
            {
                int m = Convert.ToInt32(v ?? 0);
                return m switch
                {
                    3 => "HDD",
                    4 => "SSD",
                    5 => "SCM",
                    0 => "Não especificado",
                    _ => "Desconhecido"
                };
            }
            catch { return "Desconhecido"; }
        }

        private static string BusName(object? v)
        {
            try
            {
                int b = Convert.ToInt32(v ?? 0);
                return _busNames.TryGetValue(b, out var n) ? n : $"Barramento {b}";
            }
            catch { return "Desconhecido"; }
        }

        /// <summary>
        /// Modelo/meio/barramento dos discos físicos via Storage API (MSFT_PhysicalDisk).
        /// É a única fonte confiável do TIPO (Win32_DiskDrive reporta NVMe como "SCSI").
        /// </summary>
        public static List<DiskDevice> GetDevices(bool forceRefresh = false)
        {
            lock (_deviceLock)
            {
                if (!forceRefresh && _deviceCache != null && (DateTime.UtcNow - _deviceCacheStamp).TotalSeconds < 20)
                    return _deviceCache;

                var list = new List<DiskDevice>();
                try
                {
                    var rows = NativeHardware.Cim.Query(
                        "SELECT DeviceId,FriendlyName,MediaType,BusType,Size,HealthStatus FROM MSFT_PhysicalDisk",
                        StorageNs);
                    foreach (var r in rows)
                    {
                        try
                        {
                            var dev = new DiskDevice();
                            dev.Index = int.TryParse(r.TryGetValue("DeviceId", out var d) ? d?.ToString() : "0", out var di) ? di : list.Count;
                            dev.Model = (r.TryGetValue("FriendlyName", out var f) ? f?.ToString() : null) ?? "";
                            dev.Media = MediaName(r.TryGetValue("MediaType", out var mt) ? mt : null);
                            dev.Bus = BusName(r.TryGetValue("BusType", out var bt) ? bt : null);
                            try { dev.SizeGb = Convert.ToUInt64(r.TryGetValue("Size", out var sz) ? sz ?? (ulong)0 : (ulong)0) / 1073741824.0; } catch { }
                            dev.Health = HealthName(r.TryGetValue("HealthStatus", out var hs) ? hs : null);
                            list.Add(dev);
                        }
                        catch { }
                    }
                }
                catch { }

                // Fallback: Win32_DiskDrive (sem tipo real do meio, mas com modelo/tamanho)
                if (list.Count == 0)
                {
                    try
                    {
                        var rows = NativeHardware.Cim.Query("SELECT Index,Model,InterfaceType,Size FROM Win32_DiskDrive");
                        foreach (var r in rows)
                        {
                            try
                            {
                                var dev = new DiskDevice();
                                dev.Index = Convert.ToInt32(r.TryGetValue("Index", out var i) ? i ?? 0 : 0);
                                dev.Model = (r.TryGetValue("Model", out var m) ? m?.ToString() : null) ?? "";
                                dev.Bus = (r.TryGetValue("InterfaceType", out var it) ? it?.ToString() : null) ?? "";
                                dev.Media = "";
                                try { dev.SizeGb = Convert.ToUInt64(r.TryGetValue("Size", out var sz) ? sz ?? (ulong)0 : (ulong)0) / 1073741824.0; } catch { }
                                list.Add(dev);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                _deviceCache = list;
                _deviceCacheStamp = DateTime.UtcNow;
                return list;
            }
        }

        private static string HealthName(object? v)
        {
            try
            {
                int h = Convert.ToInt32(v ?? 0);
                return h switch
                {
                    0 => "Saudável",
                    1 => "Atenção",
                    2 => "Não saudável",
                    5 => "Desconhecido",
                    _ => $"Estado {h}"
                };
            }
            catch { return ""; }
        }

        // ────────────────────────────────────────────────────────────────────
        //  AMOSTRAGEM DOS DISCOS FÍSICOS (PerformanceCounter persistente)
        //
        //  Criar um PerformanceCounter por segundo é caro; aqui cada counter é
        //  criado UMA vez e reutilizado (mesmo princípio do handle PDH persistente
        //  do TMOG). Se a categoria/instância falhar, o disco é reportado como
        //  indisponível com o motivo — nunca inventamos número.
        // ────────────────────────────────────────────────────────────────────
        private sealed class CounterSet
        {
            public string Instance = "";
            public PerformanceCounter? ReadBps, WriteBps, Queue, SecPerTransfer, Transfers;
            public bool Broken;
        }

        private static readonly string _systemDrive =
            (System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\").TrimEnd('\\');

        private static readonly Dictionary<string, CounterSet> _counters = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _counterLock = new();
        private static string _categoryError = "";

        private static CounterSet? BuildCounterSet(string instance)
        {
            try
            {
                var set = new CounterSet { Instance = instance };
                set.ReadBps = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", instance, true);
                set.WriteBps = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", instance, true);
                set.Queue = new PerformanceCounter("PhysicalDisk", "Current Disk Queue Length", instance, true);
                set.SecPerTransfer = new PerformanceCounter("PhysicalDisk", "Avg. Disk sec/Transfer", instance, true);
                set.Transfers = new PerformanceCounter("PhysicalDisk", "Disk Transfers/sec", instance, true);
                // Primer: a 1ª leitura de counters de taxa devolve 0 por definição.
                foreach (var c in new[] { set.ReadBps, set.WriteBps, set.Queue, set.SecPerTransfer, set.Transfers })
                {
                    try { c!.NextValue(); } catch { }
                }
                return set;
            }
            catch
            {
                return null;
            }
        }

        private static void DisposeCounterSet(CounterSet set)
        {
            foreach (var c in new[] { set.ReadBps, set.WriteBps, set.Queue, set.SecPerTransfer, set.Transfers })
                try { c?.Dispose(); } catch { }
        }

        public static void ResetCounters()
        {
            lock (_counterLock)
            {
                foreach (var kv in _counters) DisposeCounterSet(kv.Value);
                _counters.Clear();
                _categoryError = "";
            }
        }

        /// <summary>Nomes das instâncias de disco físico ("0 D:", "1 C:", ...). Vazio = categoria indisponível.</summary>
        public static List<string> GetInstanceNames()
        {
            try
            {
                var cat = new PerformanceCounterCategory("PhysicalDisk");
                return cat.GetInstanceNames()
                    .Where(n => !n.Equals("_Total", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                _categoryError = ex.Message;
                return new List<string>();
            }
        }

        private static (int index, string letters) ParseInstance(string instance)
        {
            // "1 C:" / "0 D: E:" / "2 HarddiskVolume3"
            var m = System.Text.RegularExpressions.Regex.Match(instance, @"^\s*(\d+)\s*(.*)$");
            if (!m.Success) return (-1, "");
            int idx = int.TryParse(m.Groups[1].Value, out var i) ? i : -1;
            string letters = m.Groups[2].Value.Trim();
            return (idx, letters);
        }

        /// <summary>Lê o estado atual de todos os discos físicos (1 chamada por disco).</summary>
        public static StorageSnapshot Sample()
        {
            var snap = new StorageSnapshot();
            var devices = GetDevices();
            var names = GetInstanceNames();
            if (names.Count == 0)
            {
                snap.Error = _categoryError.Length > 0
                    ? $"Contadores de disco indisponíveis nesta máquina ({_categoryError})"
                    : "Nenhum contador de disco físico encontrado";
                return snap;
            }

            lock (_counterLock)
            {
                // Descarta counters de instâncias que sumiram (USB removido etc.)
                foreach (var gone in _counters.Keys.Where(k => !names.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
                {
                    DisposeCounterSet(_counters[gone]);
                    _counters.Remove(gone);
                }

                foreach (var name in names)
                {
                    if (!_counters.TryGetValue(name, out var set))
                    {
                        set = BuildCounterSet(name)!;
                        if (set == null) continue;
                        _counters[name] = set;
                    }

                    var (idx, letters) = ParseInstance(name);
                    var rate = new DiskRate { Instance = name, Index = idx, Letters = letters };
                    rate.Device = devices.FirstOrDefault(d => d.Index == idx);
                    // Disco do sistema: a letra onde o Windows vive aparece nas letras da instância.
                    if (_systemDrive.Length > 0 && letters.Contains(_systemDrive, StringComparison.OrdinalIgnoreCase))
                    {
                        rate.IsSystem = true;
                        if (rate.Device != null) rate.Device.IsSystem = true;
                    }

                    try
                    {
                        rate.ReadBps = Math.Max(0, set.ReadBps!.NextValue());
                        rate.WriteBps = Math.Max(0, set.WriteBps!.NextValue());
                        double q = set.Queue!.NextValue();
                        rate.Queue = double.IsNaN(q) || q < 0 ? 0 : q;
                        double st = set.SecPerTransfer!.NextValue();
                        rate.LatencyMs = double.IsNaN(st) || st < 0 || st > 30 ? 0 : st * 1000.0;
                        double tp = set.Transfers!.NextValue();
                        rate.TransfersPerSec = double.IsNaN(tp) || tp < 0 ? 0 : tp;

                        // ATIVIDADE = tempo ocupado / tempo decorrido = latência × transferências.
                        // É o MESMO cálculo do "Tempo ativo" do Gerenciador de Tarefas, mas derivado
                        // de dois contadores de taxa estáveis. Não usamos "% Idle Time": sendo
                        // PERF_100NSEC_TIMER_INV, quando o provedor de performance não atualiza o
                        // valor (disco ocioso) o delta vira 0 e a fórmula devolve 100% de atividade
                        // FALSA — exatamente o oposto do que o usuário precisa saber.
                        if (rate.TransfersPerSec <= 0)
                            rate.ActivityPct = 0;
                        else
                            rate.ActivityPct = Math.Clamp(rate.LatencyMs / 1000.0 * rate.TransfersPerSec * 100.0, 0, 100);
                    }
                    catch
                    {
                        set.Broken = true;
                        rate.Error = "leitura falhou";
                        DisposeCounterSet(set);
                        _counters.Remove(name);
                    }

                    snap.Disks.Add(rate);
                }
            }
            return snap;
        }

        /// <summary>
        /// Amostra "aquecida": descarta a primeira leitura e devolve a segunda, alguns ms depois.
        ///
        /// POR QUE ISTO EXISTE (bug real, achado no harness): contadores de TAXA do Windows
        /// precisam de DUAS leituras separadas no tempo. Quando o conjunto de counters é criado
        /// (primeira abertura da aba, ou depois de ResetCounters), a leitura imediatamente
        /// seguinte devolve TUDO ZERO — inclusive atividade, fila e latência. Com um snapshot
        /// todo zero, "o disco mais ocupado" era escolhido ao acaso: o teste de impacto travava
        /// o alvo no disco ERRADO e media um disco ocioso enquanto o outro levava 800 MB/s.
        /// Quem decide alvo (ou auto-foco) precisa desta versão, não da Sample() crua.
        /// </summary>
        public static StorageSnapshot SampleWarm(int settleMs = 450)
        {
            Sample();
            int wait = Math.Clamp(settleMs, 120, 2000);
            var until = Environment.TickCount64 + wait;
            while (Environment.TickCount64 < until) Thread.Sleep(25);
            return Sample();
        }

        // ────────────────────────────────────────────────────────────────────
        //  SERVIÇOS POR PID
        // ────────────────────────────────────────────────────────────────────
        private static Dictionary<int, List<ServiceRef>>? _servicesByPid;
        private static DateTime _servicesStamp = DateTime.MinValue;
        private static readonly object _svcLock = new();

        /// <summary>Mapa PID → serviços hospedados (svchost pode ter vários). Cacheado ~30 s.</summary>
        public static Dictionary<int, List<ServiceRef>> GetServicesByPid(bool forceRefresh = false)
        {
            lock (_svcLock)
            {
                if (!forceRefresh && _servicesByPid != null && (DateTime.UtcNow - _servicesStamp).TotalSeconds < 30)
                    return _servicesByPid;

                var map = new Dictionary<int, List<ServiceRef>>();
                try
                {
                    var rows = NativeHardware.Cim.Query(
                        "SELECT Name,DisplayName,ProcessId,State FROM Win32_Service WHERE ProcessId > 0");
                    foreach (var r in rows)
                    {
                        try
                        {
                            int pid = Convert.ToInt32(r.TryGetValue("ProcessId", out var p) ? p ?? 0 : 0);
                            if (pid <= 0) continue;
                            var svc = new ServiceRef
                            {
                                Name = (r.TryGetValue("Name", out var n) ? n?.ToString() : null) ?? "",
                                DisplayName = (r.TryGetValue("DisplayName", out var d) ? d?.ToString() : null) ?? "",
                                State = (r.TryGetValue("State", out var s) ? s?.ToString() : null) ?? ""
                            };
                            if (svc.Name.Length == 0) continue;
                            if (!map.TryGetValue(pid, out var list)) map[pid] = list = new List<ServiceRef>();
                            list.Add(svc);
                        }
                        catch { }
                    }
                }
                catch { }

                _servicesByPid = map;
                _servicesStamp = DateTime.UtcNow;
                return map;
            }
        }

        // ────────────────────────────────────────────────────────────────────
        //  SEGURANÇA — dois níveis, porque um deles é reversível e o outro não.
        //
        //  NÍVEL 1 — CRÍTICO (bloqueio rígido): suspender/encerrar quebra o Windows
        //    na hora e a recuperação exige reiniciar. O Kit NÃO deixa tentar.
        //  NÍVEL 2 — RISCO (aviso + confirmação): mexer afeta algo visível ao usuário
        //    (som, indexação, Defender, Explorer), MAS a suspensão é reversível —
        //    é justamente o caso de uso do diagnóstico (ex.: SearchIndexer). O Kit
        //    avisa ANTES e oferece ação melhor (reiniciar o serviço) quando aplicável.
        // ────────────────────────────────────────────────────────────────────
        private static readonly HashSet<string> _criticalNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Registry", "Memory Compression", "Idle", "Secure System",
            "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
            "dwm", "fontdrvhost", "audiodg", "LogonUI"
        };

        private static readonly Dictionary<string, string> _riskyNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["svchost"] = "hospeda vários serviços do Windows — suspender derruba todos de uma vez (atualizações, rede, áudio…)",
            ["SearchIndexer"] = "indexação do Windows Search — o Explorer fica mais lento e a busca para de atualizar enquanto suspenso",
            ["MsMpEng"] = "antivírus do Windows (Defender) — fica sem proteção enquanto suspenso",
            ["NisSrv"] = "inspeção de rede do Defender",
            ["explorer"] = "área de trabalho e barra de tarefas congelam enquanto suspenso",
            ["TrustedInstaller"] = "instalação de atualizações do Windows em andamento",
            ["spoolsv"] = "fila de impressão para até ser retomado",
            ["SecurityHealthService"] = "central de segurança do Windows",
            ["WmiPrvSE"] = "consultas WMI de vários programas e do próprio Kit",
        };

        /// <summary>
        /// Bloqueio rígido: suspender/encerrar este processo quebra o Windows imediatamente.
        /// </summary>
        public static bool IsCritical(int pid, string name, out string reason)
        {
            if (pid <= 4) { reason = "núcleo do sistema (PID " + pid + ")"; return true; }
            if (_criticalNames.Contains(name))
            {
                reason = name switch
                {
                    "csrss" => "subsistema crítico do Windows (tela azul se encerrado)",
                    "winlogon" => "logon do Windows (encerra a sessão)",
                    "lsass" => "autenticação do Windows (o computador reinicia)",
                    "services" => "gerenciador de serviços do Windows",
                    "dwm" => "compositor de vídeo (a tela congela)",
                    "audiodg" => "áudio do Windows (o som para)",
                    "Registry" => "registro do Windows",
                    "Memory Compression" => "compactação de memória do Windows",
                    _ => "processo do núcleo do Windows — protegido pelo próprio sistema"
                };
                return true;
            }
            reason = "";
            return false;
        }

        /// <summary>
        /// Aviso de risco (não bloqueia): mexer afeta algo que o usuário percebe, mas a
        /// suspensão é reversível. Retorna o que avisar, ou string vazia se for seguro.
        /// </summary>
        public static string RiskWarning(string name)
            => _riskyNames.TryGetValue(name, out var w) ? w : "";

        // ────────────────────────────────────────────────────────────────────
        //  IDENTIDADE DO PROCESSO — PID sozinho NÃO identifica um processo.
        //
        //  O Windows recicla PIDs: durante uma varredura de ~1 min, o candidato
        //  pode terminar e o número ser reusado por outro processo. Suspender o
        //  "novo dono" do PID seria uma intervenção em um processo que o usuário
        //  nunca escolheu. A hora de criação é a impressão digital barata e
        //  confiável do PID — validamos antes de suspender e antes de retomar.
        // ────────────────────────────────────────────────────────────────────
        public readonly record struct ProcessIdentity(int Pid, long StartUtcTicks, string Name)
        {
            public bool Known => StartUtcTicks > 0;
            public string StartedText => Known
                ? new DateTime(StartUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss")
                : "?";
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME_STRUCT { public uint Low; public uint High; }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr h, out FILETIME_STRUCT creation, out FILETIME_STRUCT exit, out FILETIME_STRUCT kernel, out FILETIME_STRUCT user);

        /// <summary>
        /// Diferença entre os dois calendários: o FILETIME conta intervalos de 100 ns desde
        /// 1601 e o DateTime desde o ano 1. Como 1601 é POSTERIOR ao ano 1, um mesmo instante
        /// tem MENOS intervalos de 100 ns no FILETIME — logo DateTime.Ticks = FILETIME + este
        /// valor (é exatamente o que DateTime.FromFileTimeUtc faz).
        /// ATENÇÃO: eu havia escrito a subtração. Com o sinal invertido o resultado ficava
        /// negativo, a identidade saía "desconhecida" e a proteção contra PID reciclado
        /// simplesmente não protegia nada (falha silenciosa, pega pelo harness).
        /// </summary>
        private const long FileTimeToTicksOffset = 504911232000000000L;

        /// <summary>Hora de criação do processo em ticks UTC — a "impressão digital" do PID.</summary>
        public static ProcessIdentity GetIdentity(int pid, string fallbackName = "")
        {
            if (pid <= 4) return new ProcessIdentity(pid, 0, fallbackName);
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return new ProcessIdentity(pid, 0, fallbackName);
                if (!GetProcessTimes(h, out var c, out _, out _, out _)) return new ProcessIdentity(pid, 0, fallbackName);
                long ft = ((long)c.High << 32) | c.Low;
                return new ProcessIdentity(pid, ft > 0 ? ft + FileTimeToTicksOffset : 0, fallbackName);
            }
            catch { return new ProcessIdentity(pid, 0, fallbackName); }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
        }

        /// <summary>
        /// O PID ainda é o MESMO processo que observamos? Se não conseguirmos ler a
        /// criação em algum dos lados (permissão), NÃO bloqueamos — travar o diagnóstico
        /// por falta de leitura seria pior que o risco residual.
        /// </summary>
        public static bool SameProcess(ProcessIdentity expected, out string reason)
        {
            reason = "";
            if (expected.Pid <= 0 || !expected.Known) return true;
            var now = GetIdentity(expected.Pid, expected.Name);
            if (!now.Known) return true;
            if (now.StartUtcTicks == expected.StartUtcTicks) return true;
            reason = $"o PID {expected.Pid} agora pertence a OUTRO processo (nasceu às {now.StartedText}, " +
                     $"o observado nasceu às {expected.StartedText}) — o original terminou e o Windows reciclou o número";
            return false;
        }

        // ────────────────────────────────────────────────────────────────────
        //  DIÁRIO DE INTERVENÇÕES — tudo que o Kit FÊZ nesta sessão, com reversão.
        //  Requisito de segurança: nenhuma ação temporária pode ficar "invisível".
        // ────────────────────────────────────────────────────────────────────
        public sealed class InterventionEntry
        {
            public DateTime At = DateTime.Now;
            public string Kind = "";      // suspender | retomar | parar servico | iniciar servico | reiniciar servico | force stop | teste de impacto | varredura
            public string Target = "";
            public int Pid;
            public string Detail = "";
            public bool Reverted;

            public string ToLine()
                => $"[{At:HH:mm:ss}] {Kind.ToUpperInvariant(),-17} {Target}{(Pid > 0 ? $" (PID {Pid})" : "")} — {Detail}" +
                   (Reverted ? "  [REVERTIDO]" : "");
        }

        private static readonly List<InterventionEntry> _journal = new();
        private static readonly object _journalLock = new();

        public static void RecordIntervention(string kind, string target, string detail, int pid = 0, bool reverted = false)
        {
            lock (_journalLock)
            {
                if (_journal.Count >= 300) _journal.RemoveAt(0);
                _journal.Add(new InterventionEntry { Kind = kind, Target = target, Detail = detail, Pid = pid, Reverted = reverted });
            }
        }

        /// <summary>Marca como revertidas as intervenções pendentes do alvo (ex.: processo retomado, serviço reiniciado).</summary>
        public static void MarkReverted(int pid = 0, string? target = null, string? kind = null)
        {
            lock (_journalLock)
            {
                foreach (var e in _journal)
                {
                    if (e.Reverted) continue;
                    if (pid > 0 && e.Pid != pid) continue;
                    if (target != null && !string.Equals(e.Target, target, StringComparison.OrdinalIgnoreCase)) continue;
                    if (kind != null && !string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase)) continue;
                    e.Reverted = true;
                }
            }
        }

        public static IReadOnlyList<InterventionEntry> Journal { get { lock (_journalLock) return _journal.ToList(); } }
        public static void ClearJournal() { lock (_journalLock) _journal.Clear(); }

        /// <summary>Quantas intervenções temporárias ainda NÃO voltaram ao estado original.</summary>
        public static int PendingReverts
        {
            get
            {
                lock (_journalLock) return _journal.Count(e => !e.Reverted);
            }
        }

        /// <summary>Marca TODAS as intervenções do diário como revertidas (usado após desfazer tudo).</summary>
        public static void MarkAllReverted()
        {
            lock (_journalLock) foreach (var e in _journal) e.Reverted = true;
            lock (_svcLock) foreach (var s in _serviceActions) s.Restored = true;
        }

        // ────────────────────────────────────────────────────────────────────
        //  SERVIÇOS — o estado anterior é registrado ANTES de mexer.
        //  Parar um serviço é mais invasivo que suspender um processo (o Windows
        //  pode tentar reiniciá-lo, e ele pode não voltar sozinho). Por isso aqui
        //  guardamos o estado em que o serviço estava para poder restaurá-lo.
        // ────────────────────────────────────────────────────────────────────
        public sealed class ServiceAction
        {
            public DateTime At = DateTime.Now;
            public string Name = "";
            public string Display = "";
            public string Action = "";      // stop | start | restart
            public string PrevState = "";   // estado ANTES da ação
            public bool Restored;

            public string ToLine()
                => $"[{At:HH:mm:ss}] SERVIÇO {Action.ToUpperInvariant(),-7} {Display} ({Name}) — estava {PrevState}" +
                   (Restored ? "  [RESTAURADO]" : "");

            /// <summary>Este serviço precisa ser levantado de volta?</summary>
            public bool NeedsRestore =>
                !Restored && Action.Equals("stop", StringComparison.OrdinalIgnoreCase) &&
                PrevState.StartsWith("Run", StringComparison.OrdinalIgnoreCase);
        }

        private static readonly List<ServiceAction> _serviceActions = new();

        public static void RecordServiceAction(string name, string display, string action, string prevState)
        {
            lock (_svcLock)
            {
                if (_serviceActions.Count >= 100) _serviceActions.RemoveAt(0);
                _serviceActions.Add(new ServiceAction { Name = name, Display = display, Action = action, PrevState = prevState });
            }
            RecordIntervention("servico " + action, display.Length > 0 ? display : name,
                               $"estado anterior: {prevState}", 0, reverted: action.StartsWith("start") || action.StartsWith("restart"));
        }

        public static IReadOnlyList<ServiceAction> ServiceActions { get { lock (_svcLock) return _serviceActions.ToList(); } }

        /// <summary>Quantos serviços o Kit parou e ainda não levantou de volta.</summary>
        public static int PendingServiceRestores
        {
            get { lock (_svcLock) return _serviceActions.Count(s => s.NeedsRestore); }
        }

        /// <summary>
        /// Levanta de volta os serviços que o Kit parou (somente os que estavam rodando antes).
        /// Nunca mexe em serviço que o usuário já havia parado por conta própria.
        /// </summary>
        public static int RestoreServices()
        {
            int n = 0;
            List<ServiceAction> pend;
            lock (_svcLock) pend = _serviceActions.Where(s => s.NeedsRestore).ToList();

            foreach (var s in pend)
            {
                try
                {
                    using var sc = new System.ServiceProcess.ServiceController(s.Name);
                    if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Running &&
                        sc.Status != System.ServiceProcess.ServiceControllerStatus.StartPending)
                    {
                        sc.Start();
                        try { sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(15)); } catch { }
                    }
                    s.Restored = true;
                    n++;
                }
                catch (Exception ex)
                {
                    RecordIntervention("servico restaurar", s.Display, "falhou: " + ex.Message);
                }
            }
            return n;
        }

        // ────────────────────────────────────────────────────────────────────
        //  SUSPENDER / RETOMAR (NtSuspendProcess — diagnóstico, reversível)
        // ────────────────────────────────────────────────────────────────────
        [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr hProcess);
        [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr hProcess);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
        /// <summary>Direito de suspender/retomar um processo (usado também pela recuperação de áudio).</summary>
        public const uint PROCESS_SUSPEND_RESUME = 0x0800;

        // Guarda a IDENTIDADE de quem NÓS suspendemos. Só retomamos o que está aqui —
        // jamais chamamos Resume em algo que o usuário já havia suspenso por conta própria.
        private static readonly Dictionary<int, ProcessIdentity> _suspendedByKit = new();
        private static readonly object _suspLock = new();

        public static bool IsSuspendedByKit(int pid)
        {
            lock (_suspLock) return _suspendedByKit.ContainsKey(pid);
        }

        public static IReadOnlyCollection<int> SuspendedByKit
        {
            get { lock (_suspLock) return _suspendedByKit.Keys.ToList(); }
        }

        /// <summary>Resultado de uma intervenção (suspender/retomar) — sempre com motivo em português.</summary>
        public sealed class Intervention
        {
            public bool Ok;
            public string Message = "";
            public int Pid;
            public bool IdentityMismatch;   // o PID foi reciclado: NADA foi tocado
        }

        /// <summary>
        /// Suspende o processo (congela, não mata). Reversível por <see cref="ResumeProcess"/>.
        /// Bloqueia processos críticos e valida a identidade do PID antes de tocar nele.
        /// </summary>
        public static Intervention SuspendProcess(int pid, string name, ProcessIdentity? expected = null)
        {
            var r = new Intervention { Pid = pid };
            if (pid <= 0) { r.Message = "PID inválido."; return r; }
            if (IsCritical(pid, name, out var why))
            {
                r.Message = $"'{name}' não pode ser suspenso: é {why}.";
                return r;
            }

            // Guarda contra PID reciclado (processo terminou e o número foi reusado).
            var id = expected ?? GetIdentity(pid, name);
            if (!SameProcess(id, out var whyNot))
            {
                r.IdentityMismatch = true;
                r.Message = $"Não suspendi nada: {whyNot}.";
                // reverted: true porque NADA foi alterado — uma recusa não é uma dívida a
                // pagar. Se entrasse como pendente, o Kit pediria para "desfazer" o nada.
                RecordIntervention("recusado", name, "PID reciclado — nada foi suspenso", pid, reverted: true);
                return r;
            }

            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
                if (h == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    r.Message = err == 5
                        ? $"Acesso negado a '{name}' (PID {pid}). Rode o Kit como administrador."
                        : $"Não foi possível abrir '{name}' (PID {pid}) — erro {err}.";
                    return r;
                }
                int rc = NtSuspendProcess(h);
                if (rc != 0)
                {
                    r.Message = $"O Windows recusou suspender '{name}' (0x{rc:X8}).";
                    return r;
                }
                lock (_suspLock) _suspendedByKit[pid] = id;
                r.Ok = true;
                r.Message = $"'{name}' (PID {pid}) SUSPENSO — o processo está congelado, não encerrado.";
                RecordIntervention("suspender", name, "processo congelado (reversível)", pid);
                return r;
            }
            catch (Exception ex)
            {
                r.Message = $"Falha ao suspender '{name}': {ex.Message}";
                return r;
            }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
        }

        /// <summary>
        /// Retoma SÓ um processo que o próprio Kit suspendeu (registro interno).
        /// Se o PID tiver sido reciclado, não tocamos em nada — apenas limpamos o registro.
        /// </summary>
        public static Intervention ResumeProcess(int pid, string name = "")
        {
            var r = new Intervention { Pid = pid };
            if (pid <= 0) { r.Message = "PID inválido."; return r; }

            ProcessIdentity known;
            lock (_suspLock)
            {
                if (!_suspendedByKit.TryGetValue(pid, out known))
                {
                    r.Message = $"'{name}' (PID {pid}) não consta como suspenso pelo Kit — nada foi retomado " +
                                "(para não mexer em processo suspenso por outro motivo).";
                    return r;
                }
            }

            if (!SameProcess(known, out var whyNot))
            {
                lock (_suspLock) _suspendedByKit.Remove(pid);
                r.IdentityMismatch = true;
                r.Message = $"Não retomei nada: {whyNot}. O registro do Kit foi limpo.";
                RecordIntervention("retomar", name, "PID reciclado — nada retomado", pid, reverted: true);
                MarkReverted(pid: pid);
                return r;
            }

            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
                if (h == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    lock (_suspLock) _suspendedByKit.Remove(pid);
                    MarkReverted(pid: pid);
                    r.Message = err == 5
                        ? $"Acesso negado a '{name}' (PID {pid})."
                        : $"Processo '{name}' (PID {pid}) não existe mais — nada a retomar.";
                    return r;
                }
                int rc = NtResumeProcess(h);
                lock (_suspLock) _suspendedByKit.Remove(pid);
                MarkReverted(pid: pid);
                if (rc != 0)
                {
                    r.Message = $"O Windows recusou retomar '{name}' (0x{rc:X8}). Tente novamente.";
                    return r;
                }
                r.Ok = true;
                r.Message = $"'{name}' (PID {pid}) RETOMADO — voltou a rodar normalmente.";
                return r;
            }
            catch (Exception ex)
            {
                lock (_suspLock) _suspendedByKit.Remove(pid);
                MarkReverted(pid: pid);
                r.Message = $"Falha ao retomar '{name}': {ex.Message}";
                return r;
            }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
        }

        /// <summary>Retoma tudo que o Kit suspendeu (chamado ao fechar a aba/janela).</summary>
        public static int ResumeAll()
        {
            int n = 0;
            foreach (var pid in SuspendedByKit)
            {
                try { if (ResumeProcess(pid).Ok) n++; } catch { }
            }
            return n;
        }

        // ────────────────────────────────────────────────────────────────────
        //  TESTE DE IMPACTO — a peça que transforma HIPÓTESE em CORRELAÇÃO
        // ────────────────────────────────────────────────────────────────────
        public sealed class ImpactTest
        {
            public bool Ok;
            public string Message = "";
            public int Pid;
            public string ProcessName = "";
            public string DiskLabel = "";

            public double ActBefore, ActAfter;
            public double QueueBefore, QueueAfter;
            public double LatBefore, LatAfter;
            public double ReadBefore, ReadAfter;
            public double WriteBefore, WriteAfter;

            public int SamplesBefore, SamplesAfter;
            public double CorrelationPct;   // queda média ponderada (0-100)
            public string Verdict = "";     // FORTE | MODERADA | FRACA | NENHUMA | INCONCLUSIVO
            public string VerdictDetail = "";
            public bool ProcessWasBusy;     // o processo estava realmente gerando E/S no baseline?
            public string Risk = "";        // aviso de risco do processo (vazio = seguro)
            public bool ResumedOk = true;
            public string ResumeMessage = "";

            // ── CORROBORAÇÃO (o veredito nunca sai de uma métrica só) ──────────
            /// <summary>Bytes de E/S que o processo acumulou ENQUANTO estava suspenso. Um processo suspenso não faz E/S: se este número subir, a suspensão não pegou o processo esperado (PID reciclado).</summary>
            public ulong ProcIoDuringTest;
            /// <summary>Confirma que o processo realmente parou de fazer E/S durante o teste.</summary>
            public bool IoFrozen;
            /// <summary>Quantos sinais independentes apontam na MESMA direção (atividade, fila, latência, E/S do processo). 0-4.</summary>
            public int Corroborations;
            /// <summary>Frase curta listando o que corroborou — vai para a tela e para o relatório.</summary>
            public string CorroborationText = "";
            /// <summary>O PID deixou de ser o mesmo processo no meio do teste — nada foi concluído.</summary>
            public bool IdentityLost;
        }

        private sealed class PhaseAvg
        {
            public int N;
            public double Act, Queue, Lat, Read, Write;
            public void Add(DiskRate d)
            {
                N++;
                Act += d.ActivityPct; Queue += d.Queue; Lat += d.LatencyMs;
                Read += d.ReadBps; Write += d.WriteBps;
            }
            public double ActAvg => N > 0 ? Act / N : 0;
            public double QueueAvg => N > 0 ? Queue / N : 0;
            public double LatAvg => N > 0 ? Lat / N : 0;
            public double ReadAvg => N > 0 ? Read / N : 0;
            public double WriteAvg => N > 0 ? Write / N : 0;
        }

        /// <summary>
        /// Mede o disco ANTES, suspende o processo, mede DEPOIS e retoma.
        /// O processo é SEMPRE retomado (finally) — inclusive em cancelamento/erro.
        /// </summary>
        public static async Task<ImpactTest> RunImpactTestAsync(
            int pid, string name, int baselineSec, int testSec,
            Func<ulong> readProcessIo, Action<string>? progress, CancellationToken ct)
        {
            var res = new ImpactTest { Pid = pid, ProcessName = name };
            baselineSec = Math.Clamp(baselineSec, 3, 20);
            testSec = Math.Clamp(testSec, 3, 30);

            // Escolhe o disco alvo AGORA (o mais ativo) e mantém ele nas duas fases —
            // comparar discos diferentes invalidaria o teste.
            // SampleWarm (e não Sample): a primeira leitura depois de criar os contadores
            // volta zerada e faria o teste escolher o disco errado como alvo.
            var first = SampleWarm();
            var target = first.Busiest;
            if (target == null)
            {
                res.Message = "Não foi possível ler os discos físicos nesta máquina — teste indisponível.";
                return res;
            }
            res.DiskLabel = target.Display;
            res.Risk = RiskWarning(name);

            if (IsCritical(pid, name, out var why))
            {
                res.Message = $"'{name}' não pode ser suspenso para teste: é {why}.";
                return res;
            }

            // Impressão digital do processo ANTES de qualquer coisa: se o PID for reciclado
            // no meio do teste (o processo termina durante uma varredura, por exemplo), o
            // Kit precisa perceber — senão mediria o disco enquanto suspende OUTRO programa.
            var identity = GetIdentity(pid, name);

            var ioBefore = readProcessIo();
            progress?.Invoke($"Medindo o estado normal por {baselineSec}s…");

            var before = new PhaseAvg();
            for (int i = 0; i < baselineSec; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(1000, ct).ConfigureAwait(false);
                var d = Sample().Disks.FirstOrDefault(x => x.Instance.Equals(target.Instance, StringComparison.OrdinalIgnoreCase));
                if (d != null && d.Available) before.Add(d);
                progress?.Invoke($"Estado normal — {i + 1}/{baselineSec}s (atividade {d?.ActivityPct ?? 0:F0}%)");
            }

            var ioAfter = readProcessIo();
            // O processo estava de fato trabalhando disk no baseline?
            res.ProcessWasBusy = ioAfter >= ioBefore && (ioAfter - ioBefore) > 64 * 1024;
            if (!res.ProcessWasBusy)
            {
                res.Message = $"'{name}' não estava realizando E/S suficiente nos últimos {baselineSec}s — " +
                              "suspender não mudaria o disco. Nada foi alterado. Escolha outro candidato ou repita com o disco ocupado.";
                res.Verdict = "INCONCLUSIVO";
                FillAverages(res, before, null);
                return res;
            }

            // Antes de suspender: o PID ainda é o processo que começamos a medir?
            if (!SameProcess(identity, out var identityWhy))
            {
                res.IdentityLost = true;
                res.Verdict = "INCONCLUSIVO";
                res.Message = $"O teste foi abortado sem tocar em nada: {identityWhy}.";
                FillAverages(res, before, null);
                RecordIntervention("teste de impacto", name, "abortado: o PID mudou de dono durante a medição", pid);
                return res;
            }

            var susp = SuspendProcess(pid, name, identity);
            if (!susp.Ok)
            {
                res.Message = susp.Message;
                FillAverages(res, before, null);
                return res;
            }

            var after = new PhaseAvg();
            try
            {
                progress?.Invoke($"'{name}' suspenso — medindo o impacto por {testSec}s…");
                ulong ioAtSuspend = readProcessIo();
                for (int i = 0; i < testSec; i++)
                {
                    await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
                    var d = Sample().Disks.FirstOrDefault(x => x.Instance.Equals(target.Instance, StringComparison.OrdinalIgnoreCase));
                    if (d != null && d.Available) after.Add(d);
                    progress?.Invoke($"Com '{name}' suspenso — {i + 1}/{testSec}s (atividade {d?.ActivityPct ?? 0:F0}%)");
                }
                // Prova de que a suspensão pegou o processo certo: suspenso, ele não pode
                // fazer mais E/S. Se os contadores continuaram subindo, o PID mudou de dono.
                ulong ioAfterSuspend = readProcessIo();
                res.ProcIoDuringTest = ioAfterSuspend > ioAtSuspend ? ioAfterSuspend - ioAtSuspend : 0;
                // Critério por TAXA, não por bytes absolutos: E/S já despachada antes da
                // suspensão ainda completa no kernel e aparece nos contadores do processo
                // (um bloco de 4 MB em voo é normal). O que não pode acontecer é o processo
                // continuar transferindo no MESMO ritmo de antes — isso seria impossível
                // para algo suspenso e significaria que o PID mudou de dono.
                double baseRate = (ioAfter - ioBefore) / (double)Math.Max(1, baselineSec);
                double suspRate = res.ProcIoDuringTest / (double)Math.Max(1, testSec);
                res.IoFrozen = suspRate <= Math.Max(baseRate * 0.25, 128 * 1024);
            }
            catch { /* retoma no finally, sempre */ }
            finally
            {
                var rr = ResumeProcess(pid, name);
                res.ResumedOk = rr.Ok;
                res.ResumeMessage = rr.Message;
            }

            if (before.N == 0 || after.N == 0)
            {
                res.Message = "Amostras insuficientes para concluir (o disco mudou de instância durante o teste).";
                res.Verdict = "INCONCLUSIVO";
                FillAverages(res, before, after);
                return res;
            }

            FillAverages(res, before, after);
            res.Ok = true;

            // Correlação: quanto a atividade/fila/leitura/escrita CAÍRAM. Exigimos que a
            // atividade já estivesse relevante antes, senão "caiu de 2% para 1%" não prova nada.
            // Quatro métricas independentes, peso maior no que o usuário SENTE (atividade e fila).
            double actDrop = before.ActAvg > 1 ? (before.ActAvg - after.ActAvg) / before.ActAvg * 100 : 0;
            double queueDrop = before.QueueAvg > 0.2 ? (before.QueueAvg - after.QueueAvg) / before.QueueAvg * 100 : 0;
            double latDrop = before.LatAvg > 0.5 ? (before.LatAvg - after.LatAvg) / before.LatAvg * 100 : 0;
            double wrDrop = before.ReadAvg + before.WriteAvg > 64 * 1024
                ? ((before.ReadAvg + before.WriteAvg) - (after.ReadAvg + after.WriteAvg)) / (before.ReadAvg + before.WriteAvg) * 100
                : 0;
            res.CorrelationPct = Math.Round(Math.Clamp(
                (actDrop * 0.40) + (queueDrop * 0.25) + (latDrop * 0.20) + (wrDrop * 0.15), -100, 100), 1);

            // ── CORROBORAÇÃO: quantos sinais independentes caíram juntos? ─────────
            // Um número só nunca decide: "100% -> 80%" não prova nada, mas
            // "atividade 100%->8%, fila 15->0,7, latência 40->3 ms, E/S do processo ~0"
            // é uma evidência muito mais forte. Cada métrica vale 1 ponto.
            int corrob = 0;
            var corrobList = new List<string>();
            if (actDrop >= 40) { corrob++; corrobList.Add($"atividade {before.ActAvg:F0}% -> {after.ActAvg:F0}%"); }
            if (queueDrop >= 30) { corrob++; corrobList.Add($"fila {before.QueueAvg:F1} -> {after.QueueAvg:F1}"); }
            if (latDrop >= 30) { corrob++; corrobList.Add($"latência {before.LatAvg:F1} -> {after.LatAvg:F1} ms"); }
            if (wrDrop >= 30) { corrob++; corrobList.Add($"transferência {FormatBps(before.ReadAvg + before.WriteAvg)} -> {FormatBps(after.ReadAvg + after.WriteAvg)}"); }
            if (res.IoFrozen) { corrobList.Add("a E/S do próprio processo congelou (suspensão confirmada)"); }
            res.Corroborations = corrob;
            res.CorroborationText = corrobList.Count > 0 ? string.Join("; ", corrobList) : "";

            bool wasSaturated = before.ActAvg >= 25 || before.QueueAvg >= 2 || before.LatAvg >= 15;
            if (!res.IoFrozen)
            {
                // A suspensão não parou a E/S do processo: medimos o disco enquanto outro
                // processo ocupava aquele PID. Sem isso o veredito acusaria o programa errado.
                res.IdentityLost = true;
                res.Verdict = "INCONCLUSIVO";
                res.VerdictDetail = "O processo continuou fazendo E/S depois de suspenso, o que é impossível: " +
                                    "o PID provavelmente mudou de dono durante o teste. A medição é inválida — " +
                                    "repita o teste.";
            }
            else if (!wasSaturated)
            {
                res.Verdict = "INCONCLUSIVO";
                res.VerdictDetail = "O disco já não estava saturado antes do teste — não havia o que aliviar. " +
                                    "Repita enquanto o problema estiver acontecendo.";
            }
            else if (actDrop >= 50 && corrob >= 3)
            {
                res.Verdict = "FORTE";
                res.VerdictDetail = $"Vários sinais independentes caíram juntos com a suspensão ({corrob} de 4 métricas) — " +
                                    "há correlação forte com o que você estava sentindo.";
            }
            else if (actDrop >= 30)
            {
                res.Verdict = "MODERADA";
                res.VerdictDetail = corrob < 3
                    ? $"A atividade caiu, mas só {corrob} de 4 métricas acompanharam a queda — o processo pesa, " +
                      "mas a evidência não sustenta afirmar que ele é a causa sozinho."
                    : "A atividade caiu com a suspensão, mas o processo não explica tudo — " +
                      "provavelmente existe mais de um responsável.";
            }
            else if (actDrop >= 12)
            {
                res.Verdict = "FRACA";
                res.VerdictDetail = "Houve alguma queda, mas pequena demais para afirmar que este processo é a causa.";
            }
            else
            {
                res.Verdict = "NENHUMA";
                res.VerdictDetail = "Suspender o processo NÃO aliviou o disco. Ele não é a causa da saturação — " +
                                    "procure outro candidato (ou investigue o driver/dispositivo).";
            }
            if (!res.ResumedOk)
                res.VerdictDetail += "  ATENÇÃO: não foi possível retomar o processo — verifique e retome manualmente.";

            // O diário é a memória do Kit: toda intervenção temporária fica registrada para
            // poder ser revista e revertida (requisito de segurança da revisão).
            RecordIntervention("teste de impacto", name,
                $"veredito {res.Verdict} (correlação {res.CorrelationPct:F0}%, {res.Corroborations}/4 métricas, " +
                $"E/S do processo congelou: {(res.IoFrozen ? "sim" : "não")})", pid, reverted: true);
            return res;
        }

        /// <summary>Formata taxa em B/s para o texto do diagnóstico (usada também pela GUI).</summary>
        public static string FormatBps(double bps)
            => bps >= 1024 * 1024 * 1024 ? $"{bps / 1024 / 1024 / 1024:F1} GB/s"
             : bps >= 1024 * 1024 ? $"{bps / 1024 / 1024:F1} MB/s"
             : bps >= 1024 ? $"{bps / 1024:F0} KB/s"
             : $"{bps:F0} B/s";

        private static void FillAverages(ImpactTest res, PhaseAvg before, PhaseAvg? after)
        {
            res.SamplesBefore = before.N;
            res.ActBefore = Math.Round(before.ActAvg, 1);
            res.QueueBefore = Math.Round(before.QueueAvg, 2);
            res.LatBefore = Math.Round(before.LatAvg, 1);
            res.ReadBefore = before.ReadAvg;
            res.WriteBefore = before.WriteAvg;
            if (after == null) return;
            res.SamplesAfter = after.N;
            res.ActAfter = Math.Round(after.ActAvg, 1);
            res.QueueAfter = Math.Round(after.QueueAvg, 2);
            res.LatAfter = Math.Round(after.LatAvg, 1);
            res.ReadAfter = after.ReadAvg;
            res.WriteAfter = after.WriteAvg;
        }

        // ────────────────────────────────────────────────────────────────────
        //  INVESTIGAR — o dossiê completo do processo
        // ────────────────────────────────────────────────────────────────────
        public sealed class ProcessDiagnostics
        {
            public int Pid;
            public string Name = "";
            public string Path = "";
            public string Company = "";
            public string Description = "";
            public string Version = "";
            public int ParentPid;
            public string ParentName = "";
            public double CpuPct;
            public double WorkingSetMb;
            public double PrivateMb;
            public double ReadBps, WriteBps;
            public ulong ReadTotalBytes, WriteTotalBytes, OpsTotal;
            public int Threads, Handles;
            public string UserName = "";
            public string StartedAt = "";
            public string Uptime = "";
            public bool IsSystem, IsCritical, IsElevated, Responding, Suspended;
            public string CriticalReason = "";
            public string DiskLetter = "";      // unidade onde o executável está
            public string DiskLabel = "";       // "Disco 1 (C:)" — disco físico daquela unidade
            public string DiskMedia = "";
            public List<ServiceRef> Services = new();
            public int ModuleCount;
            public string Error = "";
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, uint len, out uint ret);
        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenElevation = 20;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private static bool IsElevatedProcess(int pid)
        {
            IntPtr hProc = IntPtr.Zero, hTok = IntPtr.Zero, buf = IntPtr.Zero;
            try
            {
                hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hProc == IntPtr.Zero) return false;
                if (!OpenProcessToken(hProc, TOKEN_QUERY, out hTok) || hTok == IntPtr.Zero) return false;
                buf = Marshal.AllocHGlobal(4);
                if (!GetTokenInformation(hTok, TokenElevation, buf, 4, out _)) return false;
                return Marshal.ReadInt32(buf) != 0;
            }
            catch { return false; }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                if (hTok != IntPtr.Zero) CloseHandle(hTok);
                if (hProc != IntPtr.Zero) CloseHandle(hProc);
            }
        }

        /// <summary>
        /// Junta tudo que o Kit sabe sobre o processo: identidade, privilégios,
        /// recurso, E/S, serviço hospedado e em qual disco físico ele está.
        /// Tudo que não puder ser lido com confiança fica vazio (a UI mostra "—"),
        /// nunca preenchido com valor inventado.
        /// </summary>
        public static ProcessDiagnostics Investigate(
            int pid, string name, string path,
            double cpuPct, double workingSetMb, double privateMb,
            double readBps, double writeBps, ulong readTotal, ulong writeTotal, ulong opsTotal,
            int threads, int handles, string userName)
        {
            var d = new ProcessDiagnostics
            {
                Pid = pid, Name = name, Path = path,
                CpuPct = cpuPct, WorkingSetMb = workingSetMb, PrivateMb = privateMb,
                ReadBps = readBps, WriteBps = writeBps,
                ReadTotalBytes = readTotal, WriteTotalBytes = writeTotal, OpsTotal = opsTotal,
                Threads = threads, Handles = handles, UserName = userName
            };

            try
            {
                d.IsSystem = pid < 100 || (!string.IsNullOrEmpty(path) &&
                    path.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase));
                d.IsCritical = IsCritical(pid, name, out var why);
                d.CriticalReason = why;
                d.IsElevated = IsElevatedProcess(pid);
                d.Suspended = IsSuspendedByKit(pid);

                if (path.Length > 0)
                {
                    try
                    {
                        var fvi = FileVersionInfo.GetVersionInfo(path);
                        d.Company = fvi.CompanyName ?? "";
                        d.Description = fvi.FileDescription ?? "";
                        d.Version = fvi.FileVersion ?? "";
                    }
                    catch { }
                    try
                    {
                        var root = System.IO.Path.GetPathRoot(path) ?? "";
                        d.DiskLetter = root.TrimEnd('\\');
                    }
                    catch { }
                }

                // Serviço(s) hospedados neste PID
                if (GetServicesByPid().TryGetValue(pid, out var svcs)) d.Services = svcs;

                // Processo pai (nome, se ainda vivo)
                try
                {
                    var nm = NativeMetricsHelper.EnumerateProcesses();
                    var me = nm?.FirstOrDefault(p => p.Pid == pid);
                    if (me != null)
                    {
                        d.ParentPid = me.ParentPid;
                        var par = nm!.FirstOrDefault(p => p.Pid == me.ParentPid);
                        d.ParentName = par?.Name ?? "";
                        if (me.ThreadCount > 0) d.Threads = (int)me.ThreadCount;
                        if (me.HandleCount > 0) d.Handles = (int)me.HandleCount;
                    }
                }
                catch { }

                // Tempo de atividade (via Process — falha em processos protegidos, tudo bem)
                try
                {
                    using var p = Process.GetProcessById(pid);
                    var st = p.StartTime;
                    d.StartedAt = st.ToString("dd/MM/yyyy HH:mm:ss");
                    var up = DateTime.Now - st;
                    d.Uptime = $"{(int)up.TotalDays}d {up.Hours:00}h {up.Minutes:00}m {up.Seconds:00}s";
                    d.Responding = p.Responding;
                    try { d.ModuleCount = p.Modules.Count; } catch { }
                }
                catch { }

                // Disco físico onde o executável vive (cruzando a unidade com a instância do disco)
                var snap = Sample();
                if (d.DiskLetter.Length > 0 && snap.Disks.Count > 0)
                {
                    var disk = snap.Disks.FirstOrDefault(x =>
                        x.Letters.Contains(d.DiskLetter, StringComparison.OrdinalIgnoreCase));
                    if (disk != null)
                    {
                        d.DiskLabel = disk.Display;
                        d.DiskMedia = disk.Device?.Media ?? "";
                    }
                }
            }
            catch (Exception ex) { d.Error = ex.Message; }
            return d;
        }
    }
}
