using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    /// <summary>
    /// Reparo de BOOT/BCD no estilo EaseUS BootRepair (ver docs/EASEUS_EPM_ANALYSIS.md).
    ///
    /// A ideia do EaseUS (vista no IDA em CBcdBootProc::TransferBootStaff) é simples e é a oficial:
    ///   1. bcdboot &lt;dir do Windows&gt; /s &lt;letra da ESP&gt; /f UEFI|BIOS   -> recria a loja BCD;
    ///   2. bootsect /nt60 &lt;volume do sistema&gt; /mbr                     -> BIOS: código de boot + MBR.
    /// O KitLugia até agora só fazia CIRURGIA com bcdedit (/create, /set, /displayorder) — que é
    /// exatamente o que o Win32Bcd.dll do EaseUS faz — mas nunca tinha a camada de reconstrução,
    /// que é a que salva uma BCD corrompida.
    ///
    /// Tudo aqui é verificado: nada de "deu OK" sem conferir o resultado (mesma lição do diskpart
    /// devolvendo exit 0 sem fazer nada).
    /// </summary>
    public static class BcdRepairManager
    {
        public const string WinloadPath = @"\Windows\System32\boot\winload.efi";
        public const string EspBootFolder = @"\EFI\Microsoft\Boot";

        // O bcdedit moderno (pt-BR "Carregador de Inicialização do Windows" / en-US "Windows Boot Loader")
        // NAO imprime a linha "application osloader" nas entradas reais — a deteccao correta é pelo
        // cabecalho do bloco + uma linha "path" apontando para winload (validado no host).
        private static readonly Regex WinLoaderHeader = new(
            @"Windows\s+Boot\s+Loader|Carregador\s+de\s+Inicializa\w*\s+do\s+Windows",
            RegexOptions.IgnoreCase);

        private static int CountWinLoadEntries(string bcdEnum, bool onlyRealWindows)
        {
            int count = 0;
            foreach (var block in SplitBcdBlocks(bcdEnum))
            {
                if (!WinLoaderHeader.IsMatch(block)) continue;
                if (!Regex.IsMatch(block, @"\bpath\b[^\r\n]*winload", RegexOptions.IgnoreCase)) continue;
                bool isWinPe = Regex.IsMatch(block, @"^\s*winpe\s+(?:sim|yes|1)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                if (onlyRealWindows && isWinPe) continue;   // entrada do WinRE/WinPE, nao do Windows
                count++;
            }
            return count;
        }

        private static IEnumerable<string> SplitBcdBlocks(string bcdEnum)
        {
            if (string.IsNullOrWhiteSpace(bcdEnum)) yield break;
            var current = new StringBuilder();
            foreach (var line in bcdEnum.Split('\n'))
            {
                // Separador de bloco do bcdedit: linha só com "-" ou "_" (5+)
                if (Regex.IsMatch(line, @"^\s*[-_]{5,}\s*$"))
                {
                    if (current.Length > 0) yield return current.ToString();
                    current.Clear();
                    continue;
                }
                current.AppendLine(line);
            }
            if (current.Length > 0) yield return current.ToString();
        }

        // ─────────────────────────────────────────────────────────── diagnóstico

        public sealed class BcdDiagnosis
        {
            public bool IsUefi { get; set; }
            public string FirmwareMode { get; set; } = "?";
            public string FirmwareEvidence { get; set; } = "";

            public string? EspLetter { get; set; }
            public string? EspDescription { get; set; }
            public ulong EspSize { get; set; }

            public string? WindowsLetter { get; set; }
            public string? WindowsDir { get; set; }

            public bool BcdStoreExists { get; set; }
            public bool BootMgfwPresent { get; set; }
            public bool BootFilesPresent { get; set; }
            public bool HasOsLoaderEntry { get; set; }
            public int OsLoaderCount { get; set; }
            public int RealWindowsEntries { get; set; }
            public bool BcdeditReadable { get; set; }
            public string BcdeditError { get; set; } = "";

            public List<string> Problems { get; } = new List<string>();   // bloqueiam o boot
            public List<string> Warnings { get; } = new List<string>();   // não bloqueiam, mas importam

            public bool Healthy => Problems.Count == 0;
            public bool CanRepair => !string.IsNullOrEmpty(WindowsLetter) && !string.IsNullOrEmpty(EspLetter);

            public string Summary
            {
                get
                {
                    if (Problems.Count == 0) return $"Boot saudável: {WindowsLetter}:\\Windows em {FirmwareMode}, ESP {EspLetter}:.";
                    return string.Join(" ", Problems);
                }
            }
        }

        /// <summary>Detecta UEFI x BIOS (Secure Boot + ESP + bcdedit {fwbootmgr}).</summary>
        public static (bool IsUefi, string Evidence) DetectFirmwareMode()
        {
            var evidence = new List<string>();

            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
                if (key?.GetValue("UEFISecureBootEnabled") is int sb && sb == 1)
                    evidence.Add("SecureBootEnabled=1");
            }
            catch { }

            bool hasEsp = FindEspPartition() != null;
            if (hasEsp) evidence.Add("partição EFI presente");

            if (evidence.Count == 0) return (false, "sem evidências de UEFI (ESP/SecureBoot ausentes)");

            // bcdedit {fwbootmgr} é o teste decisivo: só existe em UEFI.
            bool fwMgr = ProbeFirmwareBootManager();
            if (fwMgr) evidence.Add("bcdedit {fwbootmgr} acessível");

            bool isUefi = fwMgr || (hasEsp && evidence.Any(e => e.Contains("SecureBoot")));
            return (isUefi, string.Join(" + ", evidence));
        }

        /// <summary>Analisa a BCD do sistema (ou de um Windows alvo) sem alterar nada.</summary>
        public static async Task<BcdDiagnosis> DiagnoseAsync(string? targetWindowsLetter = null, Action<double, string>? progress = null)
        {
            var d = new BcdDiagnosis();
            string? espMountedByUs = null;
            bool weMountedEsp = false;
            try
            {
            progress?.Invoke(10, "Detectando firmware...");

            (d.IsUefi, d.FirmwareEvidence) = DetectFirmwareMode();
            d.FirmwareMode = d.IsUefi ? "UEFI" : "BIOS";

            progress?.Invoke(25, "Localizando partição EFI...");
            var esp = FindEspPartition();
            if (esp == null)
            {
                // ESP sem letra (o caso normal): monta temporariamente para ler.
                progress?.Invoke(28, "ESP sem letra — montando temporariamente...");
                var (mLetter, mOurs) = await EnsureEspMountedAsync().ConfigureAwait(false);
                if (!string.IsNullOrEmpty(mLetter))
                {
                    espMountedByUs = mLetter; weMountedEsp = mOurs;
                    esp = FindEspPartition();
                    if (esp == null)
                    {
                        // A montagem mudou as letras: relê direto da letra montada.
                        d.EspLetter = mLetter;
                        d.EspDescription = $"ESP montada em {mLetter}:";
                    }
                }
            }
            if (esp != null)
            {
                var espPart = esp.Value.Part;
                d.EspLetter = Normalize(espPart.DriveLetter);
                if (string.IsNullOrEmpty(d.EspLetter) && !string.IsNullOrEmpty(espMountedByUs))
                    d.EspLetter = espMountedByUs;
                d.EspDescription = $"{espPart.Type} {espPart.Label} ({espPart.SizeString})";
                d.EspSize = espPart.Size;
            }

            progress?.Invoke(40, "Localizando o Windows...");
            var win = ResolveWindowsLetter(targetWindowsLetter);
            d.WindowsLetter = win;
            d.WindowsDir = win == null ? null : $"{win}:\\Windows";

            progress?.Invoke(60, "Verificando arquivos de boot na ESP...");
            if (!string.IsNullOrEmpty(d.EspLetter))
            {
                string bootFolder = $"{d.EspLetter}:{EspBootFolder}";
                d.BootMgfwPresent = File.Exists(Path.Combine(bootFolder, "bootmgfw.efi"));
                d.BootFilesPresent = File.Exists(Path.Combine(bootFolder, "bootx64.efi"))
                                  || File.Exists(Path.Combine(bootFolder, "BCD"));
                d.BcdStoreExists = File.Exists(Path.Combine(bootFolder, "BCD"));
            }
            else
            {
                d.Problems.Add("Não encontrei partição EFI (ESP) para a BCD. Em disco GPT ela é obrigatória.");
            }

            progress?.Invoke(80, "Lendo a loja BCD (bcdedit /enum)...");
            var (rc, output) = await RunAsync("bcdedit.exe", "/enum all", 20000);
            d.BcdeditReadable = rc == 0;
            if (!d.BcdeditReadable)
            {
                d.BcdeditError = FirstInterestingLine(output) ?? $"bcdedit /enum all retornou código {rc}";
                d.Problems.Add($"A loja BCD não pôde ser lida: {d.BcdeditError}");
            }
            else
            {
                d.OsLoaderCount = CountWinLoadEntries(output, onlyRealWindows: false);
                d.RealWindowsEntries = CountWinLoadEntries(output, onlyRealWindows: true);
                d.HasOsLoaderEntry = d.OsLoaderCount > 0;
            }

            progress?.Invoke(95, "Consolidando diagnóstico...");

            if (d.WindowsLetter == null)
                d.Problems.Add("Não encontrei uma instalação do Windows (ex.: C:\\Windows) para reconstruir a BCD.");

            if (!d.BcdStoreExists && !string.IsNullOrEmpty(d.EspLetter))
                d.Problems.Add($"Arquivo BCD ausente em {d.EspLetter}:{EspBootFolder}\\BCD.");

            if (!d.BootMgfwPresent && !string.IsNullOrEmpty(d.EspLetter))
                d.Problems.Add($"bootmgfw.efi ausente em {d.EspLetter}:{EspBootFolder} (sem ele o firmware não encontra o gerenciador).");

            if (d.BcdeditReadable && d.RealWindowsEntries == 0)
                d.Problems.Add("A BCD não tem nenhuma entrada de bootedor do Windows (winload) — o PC não tem como iniciar o Windows.");

            if (!d.IsUefi)
                d.Warnings.Add("O sistema está em modo BIOS/legacy: o reparo também vai reescrever o código de boot com bootsect.");
            if (d.RealWindowsEntries > 1)
                d.Warnings.Add($"Há {d.RealWindowsEntries} entradas do Windows na BCD — o menu mostra duplicatas.");
            if (!string.IsNullOrEmpty(d.EspLetter) && d.EspSize < 100UL * 1024 * 1024)
                d.Warnings.Add($"A ESP tem só {d.EspSize / (1024 * 1024)} MB — pouco espaço para atualizações do boot.");
            if (d.BcdStoreExists && d.BcdeditReadable && d.HasOsLoaderEntry && d.BootMgfwPresent && d.BootFilesPresent)
                d.Warnings.Add("A BCD parece íntegra; um reparo completo vai recriá-la do zero (use o backup).");

            progress?.Invoke(100, "Diagnóstico concluído.");
            return d;
            }
            finally
            {
                await ReleaseEspMountAsync(espMountedByUs, weMountedEsp).ConfigureAwait(false);
            }
        }

        // ─────────────────────────────────────────────────────────────── reparo

        /// <summary>
        /// Reconstroi a loja BCD do zero com bcdboot (e bootsect no BIOS). É o reparo canonico.
        /// Faz BACKUP da loja atual antes e verifica o resultado depois.
        /// </summary>
        public static async Task<(bool Ok, string Message)> RebuildAsync(
            string windowsLetter,
            string espLetter,
            bool isUefi,
            bool createBackup = true,
            string? bcdLanguage = null,
            Action<double, string>? progress = null)
        {
            windowsLetter = Normalize(windowsLetter);
            espLetter = Normalize(espLetter);

            if (string.IsNullOrEmpty(windowsLetter))
                return (false, "Informe a letra do Windows.");

            string winDir = $"{windowsLetter}:\\Windows";
            if (!Directory.Exists(winDir))
                return (false, $"A pasta {winDir} não existe — escolha a letra do volume do Windows.");

            // A ESP pode não ter letra (caso normal): monta sozinho em vez de travar.
            // Igual ao EaseUS (MountSrcBootPart antes do TransferBootStaff).
            string? espMountedByUs = null;
            bool weMountedEsp = false;
            try
            {
            if (string.IsNullOrEmpty(espLetter) || !Directory.Exists($"{espLetter}:\\"))
            {
                var (mLetter, mOurs) = await EnsureEspMountedAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(mLetter))
                    return (false, $"A partição EFI ({(string.IsNullOrEmpty(espLetter) ? "sem letra" : espLetter + ":")}) não está montada/acessível e não consegui montá-la.");
                espLetter = mLetter; espMountedByUs = mLetter; weMountedEsp = mOurs;
            }

            string? bcdboot = FindTool("bcdboot.exe");
            if (bcdboot == null)
                return (false, "bcdboot.exe não encontrado. Ele existe em toda instalação do Windows (%SystemRoot%\\System32) e no WinPE.");

            progress?.Invoke(15, "Backup da loja BCD atual...");

            string? backupDir = null;
            if (createBackup)
            {
                backupDir = BackupBcdStore(espLetter);
                if (backupDir != null) Logger.Log($"[BCD] Backup da loja em {backupDir}");
                else Logger.LogWarning("BcdRepairManager", "Backup da BCD falhou — seguindo sem backup");
            }

            // Comando EXATO usado pelo EaseUS (IDA: CBcdBootProc::TransferBootStaff).
            string mode = isUefi ? "UEFI" : "BIOS";
            string langArg = string.IsNullOrWhiteSpace(bcdLanguage) ? "" : $" /l {bcdLanguage}";
            progress?.Invoke(35, $"Executando bcdboot ({mode})...");

            string args = $"\"{winDir}\" /s {espLetter}:\\ /f {mode}{langArg}";
            Logger.Log($"[BCD] > bcdboot.exe {args}");
            var (rc, outp) = await RunAsync(bcdboot, args, 180000);
            Logger.Log($"[BCD] bcdboot exit={rc} :: {TrimForLog(outp)}");

            if (rc != 0)
            {
                string why = FirstInterestingLine(outp) ?? $"bcdboot retornou código {rc}";
                if (isUefi)
                    return (false, $"bcdboot falhou: {why}\n\nVerifique se a partição {espLetter}: é FAT32 e se {winDir} é uma instalação válida do Windows.");
                // BIOS: bcdboot também é o caminho correto; mensagem simplificada
                return (false, $"bcdboot falhou: {why}\n\nVerifique se {winDir} é uma instalação válida do Windows.");
            }

            if (!isUefi)
            {
                progress?.Invoke(60, "BIOS: reescrevendo o código de boot (bootsect)...");
                string? bootsect = FindTool("bootsect.exe");
                if (bootsect == null)
                    return (false, "Bootsect.exe não encontrado — não foi possível gravar o código de boot do BIOS.");

                Logger.Log($"[BCD] > bootsect.exe /nt60 {windowsLetter}: /mbr");
                var (brc, bout) = await RunAsync(bootsect, $"/nt60 {windowsLetter}: /mbr", 120000);
                Logger.Log($"[BCD] bootsect exit={brc} :: {TrimForLog(bout)}");
                if (brc != 0)
                {
                    string why = FirstInterestingLine(bout) ?? $"bootsect retornou {brc}";
                    return (false, $"bcdboot OK, mas o bootsect falhou: {why}\nA BCD foi reconstruída; o código de boot do BIOS precisa ser refeito.");
                }
            }

            progress?.Invoke(85, "Verificando o resultado...");
            var check = await VerifyAsync(windowsLetter, espLetter, isUefi);
            if (!check.Ok)
                return (false, check.Message + (backupDir != null ? $"\n\nBackup da loja anterior: {backupDir}" : ""));

            string backupTxt = backupDir != null ? $"\nBackup da loja anterior: {backupDir}" : "";
            return (true, $"BCD reconstruída com sucesso ({mode}).{backupTxt}");
            }
            finally
            {
                await ReleaseEspMountAsync(espMountedByUs, weMountedEsp).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Recopia os arquivos de boot do Windows para a ESP (quando a BCD está boa mas os arquivos sumiram).
        /// </summary>
        public static async Task<(bool Ok, string Message)> RestoreBootFilesAsync(
            string windowsLetter, string espLetter, Action<double, string>? progress = null)
        {
            windowsLetter = Normalize(windowsLetter);
            espLetter = Normalize(espLetter);

            string? espMountedByUs = null;
            bool weMountedEsp = false;
            try
            {
            string src = $"{windowsLetter}:\\Windows\\Boot\\EFI";

            if (!Directory.Exists(src))
                return (false, $"Não encontrei {src} — essa cópia de boot não existe na instalação (partição EFI separada).");
            if (string.IsNullOrEmpty(espLetter) || !Directory.Exists($"{espLetter}:\\"))
            {
                var (mLetter, mOurs) = await EnsureEspMountedAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(mLetter))
                    return (false, $"A partição EFI ({(string.IsNullOrEmpty(espLetter) ? "sem letra" : espLetter + ":")}) não está acessível e não consegui montá-la.");
                espLetter = mLetter; espMountedByUs = mLetter; weMountedEsp = mOurs;
            }
            string dst = $"{espLetter}:{EspBootFolder}";

            progress?.Invoke(20, "Copiando arquivos de boot...");
            Directory.CreateDirectory(dst);

            int copied = 0;
            foreach (var file in Directory.EnumerateFiles(src))
            {
                string name = Path.GetFileName(file);
                if (name.Equals("BCD", StringComparison.OrdinalIgnoreCase)) continue; // NUNCA sobrescreve a BCD
                string target = Path.Combine(dst, name);
                try
                {
                    File.Copy(file, target, overwrite: true);
                    copied++;
                    progress?.Invoke(20 + 60.0 * copied / 10.0, $"Copiado: {name}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"[BCD] Falha ao copiar {name}: {ex.Message}");
                }
            }

            progress?.Invoke(90, "Verificando...");
            bool mgfw = File.Exists(Path.Combine(dst, "bootmgfw.efi"));
            if (!mgfw)
                return (false, $"A cópia terminou, mas {dst}\\bootmgfw.efi continua ausente. Use o reparo completo (bcdboot).");

            return (true, $"{copied} arquivo(s) de boot restaurado(s) em {dst}. A BCD não foi tocada.");
            }
            finally
            {
                await ReleaseEspMountAsync(espMountedByUs, weMountedEsp).ConfigureAwait(false);
            }
        }

        /// <summary>Confere se a BCD está utilizável depois de um reparo.</summary>
        public static async Task<(bool Ok, string Message)> VerifyAsync(string windowsLetter, string espLetter, bool isUefi)
        {
            string bootFolder = $"{Normalize(espLetter)}:{EspBootFolder}";

            if (!File.Exists(Path.Combine(bootFolder, "BCD")))
                return (false, $"A loja BCD não foi criada em {bootFolder}\\BCD.");

            if (!File.Exists(Path.Combine(bootFolder, "bootmgfw.efi")))
                return (false, $"bootmgfw.efi não foi gravado em {bootFolder}.");

            var (rc, outp) = await RunAsync("bcdedit.exe", "/enum all", 20000);
            if (rc != 0)
                return (false, "bcdboot rodou, mas a BCD continua ilegível para o bcdedit — o arquivo pode estar corrompido.");

            bool hasOs = CountWinLoadEntries(outp, onlyRealWindows: true) > 0;
            if (!hasOs)
                return (false, "A BCD foi recriada, mas sem entrada de boot do Windows (winload).");

            if (isUefi)
            {
                string expected = $@"{Normalize(windowsLetter)}:\Windows\System32\boot\winload.efi";
                bool pathOk = Regex.IsMatch(outp, @"path\s+[^\r\n]*winload\.efi", RegexOptions.IgnoreCase);
                if (!pathOk)
                    return (false, $"A entrada do Windows não aponta para winload.efi (esperado {expected}).");
            }

            return (true, "Verificação OK: BCD legível, com entrada de Windows e arquivos de boot presentes.");
        }

        /// <summary>
        /// Copia a loja BCD + arquivos de boot da ESP para uma pasta de backup datada.
        /// </summary>
        public static string? BackupBcdStore(string espLetter)
        {
            try
            {
                string src = $"{Normalize(espLetter)}:{EspBootFolder}";
                if (!Directory.Exists(src)) return null;

                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string dst = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                          "KitLugia", "BCD-Backup", stamp);

                Directory.CreateDirectory(dst);
                foreach (var file in Directory.EnumerateFiles(src))
                {
                    try { File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true); }
                    catch { /* arquivo em uso: segue */ }
                }

                // Guarda um dump do estado ANTES do reparo (para diff/diagnóstico).
                try
                {
                    var (rc, outp) = RunAsync("bcdedit.exe", "/enum all", 15000).GetAwaiter().GetResult();
                    if (rc == 0) File.WriteAllText(Path.Combine(dst, "bcdedit-enum-antes.txt"), outp, Encoding.UTF8);
                }
                catch { }

                return Directory.Exists(dst) ? dst : null;
            }
            catch (Exception ex)
            {
                Logger.Log($"[BCD] Backup falhou: {ex.Message}");
                return null;
            }
        }

        // ─────────────────────────────────────────────────────────────── auxiliares

        /// <summary>Partição EFI (ESP). Heurísticas: tipo GPT EFI, FAT32 pequena com rótulo, partição de sistema BIOS.</summary>
        public static (PartitionInfoEx Part, bool IsUefi)? FindEspPartition()
        {
            try
            {
                var candidates = new List<(PartitionInfoEx p, int score)>();
                foreach (var disk in PartitionManager.GetAllDisks())
                {
                    foreach (var p in disk.Partitions)
                    {
                        if (p.IsUnallocated || string.IsNullOrEmpty(p.DriveLetter)) continue;

                        int s = ScoreEspCandidate(p);
                        if (s > 0) candidates.Add((p, s));
                    }
                }

                var best = candidates.OrderByDescending(c => c.score).FirstOrDefault();
                return best.p == null ? null : (best.p, best.score >= 80);
            }
            catch (Exception ex)
            {
                Logger.Log($"[BCD] Falha ao procurar ESP: {ex.Message}");
                return null;
            }
        }

        private static int ScoreEspCandidate(PartitionInfoEx p)
        {
            string type = (p.Type ?? "").ToUpperInvariant();
            string label = (p.Label ?? "").ToUpperInvariant();
            string fs = (p.FileSystem ?? "").ToUpperInvariant();
            ulong mb = p.Size / (1024 * 1024);

            if (type.Contains("EFI")) return 100;
            // Flag IsSystem da Storage API (MSFT_Partition.IsSystem): e o sinal mais forte
            // quando a particao NAO tem letra (sem letra nao da para ler FileSystem/Label,
            // entao as regras de FS abaixo nao alcançam). Faixa 32 MB-2 GB exclui o C:.
            if (p.IsSystemFlag && mb >= 32 && mb <= 2048) return 70;
            // Legado (Win32_*) rotula a ESP como "GPT: System" sem flag e sem FS quando ela
            // nao tem letra — e o unico texto disponivel. Faixa estreita para nao pegar C:.
            if (type.Contains("SYSTEM") && mb >= 32 && mb <= 2048) return 65;
            if (fs is "FAT32" or "FAT16" or "FAT")
            {
                if (mb >= 32 && mb <= 2048 && (label.Contains("EFI") || label.Contains("SYSTEM") || label.Contains("SISTEMA")))
                    return 80;
                if (p.IsSystemFlag && mb <= 2048)
                    return 60;   // partição de sistema BIOS (100 MB)
                if (p.IsBootFlag && fs == "FAT32" && mb <= 1024)
                    return 50;
            }
            return 0;
        }

        /// <summary>
        /// Acha a ESP mesmo SEM letra de unidade (o caso normal: a ESP nasce sem letra).
        /// O FindEspPartition() exige letra e por isso o diagnóstico travava em "ESP não
        /// encontrada" numa máquina saudável — igual ao EaseUS, que monta (MountSrcBootPart)
        /// antes de operar (ver docs/EASEUS_EPM_MAP.md §6.1).
        /// </summary>
        public static PartitionInfoEx? FindEspPartitionNoLetter()
        {
            try
            {
                var candidates = new List<(PartitionInfoEx p, int score)>();
                foreach (var disk in PartitionManager.GetAllDisks())
                {
                    foreach (var p in disk.Partitions)
                    {
                        if (p.IsUnallocated) continue;
                        int s = ScoreEspCandidate(p);
                        if (s > 0) candidates.Add((p, s));
                    }
                }
                return candidates.OrderByDescending(c => c.score).FirstOrDefault().p;
            }
            catch (Exception ex)
            {
                Logger.Log($"[BCD] Falha ao procurar ESP sem letra: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Garante a ESP acessível por letra: usa a existente ou monta temporariamente via
        /// "mountvol X: /s". Retorna (letra, fomosNósQueMontamos). Quem recebe true DEVE
        /// chamar ReleaseEspMount(letra) no finally.
        /// </summary>
        public static async Task<(string? Letter, bool WeMounted)> EnsureEspMountedAsync()
        {
            var withLetter = FindEspPartition();
            if (withLetter != null && !string.IsNullOrEmpty(withLetter.Value.Part.DriveLetter))
                return (Normalize(withLetter.Value.Part.DriveLetter), false);

            var bare = FindEspPartitionNoLetter();
            if (bare == null) return (null, false);

            string free = FindFreeDriveLetter();
            if (string.IsNullOrEmpty(free)) return (null, false);

            Logger.Log($"[BCD] ESP sem letra — montando temporariamente em {free}: (mountvol /s)...");
            var (rc, outp) = await RunAsync("mountvol.exe", $"{free}: /s", 30000);
            Logger.Log($"[BCD] mountvol exit={rc} :: {TrimForLog(outp)}");
            if (rc != 0) return (null, false);

            // Confirma que montou de verdade (mountvol pode dizer 0 sem montar em ESP estranha).
            await Task.Delay(500).ConfigureAwait(false);
            try
            {
                if (!Directory.Exists($"{free}:\\"))
                {
                    Logger.Log($"[BCD] mountvol disse OK mas {free}: não abriu — desistindo.");
                    try { await RunAsync("mountvol.exe", $"{free}: /d", 15000).ConfigureAwait(false); } catch { }
                    return (null, false);
                }
            }
            catch { return (null, false); }

            return (free, true);
        }

        /// <summary>Desmonta a ESP somente se fomos nós que a montamos (WeMounted=true).</summary>
        public static async Task ReleaseEspMountAsync(string? letter, bool weMounted)
        {
            if (!weMounted || string.IsNullOrEmpty(letter)) return;
            try
            {
                Logger.Log($"[BCD] Desmontando ESP temporária {letter}: (mountvol /d)...");
                var (rc, outp) = await RunAsync("mountvol.exe", $"{letter}: /d", 15000).ConfigureAwait(false);
                Logger.Log($"[BCD] mountvol /d exit={rc} :: {TrimForLog(outp)}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[BCD] Falha ao desmontar ESP {letter}:: {ex.Message}");
            }
        }

        private static string FindFreeDriveLetter()
        {
            try
            {
                var used = new HashSet<char>(
                    DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));
                // De trás para frente (Z..S): longe das letras de dados do usuário.
                for (char c = 'Z'; c >= 'S'; c--)
                    if (!used.Contains(c) && !Directory.Exists($"{c}:\\"))
                        return c.ToString();
            }
            catch { }
            return "";
        }

        /// <summary>Letra do volume do Windows (usa a do sistema se existir; senão procura C:\Windows).</summary>
        public static string? ResolveWindowsLetter(string? preferido = null)
        {
            if (!string.IsNullOrWhiteSpace(preferido))
            {
                string p = Normalize(preferido!);
                if (Directory.Exists($"{p}:\\Windows\\System32")) return p;
            }

            // 1) o Windows que está rodando
            string sys = Normalize(Path.GetPathRoot(Environment.SystemDirectory) ?? "");
            if (!string.IsNullOrEmpty(sys) && Directory.Exists($"{sys}:\\Windows\\System32"))
                return sys;

            // 2) C: eLetters comuns com \Windows\System32
            foreach (char c in new[] { 'C', 'D', 'E' })
                if (Directory.Exists($"{c}:\\Windows\\System32")) return c.ToString();

            return null;
        }

        /// <summary>Procura uma ferramenta do Windows no sistema, na pasta do kit ou no System32.</summary>
        public static string? FindTool(string toolName)
        {
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.System),      // System32 do Windows/WinPE
                AppContext.BaseDirectory,                                            // kit published
                Path.Combine(Environment.SystemDirectory, toolName.ToLowerInvariant()),
                Path.Combine(AppContext.BaseDirectory, "Tools", "BootRepair")       // onde o kit guardaria
            };

            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                try
                {
                    string direct = Path.Combine(root, toolName);
                    if (File.Exists(direct)) return direct;
                    if (File.Exists(root)) return root;   // ja era o caminho completo
                }
                catch { }
            }
            return null;
        }

        private static bool ProbeFirmwareBootManager()
        {
            try
            {
                var (rc, _) = RunAsync("bcdedit.exe", "/enum {fwbootmgr}", 10000).GetAwaiter().GetResult();
                return rc == 0;
            }
            catch { return false; }
        }

        private static string Normalize(string letter)
        {
            string l = (letter ?? "").Trim().TrimEnd('\\').Replace(":", "");
            return string.IsNullOrWhiteSpace(l) ? "" : l.ToUpperInvariant();
        }

        private static string? FirstInterestingLine(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return null;
            foreach (var raw in output.Split('\n'))
            {
                string t = raw.Trim();
                if (t.Length < 4) continue;
                if (t.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    t.Contains("erro", StringComparison.OrdinalIgnoreCase) ||
                    t.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                    t.Contains("falha", StringComparison.OrdinalIgnoreCase) ||
                    t.Contains("não", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("[", StringComparison.Ordinal))
                    return t.Length > 160 ? t[..160] : t;
            }
            return null;
        }

        private static string TrimForLog(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return "(sem saída)";
            string one = output.Replace("\r", " ").Replace("\n", " ").Trim();
            return one.Length > 300 ? one[..300] + "..." : one;
        }

        /// <summary>Executa um processo capturando stdout+stderr (sem shell).</summary>
        private static async Task<(int ExitCode, string Output)> RunAsync(string file, string args, int timeoutMs)
        {
            // CP850 (OEM) para as ferramentas do Windows em pt-BR — no .NET Core a página de
            // código precisa ser registrada explicitamente (sem isso: NotSupportedException).
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.GetEncoding(850),
                StandardErrorEncoding = Encoding.GetEncoding(850)
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (-1, "Falha ao iniciar o processo");

            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            var all = Task.WhenAll(outTask, errTask);

            if (await Task.WhenAny(all, Task.Delay(timeoutMs)).ConfigureAwait(false) != all)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (-1, "TIMEOUT");
            }

            await proc.WaitForExitAsync().ConfigureAwait(false);
            return (proc.ExitCode, await outTask.ConfigureAwait(false) + await errTask.ConfigureAwait(false));
        }
    }
}