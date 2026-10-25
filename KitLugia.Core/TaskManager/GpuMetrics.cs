using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace KitLugia.Core.TaskManager
{
    /// <summary>
    /// Métricas de GPU no estilo do Gerenciador de Tarefas: identidade do adaptador,
    /// driver, memória (dedicada/compartilhada), DirectX e temperatura.
    ///
    /// FONTES (cascata, tudo medido nesta máquina):
    ///   • Adaptadores + LUID + VRAM  → DXGI (dxgi.dll) — GpuInfo
    ///   • Driver + data + VRAM real  → registro (classe display + Enum\PCI)
    ///   • Temperatura + reserva      → nvidia-smi (NVIDIA; AMD/Intel não expõem)
    ///   • Utilização + VRAM em uso   → PDH (GpuMonitor), por LUID
    ///   • DirectX / Feature Level    → dxdiag (1x, em background) com fallback D3D11
    /// </summary>
    public static class GpuMetrics
    {
        // ────────────── nvidia-smi (temperatura/reserva/nome oficial) ──────────────
        private sealed class SmiRow
        {
            public string Name = "";
            public string DriverVersion = "";
            public ulong TotalBytes;
            public ulong UsedBytes;
            public ulong ReservedBytes;
            public double TemperatureC = -1;
            public double Utilization = -1;
        }

        private static readonly object _smiLock = new();
        private static List<SmiRow> _smiRows = new();
        private static DateTime _smiTime = DateTime.MinValue;
        private static bool _smiTried;

        /// <summary>
        /// Lê o nvidia-smi (throttle de 2s — o processo custa ~100ms).
        /// Campos estáticos (nome/driver/total) ficam em cache por 60s; o resto é fresco.
        /// </summary>
        public static IReadOnlyList<SmiRowPublic> GetNvidiaSmi()
        {
            var result = new List<SmiRowPublic>();
            try
            {
                lock (_smiLock)
                {
                    bool stale = (DateTime.UtcNow - _smiTime).TotalSeconds >= 2;
                    if (!_smiTried || stale)
                    {
                        _smiTried = true;
                        _smiTime = DateTime.UtcNow;
                        var rows = RunSmi();
                        if (rows.Count > 0) _smiRows = rows;
                    }
                    foreach (var r in _smiRows)
                        result.Add(new SmiRowPublic
                        {
                            Name = r.Name,
                            DriverVersion = r.DriverVersion,
                            TotalBytes = r.TotalBytes,
                            UsedBytes = r.UsedBytes,
                            ReservedBytes = r.ReservedBytes,
                            TemperatureC = r.TemperatureC,
                            Utilization = r.Utilization,
                        });
                }
            }
            catch { }
            return result;
        }

        /// <summary>Linha do nvidia-smi exposta publicamente (o tipo interno é privado).</summary>
        public sealed class SmiRowPublic
        {
            public string Name { get; init; } = "";
            public string DriverVersion { get; init; } = "";
            public ulong TotalBytes { get; init; }
            public ulong UsedBytes { get; init; }
            public ulong ReservedBytes { get; init; }
            public double TemperatureC { get; init; } = -1;
            public double Utilization { get; init; } = -1;
        }

        private static List<SmiRow> RunSmi()
        {
            var rows = new List<SmiRow>();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ResolveNvidiaSmiPath(),
                    Arguments = "--query-gpu=name,driver_version,memory.total,memory.used,memory.reserved,temperature.gpu,utilization.gpu --format=csv,noheader,nounits",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return rows;
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(4000);
                foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = line.Split(',').Select(x => x.Trim()).ToArray();
                    if (parts.Length < 7) continue;
                    rows.Add(new SmiRow
                    {
                        Name = parts[0],
                        DriverVersion = parts[1],
                        TotalBytes = ParseUlong(parts[2]) * 1024UL * 1024UL,
                        UsedBytes = ParseUlong(parts[3]) * 1024UL * 1024UL,
                        ReservedBytes = ParseUlong(parts[4]) * 1024UL * 1024UL,
                        TemperatureC = ParseDouble(parts[5]),
                        Utilization = ParseDouble(parts[6]),
                    });
                }
            }
            catch { }
            return rows;
        }

        private static string ResolveNvidiaSmiPath()
        {
            try
            {
                string sys = Environment.SystemDirectory;
                string p = Path.Combine(sys, "nvidia-smi.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
            return "nvidia-smi";
        }

        private static ulong ParseUlong(string s)
        {
            return ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0UL;
        }

        private static double ParseDouble(string s)
        {
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            // nvidia-smi pode devolver "[N/A]"
            return -1;
        }

        // ────────────────────────────── Adaptadores ──────────────────────────────

        /// <summary>Lista as GPUs com TODOS os dados estáticos (usada pela aba Desempenho).</summary>
        public static List<GpuAdapterInfo> GetAdapters()
        {
            var list = new List<GpuAdapterInfo>();
            try
            {
                var dxgi = GpuInfo.GetAll();
                var smi = GetNvidiaSmi();
                int idx = 0;
                foreach (var d in dxgi)
                {
                    var info = new GpuAdapterInfo
                    {
                        Name = d.Name,
                        VendorId = d.VendorId,
                        DeviceId = d.DeviceId,
                        Index = idx++,
                        LuidKey = d.LuidKey,
                        DedicatedTotalBytes = d.DedicatedVideoMemory,
                        SharedTotalBytes = d.SharedSystemMemory,
                    };

                    // Registro: driver (versão COMPLETA do Windows, ex. "32.0.16.1656"),
                    // data e VRAM (qwMemorySize). É a MESMA fonte do Gerenciador de Tarefas.
                    var reg = ReadDisplayClassInfo(d.VendorId, d.DeviceId);
                    if (!string.IsNullOrEmpty(reg.DriverVersion)) info.DriverVersion = reg.DriverVersion;
                    if (!string.IsNullOrEmpty(reg.DriverDate)) info.DriverDate = reg.DriverDate;
                    if (reg.MemorySize > 0) info.VramTotalBytes = reg.MemorySize;

                    // nvidia-smi: nome comercial oficial + driver + VRAM real + temperatura
                    var sm = smi.FirstOrDefault(s => NamesMatch(s.Name, d.Name));
                    if (sm != null)
                    {
                        if (!string.IsNullOrWhiteSpace(sm.Name)) info.Name = sm.Name;
                        // O driver do nvidia-smi ("616.56") é mais curto que o do Windows
                        // ("32.0.16.1656") — só usa quando o registro não tem nada.
                        if (info.DriverVersion == "—" && !string.IsNullOrWhiteSpace(sm.DriverVersion)) info.DriverVersion = sm.DriverVersion;
                        if (info.VramTotalBytes == 0 && sm.TotalBytes > 0) info.VramTotalBytes = sm.TotalBytes;
                        info.ReservedBytes = sm.ReservedBytes;
                        info.TemperatureC = sm.TemperatureC;
                        info.NvidiaUtilization = sm.Utilization;
                    }
                    if (info.VramTotalBytes == 0 && d.DedicatedVideoMemory > 0) info.VramTotalBytes = d.DedicatedVideoMemory;

                    string loc = ReadPciLocation(d.VendorId, d.DeviceId);
                    if (!string.IsNullOrEmpty(loc)) info.PciLocation = loc;

                    info.DirectXVersion = GetDirectXVersion();
                    list.Add(info);
                }
            }
            catch { }
            return list;
        }

        /// <summary>Compara nomes de GPU tolerando sufixos do driver ("…RTX 5070 Ti" x "…RTX 5070 Ti D").</summary>
        public static bool NamesMatch(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
            string na = Normalize(a), nb = Normalize(b);
            return na.Length > 3 && nb.Length > 3 && (na.Contains(nb) || nb.Contains(na));
        }

        private static string Normalize(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        // ────────────────────── Registro (classe display) ──────────────────────
        private sealed class DisplayClassInfo
        {
            public string DriverVersion = "";
            public string DriverDate = "";
            public ulong MemorySize;
        }

        private static DisplayClassInfo ReadDisplayClassInfo(uint vendorId, uint deviceId)
        {
            var res = new DisplayClassInfo();
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (key == null) return res;
                string want = $"ven_{vendorId:x4}&dev_{deviceId:x4}";
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var k = key.OpenSubKey(sub);
                    if (k == null) continue;
                    string match = k.GetValue("MatchingDeviceId")?.ToString() ?? "";
                    if (match.Length == 0 || !match.Contains(want, StringComparison.OrdinalIgnoreCase)) continue;
                    res.DriverVersion = k.GetValue("DriverVersion")?.ToString() ?? "";
                    res.DriverDate = FormatRegistryDate(k.GetValue("DriverDate")?.ToString() ?? "");
                    object? qw = k.GetValue("HardwareInformation.qwMemorySize");
                    object? dw = k.GetValue("HardwareInformation.MemorySize");
                    if (qw is long l && l > 0) res.MemorySize = (ulong)l;
                    else if (qw is int i2 && i2 > 0) res.MemorySize = (ulong)i2;
                    else if (dw is long l2 && l2 > 1024 * 1024) res.MemorySize = (ulong)l2;
                    else if (dw is int d1 && d1 > 1024 * 1024) res.MemorySize = (ulong)d1;
                    break;
                }
            }
            catch { }
            return res;
        }

        /// <summary>Data do driver no formato dd/MM/aaaa (o registro traz "8-20-2026" ou "M/d/yyyy").</summary>
        private static string FormatRegistryDate(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            try
            {
                if (DateTime.TryParseExact(raw, "M-d-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1))
                    return d1.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
                if (DateTime.TryParseExact(raw, "M/d/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2))
                    return d2.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
                if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d3))
                    return d3.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            }
            catch { }
            return raw;
        }

        // ────────────────────── Localização física (Enum\PCI) ──────────────────────

        /// <summary>
        /// "Barramento PCI 1, dispositivo 0, função 0" — mesmo texto do Gerenciador de Tarefas.
        /// A fonte é o LocationInformation do dispositivo ("…;(1,0,0)").
        /// </summary>
        public static string ReadPciLocation(uint vendorId, uint deviceId)
        {
            try
            {
                using var pci = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI");
                if (pci == null) return "";
                string wantDev = $"DEV_{deviceId:X4}";
                string wantVen = $"VEN_{vendorId:X4}";
                foreach (var devKey in pci.GetSubKeyNames())
                {
                    if (!devKey.Contains(wantVen, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!devKey.Contains(wantDev, StringComparison.OrdinalIgnoreCase)) continue;
                    using var dev = pci.OpenSubKey(devKey);
                    if (dev == null) continue;
                    foreach (var inst in dev.GetSubKeyNames())
                    {
                        using var k = dev.OpenSubKey(inst);
                        if (k == null) continue;
                        string loc = k.GetValue("LocationInformation")?.ToString() ?? "";
                        if (string.IsNullOrEmpty(loc)) continue;
                        // "@System32\\drivers\\pci.sys,#65536;PCI bus %1, device %2, function %3;(1,0,0)"
                        int open = loc.LastIndexOf('(');
                        int close = loc.LastIndexOf(')');
                        if (open < 0 || close <= open) continue;
                        var parts = loc.Substring(open + 1, close - open - 1).Split(',');
                        if (parts.Length < 3) continue;
                        if (!int.TryParse(parts[0].Trim(), out int bus)) continue;
                        if (!int.TryParse(parts[1].Trim(), out int devId)) continue;
                        if (!int.TryParse(parts[2].Trim(), out int func)) continue;
                        return $"Barramento PCI {bus}, dispositivo {devId}, função {func}";
                    }
                }
            }
            catch { }
            return "";
        }

        // ────────────────────── DirectX / Feature Level ──────────────────────

        private static string? _directXVersion;
        private static bool _dxDiagStarted;
        private static readonly object _dxLock = new();

        /// <summary>
        /// Versão do DirectX no formato do Gerenciador de Tarefas: "12 (FL 12.2)".
        /// Fonte primária: dxdiag ("Feature Levels: 12_2,12_1,…") — 1x, em background.
        /// Fallback: D3D11CreateDevice (dá no máximo 12.1).
        /// </summary>
        public static string GetDirectXVersion()
        {
            lock (_dxLock)
            {
                if (!string.IsNullOrEmpty(_directXVersion)) return _directXVersion;
            }
            string fallback = DetectFeatureLevelD3D11();
            lock (_dxLock)
            {
                if (!string.IsNullOrEmpty(_directXVersion)) return _directXVersion;
                _directXVersion = fallback;
                return _directXVersion;
            }
        }

        /// <summary>Dispara (uma vez) a leitura do dxdiag em background — nunca bloqueia a UI.</summary>
        public static void StartDirectXProbe()
        {
            lock (_dxLock)
            {
                if (_dxDiagStarted) return;
                _dxDiagStarted = true;
            }
            _ = Task.Run(() =>
            {
                try
                {
                    string? fl = RunDxDiagFeatureLevels();
                    if (string.IsNullOrEmpty(fl)) return;
                    lock (_dxLock) { _directXVersion = fl; }
                }
                catch { }
            });
        }

        /// <summary>Executa o dxdiag uma vez e devolve "12 (FL 12.2)" (ou null se falhar).</summary>
        private static string? RunDxDiagFeatureLevels()
        {
            string path = Path.Combine(Path.GetTempPath(), $"kl_dxdiag_{Environment.ProcessId}_{DateTime.UtcNow.Ticks % 100000}.txt");
            try
            {
                if (File.Exists(path)) File.Delete(path);
                string exe = Path.Combine(Environment.SystemDirectory, "dxdiag.exe");
                if (!File.Exists(exe)) exe = "dxdiag";
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"/whql:off /t \"{path}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                p.WaitForExit(3000);

                // O dxdiag escreve o arquivo de forma assíncrona: espera ele existir e estabilizar.
                long lastLen = -1;
                int stable = 0;
                for (int i = 0; i < 90 && stable < 3; i++)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            long len = new FileInfo(path).Length;
                            if (len > 0 && len == lastLen) stable++; else { stable = 0; lastLen = len; }
                            if (stable >= 3) break;
                        }
                    }
                    catch { }
                    System.Threading.Thread.Sleep(500);
                }
                // O dxdiag costuma ficar vivo depois de escrever o arquivo — encerra.
                try { if (!p.HasExited) p.Kill(); } catch { }
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path);
                string? levels = ExtractDxDiagValue(text, "Feature Levels:");
                if (string.IsNullOrEmpty(levels)) return null;
                string? dxVer = ExtractDxDiagValue(text, "DirectX Version:");
                string major = "12";
                if (!string.IsNullOrEmpty(dxVer))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(dxVer, @"(\d+)");
                    if (m.Success) major = m.Groups[1].Value;
                }
                string best = BestFeatureLevel(levels);
                if (string.IsNullOrEmpty(best)) return null;
                return $"{major} (FL {best.Replace('_', '.')})";
            }
            catch { return null; }
            finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        }

        private static string? ExtractDxDiagValue(string text, string key)
        {
            try
            {
                foreach (var raw in text.Split('\n'))
                {
                    // O dxdiag INDENTA as linhas ("      Feature Levels: …") — sem o Trim
                    // o StartsWith nunca casa e o DirectX ficava sempre no fallback.
                    string line = raw.Trim();
                    if (line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                        return line.Substring(key.Length).Trim();
                }
            }
            catch { }
            return null;
        }

        /// <summary>"12_2,12_1,12_0,…" → o MAIOR nível ("12_2").</summary>
        public static string BestFeatureLevel(string levelsCsv)
        {
            try
            {
                string best = "";
                int bestMajor = -1, bestMinor = -1;
                foreach (var raw in levelsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = raw.Split('_');
                    if (parts.Length == 0) continue;
                    if (!int.TryParse(parts[0], out int major)) continue;
                    int minor = 0;
                    if (parts.Length > 1) int.TryParse(parts[1], out minor);
                    if (major > bestMajor || (major == bestMajor && minor > bestMinor))
                    {
                        bestMajor = major; bestMinor = minor; best = raw;
                    }
                }
                return best;
            }
            catch { return ""; }
        }

        // ─────────────── Fallback D3D11 (feature level máximo) ───────────────
        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int D3D11CreateDevice(IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
            [In, Out] int[]? pFeatureLevels, uint FeatureLevels, uint SDKVersion,
            out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

        private static string DetectFeatureLevelD3D11()
        {
            try
            {
                // A lista de feature levels PRECISA ser explícita: com pFeatureLevels=NULL
                // o runtime devolve 11.0 (medido) em vez do máximo suportado (12.1).
                int[] levels = { 0xC100, 0xC000, 0xB100, 0xB000, 0xA100, 0xA000, 0x9300, 0x9200, 0x9100 };
                IntPtr dev = IntPtr.Zero, ctx = IntPtr.Zero;
                int fl = 0;
                int hr = D3D11CreateDevice(IntPtr.Zero, 1 /*HARDWARE*/, IntPtr.Zero, 0, levels, (uint)levels.Length, 7,
                                          out dev, out fl, out ctx);
                if (hr != 0 || fl == 0) return "—";
                if (dev != IntPtr.Zero) { try { Marshal.Release(dev); } catch { } }
                if (ctx != IntPtr.Zero) { try { Marshal.Release(ctx); } catch { } }
                int major = (fl >> 12) & 0xF;
                int minor = (fl >> 8) & 0xF;
                return $"{major} (FL {major}.{minor})";
            }
            catch { return "—"; }
        }
    }

    /// <summary>Dados ESTÁTICOS de um adaptador de GPU (identidade, driver, memória, DirectX).</summary>
    public sealed class GpuAdapterInfo
    {
        public string Name { get; set; } = "";
        public uint VendorId { get; set; }
        public uint DeviceId { get; set; }
        /// <summary>Índice do adaptador na lista da aba Desempenho.</summary>
        public int Index { get; set; }
        /// <summary>Chave LUID no formato PDH ("luid_0x00000000_0x00014AEE").</summary>
        public string LuidKey { get; set; } = "";
        /// <summary>VRAM dedicada TOTAL (bytes) — DXGI / registro / nvidia-smi.</summary>
        public ulong VramTotalBytes { get; set; }
        /// <summary>VRAM dedicada informada pelo DXGI (bytes).</summary>
        public ulong DedicatedTotalBytes { get; set; }
        /// <summary>Memória de GPU compartilhada TOTAL (bytes) — DXGI.</summary>
        public ulong SharedTotalBytes { get; set; }
        /// <summary>Memória reservada pelo hardware (bytes) — nvidia-smi.</summary>
        public ulong ReservedBytes { get; set; }
        public string DriverVersion { get; set; } = "—";
        public string DriverDate { get; set; } = "—";
        /// <summary>"Barramento PCI 1, dispositivo 0, função 0".</summary>
        public string PciLocation { get; set; } = "—";
        /// <summary>"12 (FL 12.2)".</summary>
        public string DirectXVersion { get; set; } = "—";
        /// <summary>Temperatura da GPU em °C (NVIDIA via nvidia-smi; -1 = indisponível).</summary>
        public double TemperatureC { get; set; } = -1;
        /// <summary>Utilização lida do nvidia-smi (-1 = indisponível).</summary>
        public double NvidiaUtilization { get; set; } = -1;
    }
}
