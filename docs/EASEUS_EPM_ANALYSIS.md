# EaseUS Partition Master 20.8 (build WinPE) — análise com IDA Pro

Alvo: `C:\Users\Lugia\Downloads\easeuswinpeepm\epm` (copiado para `%TEMP%\epm_analysis\targets`; os
originais foram lidos apenas, nada foi alterado). Ferramenta: **IDA Professional 9.0** (`idat.exe -A` +
IDAPython, scripts em `docs/ida_epm_dump.py` e `docs/epm_scan_imports.py`).

## 1. Como o EaseUS opera discos (e o que isso significa para o Kit)

| Camada | Mecanismo do EaseUS | KitLugia (antes) | KitLugia (agora) |
|---|---|---|---|
| Enumeração | `CDiskEnumerator` → **VDS/Storage Spaces** (log: `Storage space init`, `Vds loadservice`) | IOCTL nativo → Storage API → WMI legado | igual (rápido, sem spawn) |
| Redimensionar | **VDS COM**: `CWin32BootRepairManager::_vdsResize` → `CBasicDiskVolume::ReSize()` (recebe `IPartitionManager`) | diskpart | **Storage API `MSFT_Partition.Resize` → IOCTL nativo → diskpart** |
| Criar/estender part. de sistema | `CreateSystemTgt` → `_isGPT` → `_getEfiPart` + `ModifyPartEfiProp`; se não houver EFI/empty part → `ResizeMoveProc` | diskpart | idem (via Core novo) |
| Criar/atualizar partição | `IPartitionManager` (VDS) + `CWin32PartConvertManager` (`Mbr2GptProc`, `Gpt2MbrProc`, `CanConvertToMBR`) | diskpart (`convert gpt/mbr`) | diskpart (fallback) |
| BCD | objeto próprio sobre **bcdedit CLI** + **bcdboot** + **bootsect** | só bcdedit (cirurgico) | idem + **reparo completo** (novo) |
| Firmware UEFI | **NVRAM direto**: `Get/SetFirmwareEnvironmentVariableW` (`BootOrder`, `BootNext`, `Boot####`) | bcdedit `/set {fwbootmgr}` | idem |

**Conclusão**: o EaseUS **não usa diskpart para redimensionar** — usa VDS (COM), que é a mesma
infraestrutura do Gerenciador de Discos. A escada nova do Kit (`MSFT_Partition.Resize` → IOCTL
`GROW_PARTITION`/`EXTEND_VOLUME` → diskpart) é o equivalente moderno e moderno-equivalente: a
Storage Management API é a sucessora do VDS (o VDS está deprecado). Ou seja: **a direção que o EaseUS
usa é a mesma que o Kit passou a usar**, e o diskpart ficou só como rede de segurança.

## 2. O reparador de BCD (`BootRepair`)

Binários: `BootRepair.exe`, `Win32BootRepair.dll`, `Win32Bcd.dll`, `Win32UEFI.dll` + as ferramentas
da Microsoft **`bcdboot.exe`, `bcdedit.exe`, `bootsect.exe`** embarcadas na pasta.

### 2.1 Núcleo do reparo — `CBcdBootProc::TransferBootStaff` (IDA, `Win32BootRepair.dll:0x1800021A0`)

Pseudocódigo reconstruído do decompilado (log de erro original:
`d:\epm\main\code\bootrepairtool\bootrepair_func\win32bootrepair\consoleproc.cpp:163`):

```
TransferBootStaff(srcWinLetter /*a2*/, espLetter /*a3*/, isBios /*a4*/):
    cmd = sprintf(L"%s %C:\\windows /s %C:\\ /f %s", <pasta>\Bcdboot.exe, a2, a3, isBios ? L"BIOS" : L"UEFI")
    if (!run(cmd)) { log("CBcdBootProc::TransferBootStaff fail!"); return false; }
    if (isBios) {
        cmd2 = sprintf(L"%s /nt60 %C: /mbr", <pasta>\BootSect.exe, a3)   // grava boot record + MBR
        if (!run(cmd2)) return false;
    }
    return true;
```

Ou seja, o "reparo de BCD" deles é o **rebuild canônico da Microsoft**:
1. `bcdboot C:\Windows /s S:\ /f UEFI` (ou `/f BIOS`) — recria a loja BCD do zero a partir da instalação;
2. `bootsect /nt60 <volume do sistema> /mbr` — reescreve o código de boot (BIOS).

