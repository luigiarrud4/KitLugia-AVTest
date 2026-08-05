# FAT_DISK_API — Plano: diskpart-free com DeviceIoControl (IOCTL nativo)

> Objetivo: deixar a PartitionsPage do KitLugia rapida como o wimlib e para o DISM —
> enumeração em milissegundos e operações destrutivas em segundos, sem WMI e sem diskpart.
> Tecnica comprovada pelo rpi-imager (`cleanDiskFast`): chamar `DeviceIoControl` direto.

## Status atual (02/08/2026)

### FASE 1 — CONCLUIDA: enumeração nativa + clean rápido

**`KitLugia.Core\NativeDiskIo.cs`** (novo, ~500 linhas, P/Invoke puro, sem dependencias):

| Método | IOCTL / API | Uso |
|---|---|---|
| `OpenDisk(n)` | CreateFile `\\.\PhysicalDriveN` | Abre disco (leitura; escrita p/ clean) |
| `OpenVolume(c)` | CreateFile `\\.\C:` com `FILE_READ_ATTRIBUTES` | Abre volume (funciona SEM admin) |
| `GetDeviceNumber` | `IOCTL_STORAGE_GET_DEVICE_NUMBER` (0x2D1080) | Confirma que o handle e disco fisico |
| `GetDiskSize` | `IOCTL_DISK_GET_DRIVE_GEOMETRY_EX` (0x700A0) | Tamanho do disco |
| `GetStorageProperties` | `IOCTL_STORAGE_QUERY_PROPERTY` (0x2D1400) | Modelo, serial, barramento (NVMe/SATA/USB) |
| `GetDriveLayout` / `ParseDriveLayout` | `IOCTL_DISK_GET_DRIVE_LAYOUT_EX` (0x70050) | Tabela MBR/GPT completa (estilo, particoes, GPT name WCHAR[36], boot flag) |
| `DeleteDriveLayout` | `IOCTL_DISK_DELETE_DRIVE_LAYOUT` (0x7C100) | "clean" do diskpart em segundos |
| `EnumerateVolumes` | GetLogicalDrives + GetVolumeInformationW + GetDiskFreeSpaceEx + `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` (0x560000) | Letra, label, FS, espaço livre, disco/offset de cada volume |
| `FindBootDiskNumber` | extents do volume do Windows | IsSystemDisk nativo |

**`KitLugia.Core\PartitionManager.cs`**:
- `GetAllDisks()` agora tenta **nativo primeiro** → fallback Storage API (MSFT_*) → fallback legado (Win32_*).
- `IsSystemDisk()` tenta nativo (boot disk por extents) → fallback MSFT_Disk.IsSystem → legado.
- `CleanDisk()` usa `IOCTL_DISK_DELETE_DRIVE_LAYOUT` quando `fullClean=false`; `clean all` continua diskpart.

**Cadeia de fallback (seguranca)**: qualquer excecao/access-denied no caminho nativo cai
automaticamente no caminho WMI anterior — comportamento da pagina nao regride.

### Quirks descobertos (importante para manutencao)

1. **PhysicalDrive exige admin** (erro 5 sem elevação). O app roda elevado → ok.
   Volume (`\\.\C:`) com `FILE_READ_ATTRIBUTES` funciona sem admin (GENERIC_READ da erro 5).
2. **Layout de `VOLUME_DISK_EXTENTS`**: `DWORD count` + **4 bytes de padding** + `DISK_EXTENT[24 bytes]`
   (DWORD + pad4 + LONG64 + LONG64). Extent comeca em offset 8, stride 24 (retornado=32 p/ 1 extent).
3. **Unions com `string` (ByValTStr) sao invalidas no CLR** para LayoutKind.Explicit
   (TypeLoadException "incorrectly aligned or overlapped"). Solucao: parsing por ponteiro
   (Marshal.ReadInt32/64/Byte) com offsets do winioctl.h — nada de structs com union.
4. **Cabecalho do DRIVE_LAYOUT_INFORMATION_EX NAO tem tamanho fixo** (confirmado por hexdump):
   - **GPT = 48 bytes**: o kernel (ntioapi.h) adiciona ao GUID de 16: `StartingUsableOffset`(8)
     + `UsableLength`(8) + `MaxPartitionCount`(4) + pad(4). O winioctl.h publico documenta 24,
     mas o kernel retorna 48 (validado: returned=768 = 48 + 5*144; primeira entrada em 0x30).
   - **MBR = 16 bytes**: style(4) + count(4) + Signature(4) + CheckSum(4).
   - Ler o estilo no offset 0 e usar 48/16 antes de somar o stride.
5. **PARTITION_INFORMATION_EX = 144 bytes** (alinhamento natural de 8):
   style(4) + pad(4) + StartOffset(8) + Length(8) + Number(4) + Rewrite(1) + pad(3) + union(112).
   GPT name: WCHAR[36] em offset 72 (union@32 + 40). Numero da particao em offset 24, NAO 20.
