using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace KitLugia.Core
{
    /// <summary>
    /// Avaliador de saúde de disco estilo Hard Disk Sentinel — algoritmo extraído por
    /// engenharia reversa do HDSentinel.exe 6.20 (sub_60479C / sub_600A60, ver
    /// Downloads/hds_re/HDS_HEALTH_RE.md).
    ///
    /// Modelo (método padrão "Analyse data field" do HDS):
    ///   1. Saúde começa em 100%.
    ///   2. Cada atributo SMART crítico com problema multiplica a saúde por
    ///      (100 − limite×peso)/100 (tabela extraída do binário).
    ///   3. Pending sectors têm curva própria de penalidade (rampa 11→99, severa ≥100).
    ///   4. Atributos de desgaste SSD (169/173/177/180/201/202/230/231/233/245)
    ///      definem a saúde diretamente e funcionam como TETO rígido.
    ///   5. NVMe: saúde = 100 − Wear (MSFT_StorageReliabilityCounter), igual ao
    ///      comportamento do HDS com "Percentage Used".
    ///
    /// Requer elevação (o Kit sempre roda como admin — manifest requireAdministrator).
    /// </summary>
    public static class DiskHealthEvaluator
    {
        public sealed class SmartAttr
        {
            public byte Id;
            public ushort Flags;
            public byte Value;
            public byte Worst;
            public byte Threshold;
            public ulong Raw;
        }

        public sealed class Result
        {
            public int Percent = 100;
            public bool Determined;                 // false = sem dados suficientes
            public string Source = "";              // "SMART (HDS)" | "Wear NVMe/SSD" | ...
            public List<string> Problems = new();   // descrições pt-BR dos problemas
            public double? TemperatureC;
            public string Detail => Problems.Count == 0 ? "" : string.Join("; ", Problems);
        }

        // ===== Tabela REada do HDSentinel (sub_600A60: fator = (100 − limite×peso)/100) =====
        // (id, nome, limite%, peso, gatilho: raw > 0)
        private static readonly (byte Id, string Nome, int Limite, double Peso)[] _tab =
        {
            (5,   "setores realocados",        70, 1.0),
            (10,  "tentativas de spin-up",     66, 4.0),   // qualquer ocorrência zera
            (7,   "erros de busca (seek)",     20, 1.0),
            (168, "erros marvell/ondas",       30, 1.5),
            (175, "erros de programa",         40, 2.0),
            (183, "erros end-to-end",          80, 1.0),
            (184, "erros end-to-end (var)",    50, 2.0),
            (187, "setores não corrigíveis",   40, 2.0),
            (196, "eventos de realocação",     36, 4.0),
            (198, "setores offline não corr.", 72, 1.0),
            (203, "erros ECC de run",          85, 0.004),
        };

        // Atributos de desgaste SSD que DEFINEM a saúde (valor normalizado = saúde)
        private static readonly byte[] _ssdWearIds = { 169, 173, 177, 180, 201, 230, 231, 233, 245 };

        private static readonly HashSet<byte> _pendingIds = new() { 197 }; // Current Pending Sectors

        // =====================================================================
        // Ponto de entrada
        // =====================================================================

        /// <summary>Avalia a saúde de um disco. Nunca lança; em falha retorna Determined=false.</summary>
        public static Result Evaluate(uint diskIndex, string model, string mediaType, string busType)
        {
            var r = new Result();
            try
            {
                bool isSsd = string.Equals(mediaType, "SSD", StringComparison.OrdinalIgnoreCase);
                bool isNvme = string.Equals(busType, "NVMe", StringComparison.OrdinalIgnoreCase);

                // 1) Tabela SMART via WMI (rápida) — funciona para HDD e SSD SATA.
                //    Os atributos de desgaste SSD (169/177/202/231/233...) dentro da
                //    tabela definem a saúde, igual ao HDS.
                var attrs = ReadSmartAttributes(model);
                if (attrs != null && attrs.Count > 0)
                {
                    r.Source = "SMART (modelo HDS)";
                    EvaluateAta(attrs, r, isSsd);
                    r.Determined = true;
                    return r;
                }

                // 2) NVMe: Health Log nativo (Log Page 0x02 via IOCTL) — o mesmo
                //    caminho do HDSentinel. "Percentage Used" exato, sem arredondamento.
                var nv = ReadNvmeHealthLog(diskIndex);
                if (nv != null)
                {
                    r.Source = "NVMe Health Log (modelo HDS)";
                    r.Percent = (int)Math.Clamp(100 - nv.PercentUsed, 0, 100);
                    r.TemperatureC = nv.TemperatureC;
                    r.Determined = true;
                    if (nv.PercentUsed > 0)
                        r.Problems.Add($"desgaste de memória flash: {nv.PercentUsed}% usado");
                    if (nv.AvailSpare > 0 && nv.SpareThreshold > 0 && nv.AvailSpare <= nv.SpareThreshold)
                        r.Problems.Add($"blocos de reserva no limite ({nv.AvailSpare}% ≤ {nv.SpareThreshold}%)");
                    if (nv.MediaErrors > 0)
                        r.Problems.Add($"{nv.MediaErrors} erros de mídia registrados");
                    if (nv.TemperatureC >= 57)
                        r.Problems.Add($"temperatura alta ({nv.TemperatureC} °C — limiar vermelho do HDS)");
                    return r;
                }

                // 3) Último recurso: Wear via StorageReliabilityCounter (PowerShell),
                //    para NVMe atrás de pontes que não passam o IOCTL.
                if (isSsd || isNvme || string.IsNullOrWhiteSpace(mediaType))
                {
                    var (wear, temp) = GetReliabilityCounter(diskIndex);
                    if (temp.HasValue) r.TemperatureC = temp;
                    if (wear.HasValue && wear.Value >= 0)
                    {
                        r.Determined = true;
                        r.Source = "Desgaste SSD (Storage Reliability Counter)";
                        r.Percent = (int)Math.Clamp(100 - wear.Value, 0, 100);
                        if (wear.Value > 0)
                            r.Problems.Add($"desgaste de memória flash: {wear.Value}% usado");
                        if (temp.HasValue && temp.Value >= 57)
                            r.Problems.Add($"temperatura alta ({temp.Value} °C — limiar vermelho do HDS)");
                    }
                }
                // 3) Sem dados — Determined=false, UI mostra o veredito do Windows
            }
            catch (Exception ex)
            {
                Logger.Log($"DiskHealthEvaluator: falha avaliando disco {diskIndex}: {ex.Message}");
                r.Determined = false;
            }
            return r;
        }

        // =====================================================================
        // Núcleo ATA — replica sub_60479C (método padrão)
        // =====================================================================
        private static void EvaluateAta(List<SmartAttr> attrs, Result r, bool isSsd)
        {
            double health = 100.0;
            ulong pending = 0;
            int? ssdHealth = null;
            var byId = attrs.ToDictionary(a => a.Id);

            foreach (var a in attrs)
            {
                bool problem = a.Raw > 0; // gatilho "atributo em estado ruim" (sub_617B08 simplificado)

                // atributos de desgaste SSD: valor normalizado vira saúde (teto rígido)
                if (_ssdWearIds.Contains(a.Id) && a.Value > 0 && a.Value <= 100)
                {
                    int v = a.Value;
                    // caso especial do HDS: 99 com disco novo (< 720 h) conta como 100
                    if (v == 99 && byId.TryGetValue(9, out var poh) && poh.Raw < 720)
                        v = 100;
                    if (ssdHealth == null || v < ssdHealth) ssdHealth = v;
                    continue;
                }

                if (_pendingIds.Contains(a.Id))
                {
                    pending += a.Raw;
                    continue;
                }

                if (!problem) continue;

                var hit = _tab.FirstOrDefault(t => t.Id == a.Id);
                if (hit.Id != 0)
                {
                    double degr = hit.Limite * hit.Peso;
                    if (degr > 100) degr = 100;
                    health *= (100.0 - degr) / 100.0;
                    r.Problems.Add($"{hit.Nome}: raw = {a.Raw}");
                }
                else if (a.Id == 197)
                {
                    // tratado acima via pending
                }
            }

            // curva de penalidade por pending sectors (RE: rampa 11→99, severa ≥100)
            if (pending > 0)
            {
                r.Problems.Add($"setores pendentes (pending sectors): {pending}");
                if (pending >= 300) { if (health > 9) health = 9; }
                else if (pending >= 100) health *= 0.25;
                else if (pending >= 11) health *= 0.5 * ((double)pending - 10) / 90.0;
                // 1..10 pendings: sem penalidade numérica (HDS só alerta)
            }

            if (ssdHealth.HasValue && health > ssdHealth.Value)
            {
                health = ssdHealth.Value;
                r.Problems.Add($"desgaste SSD reportado pelo controlador: {ssdHealth.Value}%");
            }

            health = Math.Clamp(health, 0, 100);
            r.Percent = (int)Math.Round(health);
        }

        // =====================================================================
        // Coleta
        // =====================================================================

        /// <summary>
        /// Lê o NVMe Health/SMART Log (Log Page 0x02) nativamente via
        /// IOCTL_STORAGE_PROTOCOL_COMMAND — o mesmo caminho do HDSentinel
        /// (o dump cru aparece no .hdi a partir de 0x40D0, ver HDS_HEALTH_RE.md).
        /// Requer elevação. Layout do log: temp composta @1 (Kelvin), spare
        /// threshold @3, spare disponível @4, percentage used @5, data units @32.
        /// </summary>
        private sealed class NvmeHealth
        {
            public int TemperatureC;
            public int PercentUsed;
            public int AvailSpare;
            public int SpareThreshold;
            public ulong DataUnitsRead;
            public ulong DataUnitsWritten;
            public ulong PowerOnHours;
            public ulong MediaErrors;
        }

        // IOCTL_STORAGE_QUERY_PROPERTY = CTL_CODE(IOCTL_STORAGE_BASE, 0x0500, METHOD_BUFFERED, FILE_ANY_ACCESS)
        private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
        private const uint STORAGE_PROTOCOL_TYPE_NVME = 3;      // ProtocolTypeNvme
        private const uint NVME_DATA_TYPE_LOG_PAGE = 2;         // NVMeDataTypeLogPage
        private const uint NVME_LOG_PAGE_HEALTH = 0x02;
        private const uint STORAGE_DEVICE_PROTOCOL_SPECIFIC = 50; // PropertyId
        private const int NVME_QUERY_HDR = 48;   // STORAGE_PROPERTY_QUERY(8) + STORAGE_PROTOCOL_SPECIFIC_DATA(40)
        private const int NVME_QUERY_TOTAL = NVME_QUERY_HDR + 512;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
            string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code,
            byte[] inBuf, uint inSize,
            byte[] outBuf, uint outSize, out uint bytesReturned, IntPtr overlapped);

        private static NvmeHealth ReadNvmeHealthLog(uint diskIndex)
        {
            const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
            const uint OPEN_EXISTING = 3;

            try
            {
                // acesso 0 (QUERY_ATTRIBUTES): suficiente para query property e funciona
                // até sem elevação (o Kit roda admin de qualquer forma)
                using var h = CreateFileW($"\\\\.\\PhysicalDrive{diskIndex}", 0,
                    FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) return null;

                var buf = new byte[NVME_QUERY_TOTAL];
                PutU32(buf, 0, STORAGE_DEVICE_PROTOCOL_SPECIFIC); // PropertyId
                PutU32(buf, 4, 0);                                // QueryType = PropertyStandardQuery
                PutU32(buf, 8, STORAGE_PROTOCOL_TYPE_NVME);       // ProtocolType
                PutU32(buf, 12, NVME_DATA_TYPE_LOG_PAGE);         // DataType
                PutU32(buf, 16, NVME_LOG_PAGE_HEALTH);            // ProtocolDataRequestValue = Health log
                PutU32(buf, 20, 0);                               // SubValue = NSID
                PutU32(buf, 24, 40);                              // ProtocolDataOffset (rel. a AdditionalParameters)
                PutU32(buf, 28, 512);                             // ProtocolDataLength

                if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, buf, (uint)buf.Length,
                    buf, (uint)buf.Length, out uint ret, IntPtr.Zero) || ret < NVME_QUERY_HDR + 16)
                    return null;

                var log = buf.AsSpan(NVME_QUERY_HDR, 512).ToArray();
                var n = new NvmeHealth
                {
                    TemperatureC = (log[1] | (log[2] << 8)) - 273,
                    // NVMe spec 2.4 Fig. 213: byte 3 = Available Spare, byte 4 = Threshold
                    AvailSpare = log[3],
                    SpareThreshold = log[4],
                    PercentUsed = log[5],
                    DataUnitsRead = ReadU64(log, 32),
                    DataUnitsWritten = ReadU64(log, 48),
                    PowerOnHours = ReadU64(log, 128),
                    MediaErrors = ReadU64(log, 160)
                };
                // sanity: %used plausível e log não vazio
                if (n.PercentUsed > 100) return null;
                if (n.DataUnitsRead == 0 && n.DataUnitsWritten == 0 && n.PowerOnHours == 0) return null;
                return n;
            }
            catch { return null; }
        }

        private static ulong ReadU64(byte[] b, int off) =>
            (ulong)b[off] | ((ulong)b[off + 1] << 8) | ((ulong)b[off + 2] << 16) | ((ulong)b[off + 3] << 24)
            | ((ulong)b[off + 4] << 32) | ((ulong)b[off + 5] << 40) | ((ulong)b[off + 6] << 48) | ((ulong)b[off + 7] << 56);

        private static void PutU32(byte[] b, int off, uint v)
        {
            b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24);
        }

        /// <summary>Wear (%) e temperatura via MSFT_StorageReliabilityCounter (PowerShell).</summary>
        private static (int? wear, int? temp) GetReliabilityCounter(uint diskIndex)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -Command \"(Get-PhysicalDisk -DeviceNumber {diskIndex} | Get-StorageReliabilityCounter) | Select-Object Wear,Temperature | ConvertTo-Json -Compress\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    // stderr nao era lido: se o PowerShell enchia esse pipe, o
                    // ReadToEnd() sincrono de stdout travava e o timeout de 15s
                    // nunca era alcancado. Sem redirecionamento, nao ha pipe.
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return (null, null);

                // Drena stdout em background ENQUANTO o timeout corre.
                var stdoutTask = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(15000))
                {
                    try { p.Kill(); } catch { }
                    _ = stdoutTask.ContinueWith(t => { _ = t.Exception; },
                        System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
                    return (null, null);
                }
                string outp;
                try { outp = stdoutTask.GetAwaiter().GetResult(); }
                catch { return (null, null); }
                outp = outp?.Trim();
                if (string.IsNullOrEmpty(outp) || outp == "null") return (null, null);

                int? wear = null, temp = null;
                var mW = Regex.Match(outp, "\"Wear\"\\s*:\\s*(\\d+)");
                var mT = Regex.Match(outp, "\"Temperature\"\\s*:\\s*(\\d+)");
                if (mW.Success) wear = int.Parse(mW.Groups[1].Value);
                if (mT.Success) temp = int.Parse(mT.Groups[1].Value);
                return (wear, temp);
            }
            catch { return (null, null); }
        }

        /// <summary>
        /// Lê a tabela SMART (30 atributos × 12 bytes) via WMI root\WMI
        /// MSStorageDriver_FailurePredictData + FailurePredictThresholds.
        /// Match por modelo normalizado dentro do InstanceName.
        /// </summary>
        private static List<SmartAttr> ReadSmartAttributes(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return null;
            string normModel = Normalize(model);
            if (normModel.Length < 4) return null;

            var attrs = new List<SmartAttr>();
            var thresholds = new Dictionary<byte, byte>();

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "root\\WMI", "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData");
                foreach (var o in searcher.Get())
                {
                    string inst = o["InstanceName"] as string ?? "";
                    if (!InstanceMatches(Normalize(inst), normModel)) continue;
                    var bytes = o["VendorSpecific"] as byte[];
                    if (bytes != null && bytes.Length >= 12)
                    {
                        ParseAttributeTable(bytes, 2, attrs);
                        break;
                    }
                }
                if (attrs.Count == 0) return null;

                using var st = new ManagementObjectSearcher(
                    "root\\WMI", "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictThresholds");
                foreach (var o in st.Get())
                {
                    string inst = o["InstanceName"] as string ?? "";
                    if (!InstanceMatches(Normalize(inst), normModel)) continue;
                    var bytes = o["VendorSpecific"] as byte[];
                    if (bytes != null)
                    {
                        for (int off = 2; off + 12 <= bytes.Length; off += 12)
                        {
                            byte id = bytes[off];
                            if (id != 0 && !thresholds.ContainsKey(id))
                                thresholds[id] = bytes[off + 1];
                        }
                        break;
                    }
                }

                foreach (var a in attrs)
                    if (thresholds.TryGetValue(a.Id, out byte th)) a.Threshold = th;

                return attrs;
            }
            catch { return attrs.Count > 0 ? attrs : null; }
        }

        /// <summary>Parseia 30 registros de 12 bytes: [0]=id, [1..2]=flags, [3]=value, [4]=worst, [5..10]=raw.</summary>
        private static void ParseAttributeTable(byte[] blob, int start, List<SmartAttr> into)
        {
            int count = 0;
            for (int off = start; off + 12 <= blob.Length && count < 30; off += 12)
            {
                byte id = blob[off];
                if (id == 0) continue;
                // registros de lixo/repetição no fim do blob têm value=worst=0 — pular
                if (blob[off + 3] == 0 && blob[off + 4] == 0) continue;
                var a = new SmartAttr
                {
                    Id = id,
                    Flags = BitConverter.ToUInt16(blob, off + 1),
                    Value = blob[off + 3],
                    Worst = blob[off + 4],
                    Raw = 0
                };
                for (int i = 5; i <= 10; i++)
                    a.Raw |= (ulong)blob[off + i] << ((i - 5) * 8);
                into.Add(a);
                count++;
            }
        }

        private static string Normalize(string s) =>
            Regex.Replace(s ?? "", "[^A-Za-z0-9]", "").ToUpperInvariant();

        /// <summary>
        /// Match tolerante entre modelo e InstanceName do WMI — o campo Prod do
        /// InstanceName é truncado (ex.: "WD5000AZRX-00L4H" sem o "B0"), então
        /// testamos prefixos progressivos e também o modelo sem o fabricante.
        /// </summary>
        private static bool InstanceMatches(string instNorm, string modelNorm)
        {
            if (string.IsNullOrEmpty(instNorm) || string.IsNullOrEmpty(modelNorm)) return false;
            // 1) modelo completo e prefixos progressivos (mín. 10 chars p/ evitar falso positivo)
            for (int len = modelNorm.Length; len >= 10; len--)
                if (instNorm.Contains(modelNorm.Substring(0, len))) return true;
            // 2) modelo sem o prefixo do fabricante (WMI põe o fabricante em Ven_, não no Prod_)
            foreach (var mfr in new[] { "WDC", "KINGSTON", "TOSHIBA", "SAMSUNG", "SEAGATE", "CRUCIAL", "TFORCE", "SANDISK", "HGST", "MICRON", "INTEL", "KIOXIA" })
            {
                if (!modelNorm.StartsWith(mfr)) continue;
                string rest = modelNorm.Substring(mfr.Length);
                for (int len = rest.Length; len >= 10; len--)
                    if (instNorm.Contains(rest.Substring(0, len))) return true;
            }
            return false;
        }
    }
}