Não é mágica: é rodar as ferramentas certas na ordem certa. **O KitLugia hoje só faz cirurgia com
bcdedit** (`/create`, `/set`, `/displayorder`) e nunca reconstrói a loja — por isso uma BCD corrompida
não tem recuperação no kit.

### 2.2 Modelo de BCD (`Win32Bcd.dll`) — bcdedit como biblioteca

O `Win32Bcd.dll` embrulha o `bcdedit` num modelo de objetos (`CWin32BCDManager`, `CWin32BootMgrItem`,
`CWin32BootLoaderItem`, `CWin32DeviceItem`). Exemplo decompilado (`CWin32BootMgrItem::AddBootLoader`):

```
if (item.Type >= 3):                              // 3 = Windows Boot Loader
    run_bcdedit(L"/create %s /application osloader", guid)
    if (deviceOption) run_bcdedit(L"/create %s /device", guidDevice)
    LoadByParameter(item); SaveParameters(item); _addDisplayItem(item, isAddLast)
```

Outros exports revelam o escopo: `UpdateBootOrder`, `SetDefaultItem`, `DeleteBootLoader`,
`LoadByGuidString`, `EnumProc`, `InitByCurSystem`, `InitByBcdFile`, e o tratamento de
`partition=\Device\HarddiskVolume%d` / `ramdisksdidevice`.

**Isso é exatamente a estratégia do KitLugia** (`CreateEfiBootEntry`, `CreateDirectNvramBoot`,
`CreateRamdiskEntry`: `/create` + `/application` + `/device` + `/displayorder`). Ou seja: a cirurgia de
BCD do Kit está no caminho certo — faltava só o **rebuild**.

### 2.3 Firmware UEFI (`Win32UEFI.dll`) — NVRAM direto

Imports: `GetFirmwareEnvironmentVariableW`, `SetFirmwareEnvironmentVariableW`, `DeviceIoControl`.
Strings: `BootOrder`, `BootNext`, `Boot####`, `UEFI_ClearDevPaths`, `\EFI\MICROSOFT\BOOT\BOOTMGFW.EFI`.
Exports: `UEFI_UpdateBootOrder`, `UEFI_UpdateGptBootOption`, `UEFI_UpdateMbrBootOption`,
`UEFI_SetNextBootEntryId`, `UEFI_DeleteBootOption`, `UEFI_GetBootList`, `UEFI_GetBootDevices`,
`UEFI_GetBootCount`, `UEFI_SetNextBootintoFireware`, `UEFI_IsNextBootintoFireware`, `UEFI_isUEFIAvailable`.

Eles escrevem as variáveis de firmware da placa (`\Boot####`, `BootOrder`, `BootNext`) para ordenar,
apagar ou forçar o próximo boot — inclusive "bootear no setup" (`SetNextBootintoFireware`). O Kit usa
`bcdedit /set {fwbootmgr} bootsequence {guid}`, que cobre o `BootNext` mas não a manipulação direta de
`Boot####`.

### 2.4 Passo a passo do reparo completo (deduzido do fluxo)

1. `CreateSystemTgt`: garante que existe partição de sistema/EFI (`_isGPT` → `_getEfiPart` →
   `ModifyPartEfiProp`; senão cria/estende com `ResizeMoveProc`).
2. `MountSrcBootPart`: monta a letra da partição de boot (`MountDriverLetter`).
3. `TransferBootStaff`: `bcdboot` + `bootsect` (§2.1).
4. `AddRegProcItem`: ajustes de registro (via `sub_18000F930`, um gerenciador de registro próprio).
5. `DeleteTgtSysPart`: remove partições de sistema do disco alvo antes de instalar.
6. `CWin32BCDManager::InitByCurSystem` / `InitByBcdFile` + `EnumProc`: lê a BCD para a UI
   (`Info_BCDMenus`, `Info_UefiItemsFound`, `Click_ChooseBCD`).

## 3. O que foi bringe para o KitLugia

1. **`KitLugia.Core/BcdRepairManager.cs`** — diagnóstico + reparo no mesmo estilo:
   - `Diagnose()` → problemas reais: loja BCD ausente/corrompida, sem entrada de SO, boot files
     ausentes na ESP, `bootmgfw.efi` faltando, `Winload.efi` sem `BCD` correspondente, partição EFI
     ausente, firmware ≠ do modo do disco (GPT+BIOS).
   - `RepairRebuild()` → `bcdboot <Windows> /s <ESP> /f UEFI|BIOS` (+ `bootsect /nt60 <vol> /mbr` no BIOS),
     **com backup da loja BCD antes** e **verificação depois** (`bcdedit /enum` + presença dos arquivos).
   - `RepairBootFiles()` → recopia `\EFI\Microsoft\Boot\*` e o `BCD` quando os arquivos somem mas a
     loja está boa.
   - `SetNextBootToFirmware()` → equivalente ao `UEFI_SetNextBootintoFireware`.