6. **GetStorageProperties**: `new byte[len]` NAO le os bytes (array de zeros!) — copiar com
   `Marshal.Copy` antes do Encoding.ASCII.GetString (bug real observado: modelo = 22/19 espacos).
7. **Disco GPT e MBR**: IsSystem = ESP GUID (c12a7328...); IsBoot = MBR boot indicator byte;
   WinRE = MBR 0x27 / GUID de94bba4...; MSR = e3c9e316...

### Testes feitos (host, ELEVADO — 02/08)

- **`GetAllDisks()` nativo em 18-19 ms** (vs 1358 ms via WMI sem admin): 2 discos,
  modelos corretos ("WDC WD5000AZRX-00L4HB0", "KINGSTON SFYRDK4000G"), SATA/NVMe,
  GPT, 465,8 GB / 3726,0 GB, IsSystemDisk(1)=True.
- **Partições corretas**: Disco 1 = Windows Recovery Environment 0,6 GB (GUID de94bba4),
  EFI System 0,1 GB (c12a7328), C: Basic data NTFS 3724,5 GB (ebd0a0a2), Recovery 0,8 GB,
  MSR (e3c9e316). Nomes GPT WCHAR[36] lidos corretamente.
- **Parsing sintetico** (buffer em memoria): GPT (header 48 + 2x144) e MBR (header 16 + 2x144)
  ambos ok=True, types/names/boot flags corretos. Cobre o caminho MBR sem hardware MBR.
- `EnumerateVolumes`: C: disco1@788557824 (bate com Storage API), E: disco0@1048576, boot=1.
- `GetAllDisks()` sem admin cai no fallback WMI (esperado, OpenDisk falha sem elevacao).

### Pendente (requer VM / disco de teste)
- [ ] Rodar KitLugia elevado → PartitionsPage → conferir no log "IOCTL nativo" e tempo de carregamento
- [ ] CleanDisk nativo num disco de teste (VM) → conferir que apagou a tabela em segundos

## FASE 2 — PROPOSTA: operações de escrita via IOCTL (risco alto, avaliar com cuidado)

| Operação | API | Observação |
|---|---|---|
| Estender partição | `IOCTL_DISK_GROW_PARTITION` (0x7C0D0) + `FSCTL_EXTEND_VOLUME` (0x900118) | Funciona com volume aberto; precisa espaço não alocado após a partição |
| Reduzir partição | `FSCTL_QUERY_SHRINK_VOLUME` (0x900114) → `FSCTL_SHRINK_VOLUME` (0x9001DC) | So NTFS/RAW; arquivos imóveis limitam; precisa loop de retry |
| Criar partição | `IOCTL_DISK_CREATE_DISK` + `IOCTL_DISK_SET_DRIVE_LAYOUT_EX` | Montar DRIVE_LAYOUT com nova entrada manualmente — risco de corromper tabela |
| Converter MBR<->GPT | `IOCTL_DISK_GET_DRIVE_LAYOUT_EX` → reescrever + UPDATE_PROPERTIES | Arriscado; manter diskpart como default |
| Refresh pós-operação | `IOCTL_DISK_UPDATE_PROPERTIES` + `IOCTL_DISK_ARE_VOLUMES_READY` (0x70021C, Win8+) | rpi-imager usa; evita sleep fixo |

**Recomendação**: implementar apenas **Estender** e **Reduzir** (IOCTLs oficiais com
semântica segura); criar/converter exigem montar tabelas manualmente — manter diskpart
(ou usar MSFT_Partition.Resize que já existe no kit).

## FASE 3 — BONUS (curiosidade respondida)

- **Fallback do cleanDiskFast** (rpi-imager, `diskpart_util.cpp:247-260`): se
  `IOCTL_DISK_DELETE_DRIVE_LAYOUT` falhar (ERROR_NOT_READY 21 etc), zera os primeiros 512
  bytes via `WriteFile` + `SetFilePointerEx` — 2 linhas, limpa MBR sem diskpart.
- **Libs prontas** (referência, não usadas): `MBW.Libraries.DeviceIOControlLib` (LordMike,
  baixada p/ conferir structs), VHDTool (mount/unmount VHDX).
- **Rust não acelera isto**: gargalo é IOCTL/COM, não CPU — o fast path já é nativo.
  Rust continua onde faz sentido (DeepUninstaller: hash/regex/scoring).

## Arquivos

- `KitLugia.Core\NativeDiskIo.cs` — camada nativa (P/Invoke + IOCTLs + parsing)
- `KitLugia.Core\PartitionManager.cs` — GetAllDisksNative, IsSystemDisk, CleanDisk fast path
- `C:\Users\Lugia\AppData\Local\Temp\opencode\DeviceIOControlLib-ref\` — lib de referência
- `C:\Users\Lugia\AppData\Local\Temp\opencode\rpi-imager-ref\diskpart_util.cpp` — cleanDiskFast
- Referências: learn.microsoft.com (IOCTL_DISK_GET_DRIVE_LAYOUT_EX, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, FSCTL_SHRINK_VOLUME), github.com/raspberrypi/rpi-imager, github.com/LordMike/MBW.Libraries.DeviceIOControlLib
