using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    /// <summary>Estilo da tabela de partições (valores de MSFT_Disk.PartitionStyle).</summary>
    public enum DiskStyle
    {
        Unknown = 0,
        Mbr = 1,
        Gpt = 2,
        Raw = 3
    }

    /// <summary>Linha crua de MSFT_Disk (leitura pura, sem alterar nada).</summary>
    public sealed class DiskRow
    {
        public uint Number;
        public string Model = "";
        public ulong Size;
        public DiskStyle Style = DiskStyle.Unknown;
        public bool IsSystem;
        public bool IsBoot;
        public bool IsReadOnly;
        public bool IsOffline;
        public int PartitionCount;
        public string Signature = "";      // MBR: assinatura de disco (Guid em GPT)
        public string DiskGuid = "";       // GPT: GUID do disco

        public string IdText => Style == DiskStyle.Gpt
            ? (string.IsNullOrEmpty(DiskGuid) ? "(sem GUID)" : DiskGuid)
            : (string.IsNullOrEmpty(Signature) ? "(sem assinatura)" : Signature);

        public string SizeText => $"{Size / (1024.0 * 1024 * 1024):F1} GB";
        public string StyleText => Style switch
        {
            DiskStyle.Mbr => "MBR",
            DiskStyle.Gpt => "GPT",
            DiskStyle.Raw => "RAW",
            _ => "?"
        };
        public string Label => $"Disco {Number} - {Model} ({SizeText}) [{StyleText}]";
    }

    /// <summary>Linha crua de MSFT_Partition (leitura pura).</summary>
    public sealed class PartitionRow
    {
        public uint Number;
        public string DriveLetter = "";
        public ulong Offset;
        public ulong Size;
        public string GptType = "";
        public ushort MbrType;
        public bool IsReadOnly;
        public bool IsActive;
        public bool IsSystem;
        public bool IsBoot;

        public ulong End => Offset + Size;
        public string SizeText => $"{Size / (1024.0 * 1024 * 1024):F2} GB";
        public string Letter => string.IsNullOrEmpty(DriveLetter) ? "(sem letra)" : DriveLetter + ":";
    }

    /// <summary>Plano de conversão: o que vai acontecer e o que impede.</summary>
    public sealed class ConvertPlan
    {
        public uint DiskNumber;
        public string Model = "";
        public ulong Size;
        public DiskStyle Current = DiskStyle.Unknown;
        public DiskStyle Target = DiskStyle.Mbr;

        public bool IsSystemDisk;
        public bool IsBootDisk;
        public bool IsReadOnly;
        public bool IsOffline;
        public bool IsDynamic;
        public bool IsRunningSystemDisk;
        public bool IsWinPeSession;
        public bool IsUefiFirmware;
        public bool Is64BitOs;

        public int PartitionCount;
        public ulong LargestPartition;
        public string LargestPartitionDesc = "";
        public ulong TailFreeBytes;
        public bool HasBitLocker;
        public string BitLockerLetters = "";
        public int Misaligned1M;
        // Assinatura/GUID do disco (para identificar o disco certo no WinPE, onde o número
        // pode diferir do host). Preenchido pelo Analyze a partir do MSFT_Disk.
        public string CurrentSignature = "";

        public List<string> Blockers { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public List<string> Notes { get; } = new List<string>();

        public bool CanConvert => Blockers.Count == 0;

        public string CurrentName => Current switch
        {
            DiskStyle.Mbr => "MBR",
            DiskStyle.Gpt => "GPT",
            DiskStyle.Raw => "RAW (sem tabela de partições)",
            _ => "desconhecido"
        };

        public string TargetName => Target == DiskStyle.Gpt ? "GPT" : "MBR";

        public string Summary
        {
            get
            {
                if (Blockers.Count > 0)
                    return $"Conversão para {TargetName} bloqueada: {Blockers[0]}";
                return $"Disco {DiskNumber} ({Model}, {Size / (1024.0 * 1024 * 1024):F1} GB) pode ir de {CurrentName} para {TargetName}.";
            }
        }
    }

    /// <summary>Alinhamento de uma partição (1 MiB = padrão do Windows/diskpart "align 1024k").</summary>
    public sealed class AlignmentRow
    {
        public uint Number;
        public string Letter = "";
        public ulong Offset;
        public bool Aligned1M;
        public bool Aligned4K;
        public ulong MisalignBytes => Aligned1M ? 0 : Offset % (1024UL * 1024);
    }

    public sealed class AlignmentReport
    {
        public uint DiskNumber;
        public List<AlignmentRow> Partitions { get; } = new List<AlignmentRow>();
        public int Misaligned1M => Partitions.Count(p => !p.Aligned1M);
        public int Misaligned4K => Partitions.Count(p => !p.Aligned4K);
        public bool NeedsFix => Misaligned1M > 0;
        public string Summary => Misaligned1M == 0
            ? $"Disco {DiskNumber}: todas as {Partitions.Count} partições começam em múltiplo de 1 MiB."
            : $"Disco {DiskNumber}: {Misaligned1M} de {Partitions.Count} partições fora do alinhamento de 1 MiB.";
    }

    /// <summary>Conteúdo do setor 0 (MBR) lido direto do disco.</summary>
    public sealed class MbrInfo
    {
        public byte[] Raw = Array.Empty<byte>();
        public bool SignatureOk;              // 0x55AA nos bytes 510-511
        public bool HasBootstrap;             // bytes 0..445 não são todos zero
        public string BootstrapId = "";       // GRUB / SYSLINUX / MSDOS5.0 / desconhecido
        public int PartitionEntries;
        public bool TableSane;
        public bool ProtectiveMbr;   // disco GPT: entrada única do tipo 0xEE
        public uint DiskSignature;
        public List<string> Notes { get; } = new List<string>();

        public string Summary
        {
            get
            {
                string sig = SignatureOk ? "assinatura 0x55AA OK" : "assinatura 0x55AA AUSENTE (não inicializa)";
                string boot = HasBootstrap ? ("código de boot: " + (string.IsNullOrEmpty(BootstrapId) ? "presente" : BootstrapId)) : "SEM código de boot";
                return $"{sig}; {boot}; {PartitionEntries} entrada(s) na tabela; {(TableSane ? "tabela coerente" : "TABELA INCOERENTE")}";
            }
        }
    }

    /// <summary>
    /// Conversor de disco (MBR &lt;-&gt; GPT), inicialização de disco RAW, backup/restauro da MBR e
    /// diagnóstico de alinhamento 4K/1 MiB.
    ///
    /// Técnica (ver docs/EASEUS_EPM_ANALYSIS.md, DiskConverter.dll / IDA 9.0): o EaseUS declara
    /// as operações "Convert MBR to GPT", "Convert GPT to MBR", "Initialize to MBR/GPT disk",
    /// "Rebuild MBR" e "4K alignment" e valida tudo ANTES com mensagens como
    ///   - "Cannot convert the dynamic disk to MBR disk."
    ///   - "Cannot convert PE system disk to GPT disk." / "Cannot convert the 32-bit system disk to GPT disk."
    ///   - "At least 1M unallocated space is required in the end of disk, ..."
    ///   - "The selected GPT disk cannot be converted to MBR disk, because it contains the partition larger than %1."
    ///   - "Failed to generate the BCD file during MBR to GPT conversion."
    ///
    /// A diferença importante: o motor do EaseUS conversa com o VDS (COM). No Windows 8+ o sucessor
    /// do VDS é a Storage Management API, e é ela que o KitLugia já usa: MSFT_Disk.ConvertStyle
    /// converte a tabela NO LUGAR (partições preservadas) e MSFT_Disk.Initialize cria a tabela de
    /// um disco RAW — sem diskpart e sem exigir "disco vazio", restrição que a antiga função
    /// ConvertDiskStyle do KitLugia impunha.
    ///
    /// Regra do projeto: nada de "deu OK" sem verificar. Toda conversão é conferida relendo o
    /// estilo e a contagem de partições (conversão que perde partição é DITADA no relatório).
    /// </summary>
    public static class DiskConverterManager
    {
        private const string StorageScopePath = @"\\.\ROOT\Microsoft\Windows\Storage";

        private const ulong MiB = 1024UL * 1024;
        private const ulong KiB4 = 4096UL;

        // MBR endereça a partição com LBA de 32 bits => máximo 2 TiB - 512 B por partição.
        private const ulong MbrMaxPartitionBytes = 2UL * 1024 * 1024 * 1024 * 1024 - MiB;

        // O EaseUS exige 1 MB livres no FIM do disco para MBR->GPT ("At least 1M unallocated
        // space is required in the end of disk"): é onde o GPT backup header precisa caber.
        private const ulong TailFreeRequiredForGpt = MiB;

        // GUIDs de disco dinâmico (LDM) — presente = disco dinâmico (não convertível).
        private static readonly Guid LdmMetadataGuid = new("5808c8aa-7e8f-42e0-85d2-e1e90434cfb3");
        private static readonly Guid LdmDataGuid = new("af9b60a0-1431-4f62-bc68-3311714a69ad");

        private static void Log(string msg) => Logger.Log($"[DISKCONV] {msg}");

        // ─────────────────────────────────────────────────────────────── helpers

        public static DiskStyle ParseStyle(string? text)
        {
            var t = (text ?? "").Trim().ToUpperInvariant();
            if (t.Contains("GPT")) return DiskStyle.Gpt;
            if (t.Contains("MBR")) return DiskStyle.Mbr;
            if (t.Contains("RAW")) return DiskStyle.Raw;
            return DiskStyle.Unknown;
        }

        private static void ConnectStorage(ManagementScope scope)
        {
            scope.Connect();
        }

        /// <summary>Todos os discos com estilo/flags/assinatura (MSFT_Disk).</summary>
        public static List<DiskRow> GetDisks()
        {
            var list = new List<DiskRow>();
            try
            {
                var scope = new ManagementScope(StorageScopePath);
                ConnectStorage(scope);
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_Disk"));
                using var results = searcher.Get();

                foreach (ManagementObject mo in results)
                {
                    using (mo)
                    {
                        list.Add(new DiskRow
                        {
                            Number = Convert.ToUInt32(mo["Number"] ?? 0u),
                            Model = Convert.ToString(mo["FriendlyName"]) ?? "",
                            Size = Convert.ToUInt64(mo["Size"] ?? 0UL),
                            Style = (DiskStyle)Convert.ToUInt16(mo["PartitionStyle"] ?? 0),
                            IsSystem = Convert.ToBoolean(mo["IsSystem"] ?? false),
                            IsBoot = Convert.ToBoolean(mo["IsBoot"] ?? false),
                            IsReadOnly = Convert.ToBoolean(mo["IsReadOnly"] ?? false),
                            IsOffline = Convert.ToBoolean(mo["IsOffline"] ?? false),
                            PartitionCount = Convert.ToInt32(mo["NumberOfPartitions"] ?? 0),
                            Signature = Convert.ToString(mo["Signature"]) ?? "",
                            DiskGuid = Convert.ToString(mo["Guid"]) ?? ""
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"GetDisks falhou: {ex.Message}");
            }
            return list.OrderBy(d => d.Number).ToList();
        }

        /// <summary>Partições de um disco (MSFT_Partition, uma query por disco).</summary>
        public static List<PartitionRow> GetPartitions(uint diskNumber)
        {
            var list = new List<PartitionRow>();
            try
            {
                var scope = new ManagementScope(StorageScopePath);
                ConnectStorage(scope);
                using var searcher = new ManagementObjectSearcher(
                    scope, new ObjectQuery($"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber}"));
                using var results = searcher.Get();

                foreach (ManagementObject mo in results)
                {
                    using (mo)
                    {
                        string letter = Convert.ToString(mo["DriveLetter"]) ?? "";
                        list.Add(new PartitionRow
                        {
                            Number = Convert.ToUInt32(mo["PartitionNumber"] ?? 0u),
                            DriveLetter = string.IsNullOrWhiteSpace(letter) ? "" : letter,
                            Offset = Convert.ToUInt64(mo["Offset"] ?? 0UL),
                            Size = Convert.ToUInt64(mo["Size"] ?? 0UL),
                            GptType = Convert.ToString(mo["GptType"]) ?? "",
                            MbrType = Convert.ToUInt16(mo["MbrType"] ?? 0),
                            IsReadOnly = Convert.ToBoolean(mo["IsReadOnly"] ?? false),
                            IsActive = Convert.ToBoolean(mo["IsActive"] ?? false),
                            IsSystem = Convert.ToBoolean(mo["IsSystem"] ?? false),
                            IsBoot = Convert.ToBoolean(mo["IsBoot"] ?? false)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"GetPartitions({diskNumber}) falhou: {ex.Message}");
            }
            return list.OrderBy(p => p.Offset).ToList();
        }

        /// <summary>Invoke de um método do MSFT_Disk (ConvertStyle / Initialize / SetAttributes).</summary>
        private static uint InvokeDiskMethod(uint diskNumber, string method, object[] args, out string error)
        {
            error = "";
            try
            {
                var scope = new ManagementScope(StorageScopePath);
                ConnectStorage(scope);
                using var searcher = new ManagementObjectSearcher(
                    scope, new ObjectQuery($"SELECT * FROM MSFT_Disk WHERE Number = {diskNumber}"));
                using var results = searcher.Get();

                foreach (ManagementObject mo in results)
                {
                    using (mo)
                    {
                        return Convert.ToUInt32(mo.InvokeMethod(method, args));
                    }
                }
                error = "Disco não encontrado na Storage API.";
                return 404;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log($"InvokeDiskMethod({method}) falhou: {ex.Message}");
                return 999;
            }
        }

        private static uint InvokeDiskStyle(uint diskNumber, DiskStyle style, string method, out string error)
        {
            // ConvertStyle/Initialize: (PartitionStyle UInt16, ExtendedStatus MSFT_StorageExtendedStatus)
            return InvokeDiskMethod(diskNumber, method, new object[] { (ushort)style, null! }, out error);
        }

        private static string StorageErrorMessage(uint code) => code switch
        {
            0 => "Sucesso",
            1 => "Não suportado",
            2 => "Acesso negado (execute como administrador)",
            3 => "Falha de E/S",
            4 => "Sem memória",
            5 => "Parâmetro inválido",
            6 => "Tentativa inválida",
            7 => "Volume em uso",
            8 => "Mídia gravável não está presente",
            9 => "Falha de escrita",
            10 => "Erro de estrutura",
            11 => "Acesso negado (falta privilégio de administrador)",
            50 => "Não suportado pelo disco",
            51 => "Volume online",
            52 => "Volume não pode ser redimensionado",
            53 => "Disco é somente leitura",
            55 => "Disco online/offline-required",
            60 => "Objeto não existe",
            64 => "Disco está em uso (volume montado)",
            65 => "Partição ativa",
            67 => "Volume do sistema",
            68 => "Disco está offline",
            70 => "Disco com estilo diferente do esperado",
            71 => "Disco dinâmico",
            72 => "Disco MBR não pode virar GPT",
            73 => "Disco GPT não pode virar MBR",
            74 => "Partição muito grande para MBR (>2 TiB)",
            75 => "Estilos de disco são iguais",
            76 => "Disco precisa estar offline",
            40001 => "Acesso negado",
            40002 => "Recursos insuficientes",
            _ => $"erro {code}"
        };

        // ──────────────────────────────────────────────────────── contexto do PC

        /// <summary>True se estamos numa sessão WinPE (chave MiniNT é o marcador clássico).</summary>
        public static bool IsWinPeSession()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\MiniNT");
                return key != null;
            }
            catch { return false; }
        }

        private static char RunningSystemLetter()
        {
            try
            {
                string? root = Path.GetPathRoot(Environment.SystemDirectory);
                if (!string.IsNullOrEmpty(root))
                {
                    string letter = root.Replace("\\", "").TrimEnd(':');
                    if (letter.Length == 1 && char.IsLetter(letter[0])) return char.ToUpperInvariant(letter[0]);
                }
            }
            catch { }
            return '\0';
        }

        private static bool IsLetterOnDisk(char letter, uint diskNumber)
        {
            if (!char.IsLetter(letter)) return false;
            return GetPartitions(diskNumber).Any(p =>
                string.Equals(p.DriveLetter, letter.ToString(), StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasBitLocker(uint diskNumber, out string letters)
        {
            letters = "";
            try
            {
                var scope = new ManagementScope(@"\\.\root\CIMV2\Security\Microsoft\VolumeEncryption");
                scope.Connect();

                foreach (var part in GetPartitions(diskNumber))
                {
                    if (string.IsNullOrEmpty(part.DriveLetter)) continue;

                    using var searcher = new ManagementObjectSearcher(scope,
                        new ObjectQuery($"SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter='{part.DriveLetter}:'"));
                    using var vols = searcher.Get();
                    foreach (ManagementObject v in vols)
                    {
                        using (v)
                        {
                            object? ps = v["ProtectionStatus"];
                            if (ps != null && Convert.ToInt32(ps) == 1)
                                letters = (letters.Length > 0 ? letters + "," : "") + part.DriveLetter + ":";
                        }
                    }
                }
            }
            catch
            {
                // provedor ausente (BitLocker não instalado) = nada protegido
                return false;
            }
            return letters.Length > 0;
        }

        // ─────────────────────────────────────────────────────────────── análise

        /// <summary>
        /// Roda TODAS as verificações antes de converter (regras do EaseUS + o que o Windows exige).
        /// Leitura pura: nada é alterado.
        /// </summary>
        public static ConvertPlan Analyze(uint diskNumber, DiskStyle target)
        {
            var plan = new ConvertPlan { DiskNumber = diskNumber, Target = target };

            var disk = GetDisks().FirstOrDefault(d => d.Number == diskNumber);
            if (disk == null)
            {
                plan.Blockers.Add($"Disco {diskNumber} não existe mais (foi removido ou reenumerado).");
                return plan;
            }

            plan.Model = disk.Model;
            plan.Size = disk.Size;
            plan.Current = disk.Style;
            plan.CurrentSignature = disk.Style == DiskStyle.Gpt ? disk.DiskGuid : disk.Signature;
            plan.IsSystemDisk = disk.IsSystem;
            plan.IsBootDisk = disk.IsBoot;
            plan.IsReadOnly = disk.IsReadOnly;
            plan.IsOffline = disk.IsOffline;

            var parts = GetPartitions(diskNumber);
            plan.PartitionCount = parts.Count;

            var largest = parts.OrderByDescending(p => p.Size).FirstOrDefault();
            if (largest != null)
            {
                plan.LargestPartition = largest.Size;
                plan.LargestPartitionDesc = $"{largest.Letter} {largest.SizeText}";
            }

            ulong lastEnd = parts.Count > 0 ? parts.Max(p => p.End) : 0;
            plan.TailFreeBytes = disk.Size > lastEnd ? disk.Size - lastEnd : 0;

            plan.IsDynamic = parts.Any(p =>
                (p.GptType.Length > 0 &&
                 (Guid.TryParse(p.GptType, out Guid g) && (g == LdmMetadataGuid || g == LdmDataGuid))) ||
                p.MbrType == 0x42);   // 0x42 = LDM (partição de metadados de disco dinâmico)

            char runningLetter = RunningSystemLetter();
            plan.IsRunningSystemDisk = runningLetter != '\0' && IsLetterOnDisk(runningLetter, diskNumber);
            plan.IsWinPeSession = IsWinPeSession();
            plan.Is64BitOs = Environment.Is64BitOperatingSystem;
            plan.IsUefiFirmware = BcdRepairManager.DetectFirmwareMode().IsUefi;

            plan.HasBitLocker = HasBitLocker(diskNumber, out string blLetters);
            plan.BitLockerLetters = blLetters;

            plan.Misaligned1M = parts.Count(p => p.Offset % MiB != 0);

            // ── bloqueios ──
            if (plan.Current == target)
            {
                // Já está no estilo pedido: as verificações de formato (limite do MBR, folga no
                // fim do disco para o GPT, etc.) seriam ruído — encerrar aqui.
                plan.Blockers.Add($"O disco já está em {plan.TargetName} — não há nada para converter.");
                return plan;
            }

            if (plan.Current == DiskStyle.Raw)
                plan.Blockers.Add("Disco RAW (sem tabela de partições): use INICIALIZAR, não converter.");

            if (plan.Current == DiskStyle.Unknown)
                plan.Blockers.Add("Não consegui ler o estilo do disco (Storage API indisponível ou disco em transição).");

            if (plan.IsOffline)
                plan.Blockers.Add("Disco está offline (sem assinatura de volume). Coloque-o online antes de converter.");

            if (plan.IsReadOnly || parts.Any(p => p.IsReadOnly))
                plan.Blockers.Add("Disco ou partição em somente leitura (proteção de escrita do próprio hardware).");

            if (plan.HasBitLocker)
                plan.Blockers.Add($"BitLocker ativo em {plan.BitLockerLetters}. Suspenda a proteção antes de converter (o chaveiro é preso à partição).");

            if (plan.IsDynamic)
                plan.Blockers.Add("Disco dinâmico (LDM): o EaseUS também recusa (\"Cannot convert the dynamic disk to MBR/GPT disk\"). Converta para Básico antes.");

            if (plan.IsSystemDisk)
            {
                if (plan.IsRunningSystemDisk && plan.IsWinPeSession)
                    plan.Blockers.Add("Este é o disco do WinPE em execução — o EaseUS recusa por motivos óbvios (\"Cannot convert PE system disk to GPT disk\").");

                if (target == DiskStyle.Gpt && !plan.IsUefiFirmware)
                    plan.Blockers.Add("Disco de sistema em modo BIOS: o Windows NÃO inicializa de GPT em BIOS (sem UEFI). Converta pela ESP/UEFI ou mantenha MBR.");

                if (target == DiskStyle.Mbr && plan.IsUefiFirmware)
                    plan.Blockers.Add("Disco de sistema em modo UEFI: o Windows NÃO inicializa de MBR sem bootloader intermediário (GRUB/Syslinux).");

                if (target == DiskStyle.Gpt && !plan.Is64BitOs)
                    plan.Blockers.Add("Windows 32 bits em disco GPT não inicializa (\"Cannot convert the 32-bit system disk to GPT disk\").");
            }

            if (target == DiskStyle.Mbr)
            {
                if (plan.PartitionCount > 4)
                    plan.Blockers.Add($"São {plan.PartitionCount} partições; o MBR aceita no máximo 4 primárias. As últimas seriam PERDIDAS.");

                if (largest != null && largest.Size > MbrMaxPartitionBytes)
                    plan.Blockers.Add($"A partição {plan.LargestPartitionDesc} passa de 2 TiB — o MBR não endereça mais que isso (\"...contains the partition larger than 2 TB\").");

                if (plan.PartitionCount == 0)
                    plan.Notes.Add("Disco sem partições: a conversão cria a tabela, mas não há nada para preservar.");
            }
            else if (target == DiskStyle.Gpt)
            {
                if (plan.TailFreeBytes < TailFreeRequiredForGpt && plan.PartitionCount > 0)
                    plan.Blockers.Add($"Faltam {MiB / 1024} KB livres no fim do disco ({plan.TailFreeBytes} bytes) — é onde o GPT backup header precisa caber (\"At least 1M unallocated space is required in the end of disk\"). Reduza a última partição.");

                if (parts.Any(p => p.Offset % MiB != 0))
                    plan.Notes.Add($"Conversão aceita partições fora de 1 MiB, mas o alinhamento fica inconsistente ({plan.Misaligned1M} partição(ões)).");
            }

            // ── avisos ──
            if (plan.IsSystemDisk)
            {
                if (target == DiskStyle.Gpt)
                    plan.Warnings.Add("Disco de sistema em GPT: será preciso criar a ESP (FAT32, ~100-300 MB) + MSR e regerar a BCD — a página Reparar Boot/BCD faz isso (bcdboot).");
                else
                    plan.Warnings.Add("Disco de sistema em MBR: a partição EFI e a loja BCD na ESP deixam de ser usadas (o firmware passa a ler o MBR).");
            }

            if (plan.PartitionCount > 0)
                plan.Warnings.Add($"As {plan.PartitionCount} partição(ões) são convertidas NO LUGAR (in-place), com os dados intactos — mas para segurança prefira rodar o KitLugia pelo WinPE.");

            if (parts.Any(p => p.IsActive) && plan.Current == DiskStyle.Mbr)
                plan.Notes.Add($"Há partição marcada como ATIVA ({string.Join(",", parts.Where(p => p.IsActive).Select(p => p.Letter))}) — em MBR é assim que o BIOS acha o Windows.");

            if (parts.Any(p => p.Offset % KiB4 != 0))
                plan.Warnings.Add("Há partição começando fora de 4 KiB — alguns discos NVMe/USB falham ao ler esse layout.");

            if (plan.PartitionCount == 0 && target == DiskStyle.Gpt)
                plan.Notes.Add("Disco vazio: a conversão cria a GPT e o Windows pode instalar sem o passo de inicialização.");

            return plan;
        }

        // ────────────────────────────────────────────────────────── conversão

        /// <summary>
        /// O WinPE resolve o que é "Windows em uso": com o SO offline, disco de sistema e
        /// partições travadas convertem normalmente (é o PreOS do EaseUS). NÃO resolve:
        /// BitLocker (sem chave no WinPE), dinâmico, &gt;2 TiB para MBR, offline, RAW,
        /// sem espaço no fim (GPT precisa do backup header) — esses continuam bloqueados.
        /// Usa os FLAGS do plano (não as mensagens), para não depender de texto.
        /// </summary>
        public static (bool Eligible, string WhyNot) IsWinPeConvertEligible(ConvertPlan plan)
        {
            if (plan == null) return (false, "sem análise do disco.");
            if (plan.Current == DiskStyle.Raw)
                return (false, "disco RAW: use INICIALIZAR (funciona online, sem WinPE).");
            if (plan.Current == DiskStyle.Unknown)
                return (false, "estilo do disco desconhecido.");
            if (plan.Current == plan.Target)
                return (false, "o disco já está no estilo pedido.");
            if (plan.IsOffline)
                return (false, "disco offline.");
            if (plan.IsReadOnly)
                return (false, "disco em somente leitura.");
            if (plan.HasBitLocker)
                return (false, $"BitLocker ativo em {plan.BitLockerLetters} (sem a chave no WinPE).");
            if (plan.IsDynamic)
                return (false, "disco dinâmico (LDM).");
            if (string.IsNullOrWhiteSpace(plan.CurrentSignature))
                return (false, "sem assinatura/GUID do disco (não dá para identificar o disco certo no WinPE).");
            if (plan.Target == DiskStyle.Mbr)
            {
                if (plan.PartitionCount > 4)
                    return (false, $"são {plan.PartitionCount} partições (MBR aceita 4).");
                if (plan.LargestPartition > MbrMaxPartitionBytes)
                    return (false, "há partição maior que 2 TiB.");
            }
            else
            {
                if (plan.TailFreeBytes < TailFreeRequiredForGpt && plan.PartitionCount > 0)
                    return (false, "sem 1 MB livre no fim do disco (cabeçalho reserva do GPT).");
            }
            return (true, "");
        }

        /// <summary>
        /// Converte a tabela do disco preservando as partições.
        /// Escada: (1) MSFT_Disk.ConvertStyle (API oficial, in-place) -> (2) diskpart "convert".
        /// SEMPRE verifica no final: estilo novo + contagem de partições.
        /// </summary>
        public static async Task<(bool Ok, string Message)> ConvertAsync(
            uint diskNumber,
            DiskStyle target,
            bool force = false,
            Action<double, string>? progress = null,
            Action<string>? logLine = null)
        {
            var plan = Analyze(diskNumber, target);
            void Say(string t) { logLine?.Invoke(t); Log(t); }

            if (!plan.CanConvert && !force)
            {
                // Estilo EaseUS: read-only NAO e parede, e etapa — limpa e re-analisa
                // (docs/EASEUS_EPM_MAP.md §3.2: "antes de qualquer operação, eles limpam
                // o read-only"). So quando read-only for o UNICO bloqueio; o resto
                // (BitLocker, dinâmico, sistema/BIOS, >2 TiB) continua bloqueando.
                bool onlyReadOnly = plan.Blockers.Count == 1 &&
                    plan.Blockers[0].Contains("somente leitura");
                if (onlyReadOnly)
                {
                    Say("[CONVERT] Disco/partição em somente leitura — limpando flag antes de operar...");
                    progress?.Invoke(8, "Limpando flag somente-leitura...");
                    bool cleared = await PartitionManager.ClearDiskReadOnlyAsync(diskNumber).ConfigureAwait(false);
                    await Task.Delay(500).ConfigureAwait(false);
                    var replan = Analyze(diskNumber, target);
                    if (cleared && replan.CanConvert)
                    {
                        Say("[CONVERT] Flag limpa — seguindo com a conversão.");
                        plan = replan;
                    }
                    else
                    {
                        return (false, "Conversão bloqueada:\n- " + string.Join("\n- ", replan.Blockers) +
                            "\n\n(Tentei limpar a flag somente-leitura automaticamente, mas o disco continua protegido — pode ser chave física de proteção no dispositivo.)");
                    }
                }
                else
                {
                    return (false, "Conversão bloqueada:\n- " + string.Join("\n- ", plan.Blockers));
                }
            }

            var lines = new List<string>();
            lines.Add($"Disco {diskNumber} ({plan.Model}, {plan.Size / (1024.0 * 1024 * 1024):F1} GB): {plan.CurrentName} -> {plan.TargetName}");
            foreach (var b in plan.Blockers) lines.Add("  ! " + b);
            foreach (var w in plan.Warnings) lines.Add("  ⚠ " + w);

            int before = plan.PartitionCount;

            progress?.Invoke(15, "Aplicando conversão via Storage Management API...");
            Say($"[CONVERT] ConvertStyle -> {(ushort)target}");

            uint rc = InvokeDiskStyle(diskNumber, target, "ConvertStyle", out string err);
            string engine = "MSFT_Disk.ConvertStyle";

            if (rc != 0)
            {
                Say($"[CONVERT] ConvertStyle falhou (rc={rc} {err}) — tentando diskpart");
                progress?.Invoke(35, "API oficial recusou; tentando diskpart...");

                bool dpOk = await PartitionManager.RunDiskpartAsync(
                    diskNumber, target == DiskStyle.Gpt ? "gpt" : "mbr", logLine);
                engine = "diskpart convert";
                if (!dpOk)
                    return (false, $"A conversão falhou.\n- Storage API: {StorageErrorMessage(rc)}{(string.IsNullOrEmpty(err) ? "" : " (" + err + ")")}\n- diskpart: falhou também (veja o log).\n\n{string.Join("\n", lines)}");
            }

            progress?.Invoke(70, "Verificando resultado...");
            await Task.Delay(300);

            var diskAfter = GetDisks().FirstOrDefault(d => d.Number == diskNumber);
            var partsAfter = GetPartitions(diskNumber);

            lines.Add($"Método: {engine} (rc={rc}).");

            if (diskAfter == null)
                return (false, "O disco desapareceu durante a operação (foi desconectado).");

            lines.Add($"Estilo agora: {diskAfter.StyleText}" + (string.IsNullOrEmpty(diskAfter.Signature) ? "" : $"  assinatura={diskAfter.Signature}"));

            bool styleOk = diskAfter.Style == target;

            // A conversão PODE descartar partições (limite do MBR, entradas inválidas). Dizer.
            int lost = before - partsAfter.Count;
            string lostMsg = lost > 0
                ? $"\n\n⚠ ATENÇÃO: {lost} partição(ões) foram DESCARTADAS na conversão (antes {before}, agora {partsAfter.Count})."
                : "";

            if (!styleOk)
                return (false, $"A conversão não foi aplicada: o disco continua {diskAfter.StyleText} (esperado {plan.TargetName}).\n{string.Join("\n", lines)}");

            lines.Add($"Partições: {before} -> {partsAfter.Count}");

            foreach (var p in partsAfter)
                lines.Add($"  #{p.Number} {p.Letter} {p.SizeText} @ {p.Offset / MiB} MiB");

            if (lost > 0)
                lines.Add("As partições perdidas são as que não cabiam no formato de destino — confira antes de usar o disco.");

            if (target == DiskStyle.Gpt)
                lines.Add("\nPróximo passo recomendado: criar a ESP (FAT32 ~300 MB) + MSR e rodar Reparar Boot/BCD (bcdboot) para o Windows continuar inicializando.");

            progress?.Invoke(100, "Conversão concluída.");
            Say($"[CONVERT] OK {plan.CurrentName} -> {plan.TargetName} ({before} -> {partsAfter.Count} partições)");

            return (true, string.Join("\n", lines) + lostMsg);
        }

        /// <summary>
        /// Cria a tabela de partições num disco RAW (MSFT_Disk.Initialize) — equivale ao
        /// "Initialize to MBR/GPT disk" do EaseUS. Exige disco vazio.
        /// </summary>
        public static async Task<(bool Ok, string Message)> InitializeAsync(
            uint diskNumber,
            DiskStyle target,
            bool force = false,
            Action<double, string>? progress = null)
        {
            var plan = Analyze(diskNumber, target);
            var lines = new List<string>();

            if (plan.Current != DiskStyle.Raw && !force)
                return (false, $"O disco {diskNumber} já tem tabela ({plan.CurrentName}). Use CONVERTER, não INICIALIZAR.");

            var parts = GetPartitions(diskNumber);
            if (parts.Count > 0 && !force)
                return (false, $"O disco tem {parts.Count} partição(ões): inicializar exigiria apagar tudo. Formate o espaço não alocado em vez disso.");

            lines.Add($"Disco {diskNumber} ({plan.Model}, {plan.Size / (1024.0 * 1024 * 1024):F1} GB): inicializando em {plan.TargetName}");

            progress?.Invoke(30, $"Inicializando em {plan.TargetName}...");
            uint rc = InvokeDiskStyle(diskNumber, target, "Initialize", out string err);
            if (rc != 0)
                return (false, $"Falha ao inicializar (rc={rc}: {StorageErrorMessage(rc)}{(string.IsNullOrEmpty(err) ? "" : " — " + err)}).\n{string.Join("\n", lines)}");

            progress?.Invoke(80, "Verificando...");
            await Task.Delay(200);

            var after = GetDisks().FirstOrDefault(d => d.Number == diskNumber);
            lines.Add($"Estilo agora: {after?.StyleText ?? "?"}  assinatura={after?.Signature ?? ""}");

            if (after == null || after.Style != target)
                return (false, "A inicialização não foi confirmada pelo sistema.\n" + string.Join("\n", lines));

            progress?.Invoke(100, "Concluído.");
            Log($"[DISKCONV] Disco {diskNumber} inicializado em {plan.TargetName}");
            return (true, string.Join("\n", lines) + "\n\nO disco está vazio e pronto para criar partições.");
        }

        // ─────────────────────────────────────────────────────────────── MBR

        public static string BackupRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KitLugia", "MBR-Backup");

        /// <summary>Último backup de MBR gravado do disco (usado nas mensagens da GUI).</summary>
        public static string? BackupMbrPathFor(uint diskNumber)
        {
            try
            {
                if (!Directory.Exists(BackupRoot)) return null;
                var newest = new DirectoryInfo(BackupRoot)
                    .GetDirectories()
                    .OrderByDescending(d => d.LastWriteTimeUtc)
                    .FirstOrDefault();
                string? bin = newest?.GetFiles($"disk{diskNumber}-mbr.bin").FirstOrDefault()?.FullName;
                return bin;
            }
            catch { return null; }
        }

        private static byte[] ReadSector(uint diskNumber, long offset, int length, out string error)
        {
            error = "";
            try
            {
                using var fs = new FileStream($@"\\.\PhysicalDrive{diskNumber}", FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite);
                fs.Position = offset;
                var buf = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int n = fs.Read(buf, read, length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < length)
                {
                    error = $"Leitura parcial ({read} de {length} bytes)";
                    return buf;
                }
                return buf;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return Array.Empty<byte>();
            }
        }

        private static bool WriteSector(uint diskNumber, long offset, byte[] data, out string error)
        {
            error = "";
            try
            {
                using var fs = new FileStream($@"\\.\PhysicalDrive{diskNumber}", FileMode.Open,
                    FileAccess.ReadWrite, FileShare.ReadWrite);
                fs.Position = offset;
                fs.Write(data, 0, data.Length);
                fs.Flush(flushToDisk: true);   // SetFilePointer + WriteFile + FlushFileBuffers
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Lê e valida o setor 0 do disco (leitura pura).</summary>
        public static MbrInfo? ReadMbr(uint diskNumber)
        {
            var raw = ReadSector(diskNumber, 0, 512, out string err);
            if (raw.Length < 512)
            {
                Log($"ReadMbr({diskNumber}) falhou: {err}");
                return null;
            }

            var info = new MbrInfo { Raw = raw };
            info.SignatureOk = raw[510] == 0x55 && raw[511] == 0xAA;
            info.HasBootstrap = raw.Take(446).Any(b => b != 0);
            info.DiskSignature = BitConverter.ToUInt32(raw, 0x1B8);

            string ascii = Encoding.ASCII.GetString(raw, 0, 446);
            if (ascii.Contains("GRUB", StringComparison.OrdinalIgnoreCase)) info.BootstrapId = "GRUB";
            else if (ascii.Contains("SYSLINUX", StringComparison.OrdinalIgnoreCase)) info.BootstrapId = "Syslinux/ISOLINUX";
            else if (Encoding.ASCII.GetString(raw, 3, 5) == "MSDOS") info.BootstrapId = "MS-DOS/Windows MBR";
            else if (info.HasBootstrap) info.BootstrapId = "código desconhecido";

            int valid = 0;
            bool sane = true;
            for (int i = 0; i < 4; i++)
            {
                int off = 446 + i * 16;
                byte type = raw[off + 4];
                if (type == 0) continue;
                uint start = BitConverter.ToUInt32(raw, off + 8);
                uint count = BitConverter.ToUInt32(raw, off + 12);
                if (start == 0 || count == 0 || (ulong)start + count > 1UL << 40) sane = false;
                if (type == 0xEE) info.ProtectiveMbr = true;
                valid++;
            }
            info.PartitionEntries = valid;
            info.TableSane = sane;

            if (!info.SignatureOk) info.Notes.Add("Sem 0x55AA: nenhum BIOS consegue iniciar por este disco (é GPT, está zerado ou corrompido).");
            if (info.ProtectiveMbr) info.Notes.Add("Setor 0 é um MBR protetivo de disco GPT (entrada 0xEE): o boot real é lido pela ESP, não por aqui.");
            if (!info.HasBootstrap) info.Notes.Add("Os 446 bytes de código de boot estão zerados — o disco não inicializa em modo BIOS.");
            if (valid == 0 && info.SignatureOk) info.Notes.Add("Nenhuma entrada de partição reconhecível (0x00) — tabela zerada/corrompida.");
            if (!sane) info.Notes.Add("Há entradas de partição com LBA inicial/tamanho implausível.");
            if (info.DiskSignature == 0) info.Notes.Add("Assinatura de disco (offset 0x1B8) zerada.");
            if (valid > 4) info.Notes.Add($"Foram lidas {valid} entradas — mais que as 4 do MBR (partição estendida ou tabela estendida).");

            return info;
        }

        /// <summary>Salva o setor 0 (512 bytes) em %LOCALAPPDATA%\KitLugia\MBR-Backup\&lt;timestamp&gt;.</summary>
        public static async Task<(bool Ok, string Message)> BackupMbrAsync(
            uint diskNumber,
            string? destinationFolder = null,
            Action<double, string>? progress = null)
        {
            progress?.Invoke(20, "Lendo o setor 0...");
            var info = ReadMbr(diskNumber);
            if (info == null)
                return (false, $"Não consegui ler o setor 0 do disco {diskNumber} (execute como administrador; em disco online o antivírus pode bloquear).");

            string folder = string.IsNullOrWhiteSpace(destinationFolder)
                ? Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"))
                : destinationFolder!;

            try
            {
                Directory.CreateDirectory(folder);
                string bin = Path.Combine(folder, $"disk{diskNumber}-mbr.bin");
                await File.WriteAllBytesAsync(bin, info.Raw);

                var disk = GetDisks().FirstOrDefault(d => d.Number == diskNumber);
                string sha = Convert.ToHexString(SHA256.HashData(info.Raw));

                var meta = new StringBuilder();
                meta.AppendLine($"Disco           : {diskNumber}  ({disk?.Model ?? "?"})");
                meta.AppendLine($"Estilo          : {disk?.StyleText ?? "?"}");
                meta.AppendLine($"Tamanho         : {disk?.SizeText ?? "?"}");
                meta.AppendLine($"Assinatura disco: 0x{info.DiskSignature:X8}");
                meta.AppendLine($"0x55AA          : {(info.SignatureOk ? "presente" : "AUSENTE")}");
                meta.AppendLine($"Codigo de boot  : {(info.HasBootstrap ? info.BootstrapId : "(ausente)")}");
                meta.AppendLine($"Entradas MBR    : {info.PartitionEntries}");
                meta.AppendLine($"SHA256          : {sha}");
                meta.AppendLine($"Data            : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                await File.WriteAllTextAsync(Path.Combine(folder, $"disk{diskNumber}-mbr.txt"), meta.ToString());

                progress?.Invoke(100, "Backup concluído.");
                Log($"[DISKCONV] MBR do disco {diskNumber} salva em {bin}");

                return (true, $"Setor 0 do disco {diskNumber} salvo em:\n  {bin}\n  SHA256 {sha}\n\nEstado: {info.Summary}");
            }
            catch (Exception ex)
            {
                return (false, "Falha ao gravar o backup: " + ex.Message);
            }
        }

        /// <summary>
        /// Escreve uma MBR conhecida no setor 0 ("Rebuild MBR" do EaseUS, versão segura).
        /// keepCurrentPartitionTable = true pega SÓ o código de boot do modelo e mantém a tabela
        /// de partições que está no disco agora — é o modo correto para "recuperar o boot".
        /// </summary>
        public static async Task<(bool Ok, string Message)> RestoreMbrAsync(
            uint diskNumber,
            string templateFile,
            bool keepCurrentPartitionTable,
            Action<double, string>? progress = null,
            Action<string>? logLine = null)
        {
            void Say(string t) { logLine?.Invoke(t); Log(t); }

            byte[] template;
            try
            {
                template = File.ReadAllBytes(templateFile);
            }
            catch (Exception ex)
            {
                return (false, "Não consegui ler o modelo de MBR: " + ex.Message);
            }

            if (template.Length < 512)
                return (false, $"O arquivo tem {template.Length} bytes — um setor de MBR tem 512. Use o .bin gerado pelo backup.");

            progress?.Invoke(15, "Lendo a MBR atual (backup de segurança)...");
            var current = ReadMbr(diskNumber);
            if (current == null)
                return (false, "Não consegui ler a MBR atual do disco — nada foi escrito.");

            // Backup de segurança antes de qualquer escrita.
            var (bkOk, bkMsg) = await BackupMbrAsync(diskNumber, null, null);
            Say($"[MBR] backup de segurança: {(bkOk ? "ok" : "FALHOU — " + bkMsg)}");

            var payload = new byte[512];
            Array.Copy(template, payload, 512);

            if (keepCurrentPartitionTable)
                Array.Copy(current.Raw, 446, payload, 446, 66);   // 4 x 16 + 2 bytes de assinatura

            payload[510] = 0x55;
            payload[511] = 0xAA;

            progress?.Invoke(45, "Escrevendo o setor 0...");
            Say($"[MBR] gravando {Path.GetFileName(templateFile)} no disco {diskNumber} (keepTable={keepCurrentPartitionTable})");

            if (!WriteSector(diskNumber, 0, payload, out string werr))
                return (false, "Falha ao escrever no disco: " + werr);

            progress?.Invoke(75, "Verificando a leitura de volta...");
            var verify = ReadMbr(diskNumber);
            if (verify == null)
                return (false, "Escrita feita, mas não consegui reler o setor 0.");

            bool same = verify.Raw.Take(512).SequenceEqual(payload);
            if (!same)
                return (false, "O que foi lido de volta não bate com o que foi escrito — o disco pode ter ignorado a escrita.");

            bool tableKept = true;
            if (keepCurrentPartitionTable)
                tableKept = verify.Raw.Skip(446).Take(66).SequenceEqual(current.Raw.Skip(446).Take(66));

            progress?.Invoke(100, "Concluído.");
            Say("[MBR] escrita verificada");

            var msg = new StringBuilder();
            msg.AppendLine($"Setor 0 do disco {diskNumber} reescrito a partir de {Path.GetFileName(templateFile)}.");
            msg.AppendLine($"Modo: {(keepCurrentPartitionTable ? "só o código de boot; a tabela de partições atual foi mantida" : "setor 0 inteiro (código de boot + tabela)")}.");
            msg.AppendLine($"Lido de volta: {(same ? "idêntico ao escrito" : "DIFERENTE")}; tabela preservada: {(tableKept ? "sim" : "NÃO")}.");
            msg.AppendLine($"Antes : {current.Summary}");
            msg.AppendLine($"Agora : {verify.Summary}");
            foreach (var n in verify.Notes) msg.AppendLine($"  ⚠ {n}");
            if (bkOk)
                msg.AppendLine("\nBackup do setor 0 ANTERIOR gravado em: " + (BackupMbrPathFor(diskNumber) ?? "(pasta de backup)"));

            return (true, msg.ToString());
        }

        // ────────────────────────────────────────────────────────── alinhamento

        /// <summary>Diagnóstico de alinhamento das partições (sem alterar nada).</summary>
        public static AlignmentReport AnalyzeAlignment(uint diskNumber)
        {
            var report = new AlignmentReport { DiskNumber = diskNumber };
            foreach (var p in GetPartitions(diskNumber))
            {
                report.Partitions.Add(new AlignmentRow
                {
                    Number = p.Number,
                    Letter = p.Letter,
                    Offset = p.Offset,
                    Aligned1M = p.Offset % MiB == 0,
                    Aligned4K = p.Offset % KiB4 == 0
                });
            }
            Log($"[DISKCONV] {report.Summary}");
            return report;
        }
    }
}