2. **Nada disso substitui o que já funciona**: a cirurgia com `bcdedit` do Kit é a mesma do EaseUS e
   continua sendo usada (entradas do Kit Lugia/Linux/WinPE); o rebuild é a camada que faltava para o
   caso "BCD corrompida".

## 4. Reprodução

```bash
# pré-scan (imports + strings) — rápido, sem IDA
python docs/epm_scan_imports.py Win32BootRepair.dll

# IDA batch: decompila funções por nome e por xref de string
cd %TEMP%/epm_analysis
MSYS_NO_PATHCONV=1 "<IDA>\idat.exe" -A -S"ida_epm_dump.py out.txt (TransferBootStaff|vdsResize|Mbr2Gpt) (bcdboot|bootsect|diskpart) 10" \
    "C:\...\targets\Win32BootRepair.dll"
```

Quirks do IDA 9 ( reaproveitados): `ida_auto.auto_wait()` é obrigatório no início do script; apagar o
`.i64` antes de reanalisar o mesmo arquivo; `-S` precisa de aspas explícitas.
---

# Parte 2 — Disk Converter + "Built-in Toolkits" (prints da UI + IDA em `DiskConverter.dll`)

## 5. O que os prints da UI do EPM 20.8 revelam

| Aba/seção (print) | Operações |
|---|---|
| Partition Manager | `Drive (:)` / `Partition this disk` / `Format` / `Create` / `Wipe` — ações por *unallocated* ou por partição |
| Disk Clone | `Migrate OS` / `Clone OS Disk` / `Clone Data Disk` / `Clone Partition` |
| Disk Converter | `MBR=>GPT` (marcada *Safe* + *Fast*), `GPT=>MBR`, `Dynamic=>Basic`, `Basic=>Dynamic`, `FAT=>NTFS`, `NTFS=>FAT`, `FAT=>exFAT` |
| Built-in Toolkits | `Bad Sector Scan`, **`Rebuild MBR`**, `Write Protection`, `Low-Level Format`, `4K Alignment`, `Check File System` |
| System Utilities | `Windows Shell Command`, `Boot Repair`, `Password Reset`, `Retrieve BitLocker Password` |

O `DiskConverter.dll` (2,2 MB) confirma as operações internamente — os nomes das operações salvas no
log de operações são literais:

```
"Convert MBR to GPT"   "Convert GPT to MBR"
"Initialize to MBR disk"   "Initialize to GPT disk"
"Rebuild MBR"   "4K alignment"   "AlignDisk"
"Convert primary to logical partition" / "Convert logical to primary partition"
"Wiping partition data"   "Smart Resize"
```

## 6. Regras de validação do EaseUS (strings do binário — implementadas no Kit)

Coletadas com `docs/epm_scan_imports.py` sobre `DiskConverter.dll` (o núcleo nativo é
`CommonMbrGptConvert.cpp`, log `D:\EPM\Main\code\_EPM_main\mod.Native\CommonMbrGptConvert.cpp`):

| Mensagem do EPM | Regra | Onde está no Kit |
|---|---|---|
| `The disk is write protected.` | disco/partição somente leitura | `ConvertPlan.Blockers` |
| `Please apply pending operations and then perform conversion.` | não converter com fila pendente | `ConvertAsync` opera direto (sem fila) |
| `Cannot convert the dynamic disk to MBR disk.` / `...to GPT disk.` | disco dinâmico (LDM) não é convertível | detecta partição LDM (GPT GUID `5808c8aa…`/`af9b60a0…`, MBR `0x42`) |
| `Cannot convert PE system disk to GPT disk.` | não converter o disco do WinPE em execução | `IsWinPeSession()` (chave `MiniNT`) + letra do volume em uso |
| `Cannot convert the 32-bit system disk to GPT disk.` | Windows 32 bits não boota de GPT | `Environment.Is64BitOperatingSystem` |
| `The selected GPT disk cannot be converted to MBR disk, because it contains the partition larger than %1.` | MBR endereça no máximo 2 TiB | `MbrMaxPartitionBytes` |
| `At least 1M unallocated space is required in the end of disk…` | 1 MiB livres no fim do disco para o GPT backup header | `TailFreeBytes < 1 MiB` |
| `Not enough free space. Please reserve more free space or defragment disk…` | falta espaço útil para mover/resizar | (aplica-se ao resize, já tratado pelo `GetSupportedSize`) |
| `Unable to perform conversion. Please resize the partition (Letter/FS/Size) manually and retry.` | partição incompatível com o destino | reportado como blocker/aviso |
| `The conversion task cannot be performed because a partition cannot be created or resized!` | precisa de partição intermediária | relatório |
| `Failed to generate the BCD file during MBR to GPT conversion.` / `Failed to generate the UEFI file during…` | depois de converter, o boot é refeito | o Kit **cruza com `BcdRepairManager`** (bcdboot) |
| `The cluster size of the system disk exceeds 4 KB.` | alinhamento 4K incompatível com cluster > 4 KB | `AlignmentReport` (diagnóstico) |

