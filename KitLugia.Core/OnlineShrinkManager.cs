using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;
using System.Threading.Tasks;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    /// <summary>
    /// Shrink "profundo" SEM WinPE — replicação do efeito EaseUS Partition Master
    /// (1 reboot → shrink executado) usando apenas APIs do Windows completo.
    ///
    /// Por que funciona: o shrink online (Storage API / diskpart) e o shrink do WinPE
    /// usam o MESMO driver NTFS — o WinPE não adiciona capacidade. A única vantagem
    /// do ambiente offline é que pagefile.sys/hiberfil.sys/sombras VSS não existem lá.
    /// Este manager remove esses bloqueios NO Windows, reinicia 1× e executa o shrink
    /// online no boot seguinte, restaurando a configuração original em seguida.
    ///
    /// Fluxo (UX igual ao EaseUS):
    ///   1. PrepareDeepShrinkAsync: desliga hibernação, limpa sombras VSS, desativa
    ///      pagefile, grava config de retomada + RunOnce, reinicia.
    ///   2. Reboot → pagefile.sys/hiberfil.sys não existem mais.
    ///   3. ResumePendingShrinkAsync (chamado no startup do Kit): reduz o volume via
    ///      Storage API online, restaura pagefile/hibernação, grava resultado.
    ///
    /// Limitação honesta: nem este caminho nem o WinPE movem dados — ambos truncam.
    /// Se após liberar tudo ainda faltar espaço (zona do MFT, $LogFile), o único
    /// caminho que move dados é ntfsresize (Alpine/Offline).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class OnlineShrinkManager
    {
        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KitLugia");
        private static string ConfigPath => Path.Combine(Dir, "shrink_resume.json");
        private static string ResultPath => Path.Combine(Dir, "shrink_resume_result.json");

        private const string RunOnceKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
        private const string RunOnceName = "KitLugiaShrinkResume";

        public sealed class ResumeConfig
        {
            public string DriveLetter { get; set; } = "";
            public long ShrinkMb { get; set; }
            public bool HibernationWasOn { get; set; }
            /// <summary>Valor cru do registro PagingFiles (MultiString) antes de mexer.</summary>
            public List<string> PagingFilesRaw { get; set; } = new();
            public bool PagefileAutomatic { get; set; }   // legado: só leitura de configs antigas
            public List<PageFileEntry> PageFileSettings { get; set; } = new(); // legado
            public bool UsnJournalCleared { get; set; }
            public string ShadowStorageMaxBytes { get; set; } = ""; // para restaurar o limite VSS
            public string Timestamp { get; set; } = "";
        }

        public sealed class PageFileEntry
        {
            public string Name { get; set; } = "";
            public int? InitialSize { get; set; }
            public int? MaximumSize { get; set; }
        }

        // =====================================================================
        // 1) Análise — o shrink pedido cabe no limite ONLINE?
        // =====================================================================
        public static async Task<(long onlineMaxMb, bool fitsOnline)> AnalyzeAsync(char drive, long shrinkMb)
        {
            long onlineMax = await PartitionManager.GetMaxShrinkMb(drive.ToString());
            return (onlineMax, shrinkMb <= onlineMax);
        }

        // =====================================================================
        // 2) Preparação + reboot (o "1 reboot" estilo EaseUS)
        // =====================================================================
        public static async Task<(bool ok, string msg)> PrepareDeepShrinkAsync(
            char drive, long shrinkMb, Action<double, string>? progress = null)
        {
            try
            {
                var cfg = new ResumeConfig
                {
                    DriveLetter = drive.ToString().ToUpperInvariant(),
                    ShrinkMb = shrinkMb,
                    Timestamp = DateTime.Now.ToString("s")
                };

                // ---- 2.1 Hibernação (remove hiberfil.sys na hora, sem reboot) ----
                progress?.Invoke(10, "Desligando hibernação (libera hiberfil.sys)...");
                cfg.HibernationWasOn = File.Exists(@"C:\hiberfil.sys");
                if (cfg.HibernationWasOn)
                {
                    var (rc, _) = await PartitionManagerRun("powercfg", "/h off");
                    Logger.Log($"[SHRINK-ONLINE] powercfg /h off rc={rc}");
                }

                // ---- 2.2 Cópias sombra VSS (travam espaço no fim do volume) ----
                // Na máquina de referência havia 41,8 GB alocados em 7 pontos de
                // restauração — o MAIOR bloqueio de shrink, maior que pagefile+hiberfil.
                // Além de apagar, reduzimos o limite de armazenamento para o VSS não
                // recriar espaço entre a limpeza e o reboot (o original vai no config).
                progress?.Invoke(25, "Removendo cópias sombra (VSS)...");
                cfg.ShadowStorageMaxBytes = ReadShadowStorageMax($"/for={drive}:");
                await PartitionManagerRun("vssadmin", $"delete shadows /for={drive}: /all /quiet");
                await PartitionManagerRun("vssadmin", $"resize shadowstorage /for={drive}: /on={drive}: /maxsize=401MB");

                // ---- 2.3 Pagefile (sai de fato no reboot) ----
                // Usa o REGISTRO PagingFiles (REG_MULTI_SZ), não WMI: o hop documenta que
                // Win32_PageFileSetting moveu um pagefile de D: para C: num Windows real.
                progress?.Invoke(40, "Desativando arquivo de paginação...");
                cfg.PagingFilesRaw = ReadPagingFilesRaw();
                if (!DisablePagefile(drive.ToString()))
                    Logger.Log("[SHRINK-ONLINE] Aviso: não foi possível alterar o pagefile (segue mesmo assim)");

                // ---- 2.3b Journal de mudanças (UsnJrnl) ----
                // Vive em $Extend\$UsnJrnl, perto do fim da MFT, e pode travar o shrink.
                // O Windows recria sozinho com os defaults se faltar.
                progress?.Invoke(50, "Limpando journal de mudanças (USN)...");
                try
                {
                    var (usnCode, usnOut) = await PartitionManagerRun("fsutil", $"usn deletejournal /d {drive}:");
                    cfg.UsnJournalCleared = usnCode == 0;
                    Logger.Log($"[SHRINK-ONLINE] USN journal rc={usnCode} {usnOut.Trim()}");
                }
                catch (Exception uex)
                {
                    Logger.Log($"[SHRINK-ONLINE] Aviso ao limpar USN journal: {uex.Message}");
                }

                // ---- 2.4 Config de retomada + RunOnce ----
                progress?.Invoke(60, "Gravando retomada automática...");
                Directory.CreateDirectory(Dir);
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));

                string exePath = Environment.ProcessPath ?? "";
                if (!string.IsNullOrEmpty(exePath))
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RunOnceKey, writable: true);
                    key?.SetValue(RunOnceName, $"\"{exePath}\" --resume-shrink");
                }
                Logger.Log($"[SHRINK-ONLINE] Config de retomada gravada: {cfg.DriveLetter}: -{shrinkMb}MB");

                // ---- 2.5 Reboot ----
                progress?.Invoke(90, "Reiniciando...");
                await PartitionManagerRun("shutdown", "/r /t 10 /c \"KitLugia: shrink sem WinPE - o processo continuara apos o reboot\"");

                return (true,
                    "Preparação concluída! O PC reiniciará em 10 segundos.\n\n" +
                    $"Após o reboot, o KitLugia reduzirá {cfg.DriveLabel()}: em {shrinkMb} MB automaticamente\n" +
                    "(sem WinPE — direto no Windows, com pagefile/hibernação já removidos)\n" +
                    "e restaurará a configuração original em seguida.\n\n" +
                    "⚠️ O pagefile ficará ausente até o próximo reboot natural do Windows.");
            }
            catch (Exception ex)
            {
                Logger.Log($"[SHRINK-ONLINE] ERRO na preparação: {ex.Message}");
                return (false, $"Erro ao preparar shrink online: {ex.Message}");
            }
        }

        // =====================================================================
        // 3) Retomada — chamado no startup do Kit (RunOnce ou auto-start)
        // =====================================================================
        /// <summary>Retorna (ok, mensagem). (false, "") = nada a fazer.</summary>
        public static async Task<(bool ok, string msg)> ResumePendingShrinkAsync()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return (false, "");

                var cfg = JsonSerializer.Deserialize<ResumeConfig>(File.ReadAllText(ConfigPath));
                if (cfg == null || string.IsNullOrEmpty(cfg.DriveLetter) || cfg.ShrinkMb <= 0)
                {
                    CleanupConfig();
                    return (false, "");
                }

                Logger.Log($"[SHRINK-ONLINE] RETOMADA: {cfg.DriveLabel()}: -{cfg.ShrinkMb}MB (agendado em {cfg.Timestamp})");
                await Task.Delay(15000); // deixa o sistema assentar após o logon

                char drive = cfg.DriveLetter[0];

                // Tamanho atual do volume (WMI Storage)
                long currentSize = await GetCurrentVolumeSizeAsync(drive);
                if (currentSize <= 0)
                {
                    await FinishAsync(cfg, false, $"Volume {cfg.DriveLabel()}: não encontrado ou indisponível.");
                    return (false, $"Shrink retomado: volume {cfg.DriveLabel()}: indisponível. Nada foi alterado.");
                }

                long newSize = currentSize - cfg.ShrinkMb * 1024 * 1024;

                // Shrink online — mesma Storage API que o WinPE usaria
                bool ok = await PartitionManager.ShrinkPartitionUsingStorageAPI(drive, newSize);

                string detail;
                if (ok)
                {
                    detail = $"Volume {cfg.DriveLabel()}: reduzido em {cfg.ShrinkMb} MB (sem WinPE).";
                    Logger.Log($"[SHRINK-ONLINE] SUCESSO: {detail}");
                }
                else
                {
                    // Não alcançou: os bloqueios remanescentes são metadados NTFS
                    long maxNow = await PartitionManager.GetMaxShrinkMb(cfg.DriveLetter);
                    detail = maxNow > 0
                        ? $"Redução online limitada a {maxNow} MB (pedido: {cfg.ShrinkMb} MB). " +
                          "O que sobra são metadados NTFS (zona do MFT) — só um resize offline que move dados vai além."
                        : "Redução online falhou mesmo após liberar pagefile/hibernação/sombras.";
                    Logger.Log($"[SHRINK-ONLINE] PARCIAL: {detail}");
                }

                await FinishAsync(cfg, ok, detail);
                return (ok, detail);
            }
            catch (Exception ex)
            {
                Logger.Log($"[SHRINK-ONLINE] ERRO na retomada: {ex.Message}");
                return (false, $"Erro na retomada do shrink: {ex.Message}");
            }
        }

        private static async Task FinishAsync(ResumeConfig cfg, bool ok, string detail)
        {
            // ---- Restauração da configuração original ----
            try
            {
                RestorePagefile(cfg);
                if (cfg.HibernationWasOn)
                    await PartitionManagerRun("powercfg", "/h on");
                // devolve o limite de armazenamento de sombras original
                if (!string.IsNullOrEmpty(cfg.ShadowStorageMaxBytes))
                {
                    await PartitionManagerRun("vssadmin",
                        $"resize shadowstorage /for={cfg.DriveLetter}: /on={cfg.DriveLetter}: /maxsize={cfg.ShadowStorageMaxBytes}");
                }
            }
            catch (Exception ex) { Logger.Log($"[SHRINK-ONLINE] Aviso na restauração: {ex.Message}"); }

            try
            {
                var result = new Dictionary<string, object>
                {
                    ["Drive"] = cfg.DriveLetter, ["RequestedMb"] = cfg.ShrinkMb,
                    ["Success"] = ok, ["Detail"] = detail, ["Timestamp"] = DateTime.Now.ToString("s")
                };
                File.WriteAllText(ResultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }

            CleanupConfig();
        }

        private static void CleanupConfig()
        {
            try
            {
                if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RunOnceKey, writable: true);
                key?.DeleteValue(RunOnceName, throwOnMissingValue: false);
            }
            catch { }
        }

        // =====================================================================
        // Pagefile — leitura, desativação e restauração (WMI, defensivo)
        // =====================================================================
        private const string PagingKeyPath =
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

        /// <summary>
        /// Lê o valor CRU do registro PagingFiles (REG_MULTI_SZ).
        /// Cada entrada: "C:\pagefile.sys 0 0" (inicial máximo, 0 0 = gerenciado pelo sistema).
        ///
        /// Por que registro e não WMI: o hop (projeto que faz o mesmo shrink em produção)
        /// documenta que mexer via Win32_PageFileSetting "moved a paging file from D: to C:
        /// when it was tested on a real Windows". O registro é o que o Windows lê no boot,
        /// e pode ser restaurado exatamente como estava.
        /// </summary>
        private static List<string> ReadPagingFilesRaw()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(PagingKeyPath);
                if (key?.GetValue("PagingFiles") is string[] arr)
                    return arr.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            }
            catch (Exception ex)
            {
                Logger.Log($"[SHRINK-ONLINE] Aviso ao ler PagingFiles: {ex.Message}");
            }
            return new List<string>();
        }

        /// <summary>Grava PagingFiles exatamente como recebido (REG_MULTI_SZ).</summary>
        private static bool WritePagingFilesRaw(List<string> entries)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(PagingKeyPath, writable: true);
                if (key == null) return false;
                var clean = entries.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
                key.SetValue("PagingFiles", clean, Microsoft.Win32.RegistryValueKind.MultiString);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[SHRINK-ONLINE] Falha ao gravar PagingFiles: {ex.Message}");
                return false;
            }
        }

        /// <summary>Remove do PagingFiles apenas as entradas do drive alvo (mantém outros drives).</summary>
        private static List<string> PagingWithoutDrive(List<string> entries, string driveLetter)
        {
            string letter = driveLetter.TrimEnd(':').ToUpperInvariant();
            string sysDrive = (Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\")
                              .TrimEnd('\\').ToUpperInvariant();
            bool isSystem = (letter + ":") == sysDrive;

            return entries.Where(e =>
            {
                if (string.IsNullOrWhiteSpace(e)) return false;
                string path = e.Trim().Split(' ')[0];
                // "?:\pagefile.sys" = o Windows escolhe o drive (na pratica C:) — conta como C:
                if (path.StartsWith("?:")) return !isSystem;
                return !path.StartsWith(letter + ":", StringComparison.OrdinalIgnoreCase);
            }).ToList();
        }

        /// <summary>Desativa o pagefile do drive alvo (efetivo após reboot).</summary>
        private static bool DisablePagefile(string driveLetter)
        {
            var current = ReadPagingFilesRaw();
            var remaining = PagingWithoutDrive(current, driveLetter);
            bool ok = WritePagingFilesRaw(remaining);
            if (ok)
                Logger.Log($"[SHRINK-ONLINE] PagingFiles: {current.Count} entrada(s) -> {remaining.Count} " +
                           $"(removido o pagefile de {driveLetter}). Efetivo após o reboot.");
            return ok;
        }

        /// <summary>Restaura o PagingFiles exatamente como estava antes.</summary>
        private static void RestorePagefile(ResumeConfig cfg)
        {
            // Config antiga (pré-migração): usa o caminho WMI legado.
            if (cfg.PagingFilesRaw == null || cfg.PagingFilesRaw.Count == 0)
            {
                RestorePagefileLegacy(cfg);
                return;
            }

            if (WritePagingFilesRaw(cfg.PagingFilesRaw))
                Logger.Log($"[SHRINK-ONLINE] PagingFiles restaurado ({cfg.PagingFilesRaw.Count} entrada(s)). " +
                           "Ativo no próximo reboot.");
            else
                Logger.Log("[SHRINK-ONLINE] ERRO: não foi possível restaurar o PagingFiles — verifique " +
                           "Configurações do Sistema > Desempenho > Memória virtual.");
        }

        /// <summary>Compatibilidade com shrink_resume.json gerado por versões anteriores (WMI).</summary>
        private static void RestorePagefileLegacy(ResumeConfig cfg)
        {
            try
            {
                if (cfg.PagefileAutomatic || cfg.PageFileSettings.Count == 0)
                {
                    using var csObj = new ManagementObjectSearcher("root\\CIMV2", "SELECT AutomaticManagedPagefiles FROM Win32_ComputerSystem")
                        .Get().Cast<ManagementObject>().FirstOrDefault();
                    if (csObj != null)
                    {
                        csObj.SetPropertyValue("AutomaticManagedPagefiles", true);
                        csObj.Put();
                    }
                }
                else
                {
                    foreach (var e in cfg.PageFileSettings)
                    {
                        var ns = new ManagementClass("root\\CIMV2", "Win32_PageFileSetting", null).CreateInstance();
                        ns["Name"] = e.Name;
                        if (e.InitialSize.HasValue) ns["InitialSize"] = e.InitialSize.Value;
                        if (e.MaximumSize.HasValue) ns["MaximumSize"] = e.MaximumSize.Value;
                        ns.Put();
                    }
                }
                Logger.Log("[SHRINK-ONLINE] Pagefile restaurado via WMI (config legada).");
            }
            catch (Exception ex)
            {
                Logger.Log($"[SHRINK-ONLINE] Falha ao restaurar pagefile (legado): {ex.Message}");
            }
        }


        /// <summary>
        /// Lê o limite atual de armazenamento de sombras do volume via WMI
        /// (Win32_ShadowStorage.MaxSpace) — estruturado, sem parsing de saída
        /// localizada do vssadmin (que vem com mojibake de console pt-BR).
        /// Retorna "UNBOUNDED", bytes em decimal, ou "" se não souber.
        /// </summary>
        private static string ReadShadowStorageMax(string volArg)
        {
            try
            {
                char drive = volArg.Replace("/for=", "").Trim().FirstOrDefault();
                if (drive == ' ') return "";

                // GUID do volume da letra
                string? volGuid = null;
                foreach (var o in new ManagementObjectSearcher("root\\CIMV2",
                    $"SELECT DeviceID FROM Win32_Volume WHERE DriveLetter = '{drive}:'").Get())
                {
                    using var mo = (ManagementObject)o;
                    volGuid = mo["DeviceID"]?.ToString();
                    break;
                }
                if (string.IsNullOrEmpty(volGuid)) return "";

                foreach (var o in new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_ShadowStorage").Get())
                {
                    using var mo = (ManagementObject)o;
                    // Volume/DiffVolume voltam como string de referência WMI com as barras
                    // ESCAPADAS (\\\\?\\Volume{guid}\\) — compara sem barras para não depender disso
                    string volRef = (mo["Volume"]?.ToString() ?? "").Replace("\\", "");
                    if (volRef.IndexOf(volGuid.Replace("\\", ""), StringComparison.OrdinalIgnoreCase) < 0) continue;

                    if (mo["MaxSpace"] is ulong max)
                        return max >= ulong.MaxValue - 1 ? "UNBOUNDED" : max.ToString();
                }
            }
            catch (Exception ex) { Logger.Log($"[SHRINK-ONLINE] Falha ao ler limite VSS: {ex.Message}"); }
            return "";
        }

        private static async Task<long> GetCurrentVolumeSizeAsync(char drive)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"\\.\ROOT\Microsoft\Windows\Storage",
                    $"SELECT Size FROM MSFT_Partition WHERE DriveLetter = '{drive}'");
                foreach (var o in searcher.Get())
                {
                    using var mo = (ManagementObject)o;
                    return Convert.ToInt64(mo["Size"]);
                }
            }
            catch (Exception ex) { Logger.Log($"[SHRINK-ONLINE] Falha ao ler tamanho do volume: {ex.Message}"); }
            return 0;
        }

        private static async Task<(int rc, string output)> PartitionManagerRun(string file, string args)
        {
            try
            {
                using var proc = new System.Diagnostics.Process();
                proc.StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                proc.Start();
                var done = Task.Run(() => proc.WaitForExit(60000));
                await done;
                return (proc.ExitCode, "");
            }
            catch (Exception ex)
            {
                Logger.Log($"[SHRINK-ONLINE] Falha executando {file}: {ex.Message}");
                return (-1, ex.Message);
            }
        }
    }

    internal static class ShrinkConfigExt
    {
        public static string DriveLabel(this OnlineShrinkManager.ResumeConfig cfg) => cfg.DriveLetter;
    }
}