IDA (`DiskConverter.dll`, nomes Qt mangled): `DiskConverter::isMBR2GPTValid` @ `0x1800740F0` e
`isGPT2MBRValid` @ `0x180073A70` são exatamente esses checklists; `doExec` @ `0x1800731C0` orquestra
(`addOperation` → valida → executa → `recordOperationInfo`), com `MBR_TO_GPT` na tabela
(`0x1800B4450`).

## 7. O que foi implementado no KitLugia (`DiskConverterManager`)

**`KitLugia.Core/DiskConverterManager.cs` (NOVO)** — a peça que faltava no motor de disco:

1. `GetDisks()` / `GetPartitions()` — `MSFT_Disk` + `MSFT_Partition` (estilo, flags, assinatura/GUID, offsets).
2. `Analyze(disk, target)` — **todas** as verificações da tabela acima, em leitura pura.
3. `ConvertAsync()` — escada `MSFT_Disk.ConvertStyle(1|2)` (API oficial, **in-place**, partições e dados
   preservados) → fallback `diskpart convert`. **Verificação obrigatória no final**: estilo novo,
   assinatura/GUID e contagem de partições (conversão que descarta partição é dita no relatório).
4. `InitializeAsync()` — `MSFT_Disk.Initialize(1|2)` para disco RAW (`Initialize to MBR/GPT disk`).
5. `ReadMbr()` / `BackupMbrAsync()` / `RestoreMbrAsync()` — `Rebuild MBR` em modo seguro: backup dos
   512 bytes em `%LOCALAPPDATA%\KitLugia\MBR-Backup\<data>` (com SHA256 + `.txt`), e na restauração
   o padrão é **copiar só os 446 bytes de código de boot e manter a tabela de partições atual** —
   que é o que recupera a inicialização sem mexer no layout. Escrita com `Flush(flushToDisk: true)`
   e **leitura de volta comparada byte a byte**.
6. `AnalyzeAlignment()` — `4K alignment`: início de cada partição contra 1 MiB e 4 KiB
   (diagnóstico honesto: reposicionar uma partição existente exige mover os dados).

**`KitLugia.GUI/Pages/DiskConverterPage.xaml(.cs)` (NOVO)** — página no menu, ao lado de
"Reparar Boot/BCD": seletor de disco, **ANALISAR** (mostra os dois sentidos GPT e MBR com ✔/✖/⚠),
os botões de conversão/inicialização, as ferramentas de MBR e o diagnóstico de alinhamento.
A conversão para GPT em disco de sistema termina com o lembrete de rodar o `Reparar Boot/BCD`
(é exatamente o `Failed to generate the BCD file during MBR to GPT conversion` do EaseUS).

**Correção no `PartitionManager`**: `ConvertDiskStyle()` exigia disco **vazio** e recusava o disco do
sistema (`"A conversão MBR/GPT requer que o disco esteja completamente vazio"`). Agora delega ao
`DiskConverterManager` — que converte com as partições no lugar, como o EaseUS e o Windows 11 fazem.
`RunDiskpartScript` passou a `internal` (fallback) e ganhou `RunDiskpartAsync(disk, gpt|mbr)`.

Validado no host (leitura pura): disco 0 (465 GB, GPT) → `podeConverter` para MBR = **true**;
disco 1 (3,7 TB, sistema/UEFI) → 3 bloqueios corretos para MBR (UEFI sem GRUB, 5 partições > 4,
C: > 2 TiB) e alinhamento real detectado (4 de 5 partições fora de 1 MiB).
