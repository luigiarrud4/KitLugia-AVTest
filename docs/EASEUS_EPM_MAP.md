# EaseUS Partition Master 20.8 (WinPE) — MAPA COMPLETO da arquitetura

> **Este documento existe para não repetir a análise.** Tudo aqui foi extraído de forma
> determinística por scripts versionados (seção 10). Se precisar reverificar algo, rode os
> scripts — não precisa de novo reversing.

**Alvo:** `C:\Users\Lugia\Downloads\easeuswinpeepm\epm` (cópias de trabalho em
`%TEMP%\epm_analysis\targets`; os originais foram apenas lidos).
**Ferramentas:** IDA Professional 9.0 (`idat.exe -A` + IDAPython), `pefile`.
**Escopo:** 41 módulos, **3.051 exports**, **177 caminhos de fonte vazados** em 37 diretórios.

Complementa [`EASEUS_EPM_ANALYSIS.md`](EASEUS_EPM_ANALYSIS.md) (análise narrativa do reparo de
BCD e do Disk Converter). Este aqui é o mapa de referência.

---

## 1. Sumário: o que o EPM é, por dentro

O EPM **não é um programa que chama ferramentas**. É um motor de disco próprio (~30 MB de
código nativo) onde `diskpart` e `bcdedit` são apenas **também** opções de fallback:

| Camada | Mecanismo | Evidência |
|---|---|---|
| Enumeração de disco | **VDS COM** (`IVdsDispatcher`/`IVdsVolume`/`IVdsPartition`) | GUID de `IVdsDispatcher` presente em 11 módulos |
| Leitura/escrita de volume | **IOCTL próprio** (`CHardDriveIO`: `OpenDisk`/`ReadSector`/`WriteSector`/`VerifySector`/`GetGeometryInfo`) | export em `Device.mo`, `NtfsResizeMove.mo`, `PartitionManager.dll` |
| Redimensionar/mover volume | motor NTFS/FAT **próprio** (§7) | 60 arquivos-fonte em `mod.ntfsutil` |
| Criar/excluir partição | VDS + `DeleteDriveLayout` | `CLegacyPublic::DeleteDriveLayout` |
| MBR↔GPT | `CommonMbrGptConvert.cpp` + regeneração de UEFI (§5) | `CConvMBR2GPTController` |
| BCD | **biblioteca BCD binária própria** + `bcdboot`/`bootsect` embarcados (§6) | `Bcd_Init`, `LockBcdFile`, `BCD00000000\Objects\...` |
| Firmware UEFI | **NVRAM direto** (`Get/SetFirmwareEnvironmentVariableW`) | 17 exports `UEFI_*` |
| `diskpart` | **só** para 4 coisas estreitas (§3.3) | 5 módulos, todos com script literal |
| `chkdsk` | embarcado (`WinChkdsk.exe`) | `ToolBox::RunToolProgram` |

**Consequência prática:** o `PartitionManager` do KitLugia (Storage Management API → IOCTL →
diskpart) é o equivalente **moderno** disso. A Storage Management API é a sucessora do VDS.
O EPM usaria VDS porque é um produto WinPE que roda em Windows XP/7.

---

## 2. Os 41 módulos

### 2.1 Núcleo (`bin/`)

| Módulo | Tamanho | Papel (pelos exports) |
|---|---|---|
| `PartitionManager.dll` | 7,5 MB | Orquestrador. `PartitionManager` (11 classes), `PartitionManager::AddMBR2GPTOperation`, `AddGPT2MBROperation`, `DoExpandBoot`, `DoWin11Checker`, `DoExplore`, `DoReboot` |
| `ToolBox.dll` | 2,4 MB | **Built-in Toolkits**: `doRebuild`/`doRebuildRun` (Rebuild MBR), `do4kAlign`/`do4kAlignRun`/`check4kAlign`, `doWriteProtected`/`initWPProcess`/`clearWPProcess`/`isDriverWritable`, `getBadSectors`, `doBootRepair`, `doPasswordClear` |
| `DiskConverter.dll` | 2,2 MB | `DiskConverter`: `isMBR2GPTValid`, `isGPT2MBRValid`, `checkCanConvert`, `CheckBootPart`, `CheckFileSystemPartExtendInfo`, `doDynamicToBasic`, `ShowMBR2GPTWarningDlg`, `doExec` |
| `NTFSUtil.mo` | 896 KB | Fachada do motor NTFS: `NTFSUtilResizeMoveVolume`, `NTFSUtilClusterResize`, `NTFSUtilConvertToFat`, `NTFSUtilCopyVolume(2)`, `NTFSUtilMergeVolume`, `NTFSUtilDisableVolumeIO`/`EnableVolumeIO`/`FlushVolume`, `NTFSUtilCalcUsedSizeWhenSectorSizeChanged` |
| `LdmManager.mo` | 800 KB | Disco dinâmico (LDM): `found ESP partition in GPT`, `found MSR partition in GPT`, `found OEM partition in MBR`, `Disk%d Cannot find VMDB signature` |
| `MergePartition.mo` | — | `EUMerge::MergePartition`, `ResizeFATPartition`, `ResizeNtfsPartition`, `CopyFileForWin32` |
| `PartitionRecovery.dll` | 2,2 MB | Motor de recuperação de partição (veja §9) |
| `RecoveryModule.dll` | 1,5 MB | UI/fluxo de recovery |
| `RawFixer.dll` | 1,0 MB | Reparo de sistema de arquivos (§8.3) |
| `Clone.dll` / `CloneModule.dll` | 3,6 / 2,4 MB | `CEUCloneMgr`, `CEUPreOsMgr`, clone de disco/partição (§8.1) |
| `RecoveryPartitionFixer.mo` | 733 KB | WinRE: `RecoveryPartitionFixer.cpp`, `Reagent.h`, `DiskPart.cpp` |
| `BitLockerLib.dll` | 738 KB | **BitLocker completo in-house** (§9.2) |
| `Discovery.dll` | 2,0 MB | Geometria CHS legada + orquestração de clone (§8.4) |
| `ToolsModule.dll` | 2,7 MB | Fila de operações pendentes: `CChangeLetterOperation`, `CDeletePartitionOperation`, `CDelAllPartitionOperation` (todas com `IsValidOperation`/`ModifyManagerItem`/`IsRequireReboot`) |
| `Device.mo` / `DeviceManager.mo` | 688 / 734 KB | Enumeração; `CUpdateSystemBootPartition::GetGptBCDFile`, `SetGptSystemAndBootPart` |
| `NTFSLib.mo` | 210 KB | Leitura de baixo nível de NTFS: `CNtfsMFT`, `CNtfsVolume`, `CNtfsRunList`, `CNtfsFileRecord(Attribute)`, `CNtfsMultiFileRecord`, `CNtfsAttributeList`, `TBitmap`, `ChunkCache` |
| `NtfsResizeMove.mo` / `ResizeNTFS.mo` | 201 / 240 KB | Resize offline (variante "rápida"): `NTFSRESIZEMOVE`, `NtfsFastResizeMove`, `ResizeNTFSVolume`, `CNtfsMFT::Get0XB0DataRun` |
| `NTFSCopy.mo` | 207 KB | Cópia de volume NTFS: `ExtendMFT.cpp`, `TargetVolume.cpp`, `CopyNTFSVolume.cpp` |
| `FatCopy.mo` | 146 KB | `FatCopy.cpp`, `FatResizeMoveArithmetic.cpp`, `ResizeFatX.cpp` |
| `SectorCopy.mo` | 62 KB | `CSectorCopyModule` — cópia bruta por setor |
| `LLFProc.mo` | 87 KB | Low-Level Format: `GetLLFProc`, `MayBeSupportUEFI`, `ReleaseLLFProc` |
| `EFIBoot.mo` | 74 KB | `GetModule`; suporte a boot EFI |
| `Fixup.mo` | 85 KB | Correções pós-boot |
| `Win32Bcd.dll` | 289 KB | bcdedit embrulhado (§6.2) |
| `Win32BootRepair.dll` | 342 KB | `CWin32BootRepairManager`, `CWin32PartConvertManager` (§6.1) |
| `Win32UEFI.dll` | 178 KB | NVRAM (§6.3) |
| `LegacyPublic.dll` | 284 KB | **`CLegacyPublic`: 120+ funções de decisão** (§4) |
| `VhdVmdk.dll` | 272 KB | `vmdk2.cpp`, `readwriteinterface.cpp` |
| `EUCloneClient.dll` | 219 KB | Cliente de clone (pipe): `CloneClient.cpp`, `Pipe.cpp` |
| `EPMUI.exe` | 3,7 MB | Qt5 GUI |
| `MainFrame.dll` / `PublicLoader.dll` | 293 / 303 KB | Shell da UI |

### 2.2 Ferramentas embarcadas (`bin/`)

`bcdboot.exe`, `bcdedit.exe`, `bootsect.exe`, `WinChkdsk.exe`, `ConvertFat2NTFS.exe`,
`WriteProtect.exe`, `AddDrivers.exe`, `GetDriver.exe`, `spawn.exe`, `firebasefetch.exe`.

> **Ação para o Kit:** o `BcdRepairManager.FindTool()` já procura em
> `AppContext.BaseDirectory\Tools\BootRepair` — `bcdboot`/`bootsect` precisam ser **embutidos
> no pacote** para funcionar no WinPE. É a mesma lacuna que o EaseUS resolveu embarcando.

### 2.3 `BootRepair/` (cópia stand-alone do motor)

Réplica do núcleo para o modo WinPE: `BootRepair.exe` + 30 `.mo`/`.dll` (inclui `Win32UEFI.dll`,
`Win32Bcd.dll`, `LegacyPublic.dll`, `bcdboot/bootsect/bcdedit`).
**Achado:** `BootRepair/NTFSUtil.mo` é o **mesmo** módulo de `bin/` (mesmos 25 caminhos de
fonte, mesmo `NTFSUtil.pdb`) — ou seja, o motor de resize NTFS é compartilhado entre a UI e o
reparador de boot.

---

## 3. Onde o EPM usa `diskpart` (e onde NÃO usa)

### 3.1 Resposta curta

**diskpart aparece em 5 módulos, sempre com script literal.** Não é o motor de resize.

### 3.2 Todas as ocorrências

| Módulo | String literal | Para quê |
|---|---|---|
| `WriteProtect.exe` | `diskpart.exe /s %1` + script `select disk %1` / `attributes disk %2 readonly` | **A ferramenta "Write Protection" É o diskpart.** |
| `PartitionManager.dll` | `cmd /c echo select disk %1 ^& echo online disk ^& echo attributes disk clear readonly \| diskpart` | Tirar o flag read-only antes de operar |
| `RecoveryPartitionFixer.mo` | `diskpart /s diskpart_script.txt`, `diskpart /s set_recovery_partition.txt`, `=diskpart /s diskpart_script.txt` | Recriar/ajustar a partição WinRE (`create partition PRIMARY size =`) |
| `CloneModule.dll` | `cmd.exe /c diskpart /s "%s"` | Fluxo de clone (`select disk %lu`) |
| `BootRepair.exe` | `diskpart.exe /s "%1"` + `select disk %1`, `select partition %2` | Fluxo de reparo |

### 3.3 Conclusões acionáveis

1. **"Write Protection" = `attributes disk N readonly`** — e o Kit pode fazer o mesmo via
   `IOCTL_DISK_SET_ATTRIBUTES`, sem spawn de processo.
2. **Antes de qualquer operação, eles limpam o read-only** — o Kit deve fazer o mesmo
   (hoje o `DiskConverterManager` só *reporta* disco read-only como bloqueio).
3. **Nenhum uso de diskpart para resize.** Confirma §1.

---

## 4. `CLegacyPublic` — o motor de decisão (215 exports)

`LegacyPublic.dll` é onde vive quase toda a lógica "¿esto é possível?". Vale colar a lista,
porque é o catálogo de regras que o Kit pode espelhar:

**Boot/GPT:** `IsSupportGPTDisk`, `IsGPTUnsupported`, `IsGPTBootPartition`, `IsGPTSystemPartition`,
`IsGPTRecoveryPartition`, `IsGrubBoot`, `IsBootDisk`, `IsSystemAndBootDisk`, `IsOnlySystemDisk`,
`IsBeyond8TB`, `IsBeyondMaxPartSizeForMBR`, `IsMbrDiskHasMaxPrimaryPartition`, `IsFirstAndLogicalPartition`,
`IsGrubBoot`, `IsBootRelativeDisk`.

**Sistema:** `IsWinXPAnd32Bit`, `IsWin7SP1System`, `IsWindows10_1709Later`, `IsSystemX64`,
`Is64Proc`, `IsServer`, `GetOsVersionNumbers`, `GetOsVersionInfo`, `IsServer`.

**Volumes/FS:** `GetNTFSMaxVolSize`, `GetNTFSMinAndMaxClusterSize`, `GetNTFSMinimumClusterSize`,
`GetFatMinAndMaxClusterSize`, `GetMatchingClusterSize`, `GetExFatMaxVolSize`, `GetExt4MaxVolSize`,
`GetExtMinSizeWhenBlockChange`, `IsSupportResizeFsType`, `GetFileSystem(Type)`, `IsExtendNTFSVolume`.

**Dinâmico/LDM:** `IsOSSupportDynamicDisk`, `GetDynamicRange`, `GetDynamicVolumeNumberAndPercent`,
`GetDynamicVolUsedSector`, `GetDynamicRMoveParameter(AtBootPart)`, `IsSameDynmaicVolume`.

**Segurança/estado:** `IsBitLockerPart`, `IsVolumeReadOnly`, `IsVolumeOffline`, `DiskCanLocked`,
`PartitionPationCanLocked`, `IsCurrentSystemOnVHD`, `IsVHDOnDisk`, `IsVHDOnPartition`,
`IsStorageSpace`, `HasBitLockerPart`, `IsOverHeatMerge`.

**Infra:** `CAsynLockVolume` (`Lock`/`UnLock`/`DisMount` com `MAX_RETRY_TIMES`/`LOCK_RETRY_TIMES`
e thread dedicada), `CDevNotifyManager` (`BeginMonitor`/`NotifyDevChange`/`NeedRefresh` —
monitora troca de disco via callback de dispositivo).

> `CAsynLockVolume` é interessante: **tenta bloquear o volume várias vezes em thread separada**
> antes de desistir. O Kit hoje faz uma tentativa só. Uma partição com file handle aberto
> é a causa nº1 de "extend falhou"; o retry assíncrono é uma melhoria barata e real.

---

## 5. Conversão MBR↔GPT

Fonte: `mod.native\CommonMbrGptConvert.cpp` (presente em 10 módulos).
IDA: `DiskConverter::isMBR2GPTValid` @ `0x1800740F0`, `isGPT2MBRValid` @ `0x180073A70`,
`doExec` @ `0x1800731C0`.

Checklist de bloqueio (strings literais — todas já implementadas no `DiskConverterManager`):

```
The disk is write protected.
Cannot convert the dynamic disk to MBR disk. / ...to GPT disk.
Cannot convert PE system disk to GPT disk.
Cannot convert the 32-bit system disk to GPT disk.
The selected GPT disk cannot be converted to MBR disk, because it contains the partition larger than %1.
At least 1M unallocated space is required in the end of disk, you can shrink the latest volume and try again.
Not enough free space. Please reserve more free space or defragment disk...
Unable to perform conversion. Please resize the partition (Letter/FS/Size) manually and retry.
The conversion task cannot be performed because a partition cannot be created or resized!
The cluster size of the system disk exceeds 4 KB.
Failed to generate the BCD file during MBR to GPT conversion.
Failed to generate the UEFI file during MBR to GPT conversion.
```

**Ponto-chave:** as duas últimas provam que **depois de converter, eles regeneram o boot**
(`CGenUEFISystemFile` em `genuefisystemfile.cpp`, também `mod.native`).
No Kit isso é a ponte `DiskConverterPage` → `BcdRepairManager`, que já existe.

**`genuefisystemfile.cpp`** — o que ele gera: `%s\EFI\Microsoft\Boot\bootmgfw.efi`,
`%s\Boot\BCD`, `%s\TempBCD`, `EFI System Partition`, `BOOTMGR is missing`,
`BOOTMGR is compressed`, `Boot:%S %S %d EFI not support in image`.

---

## 6. Boot / BCD / UEFI

### 6.1 Fluxo do reparo (`Win32BootRepair.dll`)

Fontes: `consoleproc.cpp`, `win32bootrepair.cpp`.
Classes: `CWin32BootRepairManager` (`CreateSystemTgt`, `MountSrcBootPart`, `ModifyPartEfiProp`,
`ResizeMoveProc`, `PartCreationProc`, `DisMountProc`, `FormateProc`, `AddRegProcItem`,
`DeleteTgtSysPart`, `RegCleanupProc`, `EndBcdProcess`, `StartBcdProcess`),
`CWin32PartConvertManager` (`CanConvertToGPT`, `CanConvertToMBR`, `Mbr2GptProc`,
`Gpt2MbrProc`, `ConvertDriverLayOutToGPT`, `ConvertDriverLayOutToMBR`, `_findEmptyPart`, `_isGPT`).

Núcleo (IDA `Win32BootRepair.dll:0x1800021A0`, log original
`d:\epm\main\code\bootrepairtool\bootrepair_func\win32bootrepair\consoleproc.cpp:163`):

```
CBcdBootProc::TransferBootStaff(srcWinLetter, espLetter, isBios):
    Bcdboot.exe "%C:\windows" /s %ESP%: /f UEFI|BIOS
    if isBios: BootSect.exe /nt60 %Sistema%: /mbr
```

Strings confirmadas: `\Bcdboot.exe`, `\BootSect.exe`, `%s /nt60 %C: /mbr`.
→ **O reparo de BCD do EaseUS = rodar as ferramentas da Microsoft na ordem certa.**
Isto já está implementado no `BcdRepairManager.RebuildAsync`.

### 6.2 Biblioteca BCD binária própria

Achado **maior** desta seção: o EPM **não** depende do `bcdedit` para ler/escrever a BCD.
Ele tem um parser do formato binário da BCD (hive de registro):

```
Bcd_Init / Bcd_UnloadLibrary
%s:Lock bcd file faulty on Win32 platform!
%s:Unload bcd file faulty on Win32 platform!
%s:Failed to initialize bcd!
BCD00000000\Objects\%s\Elements\11000001
BCD00000000\Objects\{9dea862c-5cdd-4e70-acc1-f32b344d4795}\Elements\24000001
BCDOBJECT={9dea862c-5cdd-4e70-acc1-f32b344d4795}
```

Com **lock de arquivo** (`LockBcdFile`/`UnlockBcdFile`, incluso o bug de 8 bytes de flags).
Classes (`Win32Bcd.dll`, 93 exports): `CWin32BCDManager` (`InitByCurSystem`, `InitByBcdFile`,
`EnumProc`, `GetBcdeditProc`), `CWin32BootMgrItem` (`AddBootLoader`, `DeleteBootLoader`,
`UpdateBootOrder`, `SetDefaultItem`, `ResetContend`, `_addDisplayItem`), `CWin32BootLoaderItem`
(`LoadByParameter`, `LoadByString`, `SaveParameters`, `GetWimDeviceOption`),
`CWin32DeviceItem` (`LoadByGuidString`).

> **Para o Kit:** manter `bcdedit` (mais seguro e o Windows o suporta sempre) é a escolha
> certa. Mas fica registrado que o caminho mais rápido/robusto existe — e que o
> `Win32Bcd.dll` faz exatamente o que o `CreateEfiBootEntry` do `WinbootManager` faz.

### 6.3 NVRAM direto (`Win32UEFI.dll`, 17 exports)

```
UEFI_Init / UEFI_UnInit / UEFI_isUEFIAvailable
UEFI_UpdateBootOrder / UEFI_UpdateGptBootOption / UEFI_UpdateMbrBootOption
UEFI_SetNextBootEntryId / UEFI_GetNextBootEntryId / UEFI_DeleteBootOption
UEFI_GetBootList / UEFI_GetBootDevices / UEFI_GetBootCount / UEFI_GetDevPathByOption
UEFI_SetNextBootintoFireware / UEFI_IsNextBootintoFireware   <-- "bootar no setup"
UEFI_ClearDevPaths
```

Strings: `BootOrder`, `BootNext`, `Boot####`, `2600FirmwareVariable`,
`\EFI\MICROSOFT\BOOT\BOOTMGFW.EFI`.
Imports: `GetFirmwareEnvironmentVariableW`, `SetFirmwareEnvironmentVariableW`, `DeviceIoControl`.

> O Kit usa `bcdedit /set {fwbootmgr} bootsequence {guid}` (cobre `BootNext`). **Falta**:
> `UEFI_ClearDevPaths` e o "bootar no setup" (`BootNext=UEFI\...` com o caminho do firmware).
> O `BcdRepairManager` tem `DetectFirmwareMode`, `DiagnoseAsync`, `RebuildAsync`,
> `RestoreBootFilesAsync`, `VerifyAsync`, `BackupBcdStore`, `FindEspPartition`,
> `ResolveWindowsLetter`, `FindTool` — **não tem** equivalente a `UEFI_SetNextBootintoFireware`.

---

## 7. O motor de resize NTFS (o coração do produto)

### 7.1 Os 25 arquivos-fonte de `mod.ntfsutil`

Revelados por `__FILE__` nos asserts. Este é o **desenho completo** do algoritmo:

```
AttributeParser.cpp               NTFSAnalyze.cpp            NTFSResizeMove.cpp
CNTFSClusterResize.cpp            NTFSCopy.cpp               NTFSUtil.cpp
CNTFSClusterResizePreProc.cpp     NTFSMerge.cpp              NtfsItemProc.cpp
Decompress.cpp                    NTFSAlign.cpp              PartitionUtilNTFS.cpp
FirstVolumeMergeDeal.cpp          ResizeMoveAlgorithm.cpp    VolumeInfo.cpp
HardDriveIOEx.cpp                 ResizeMoveAlgorithmCreator.cpp
NewVolumeMergeDeal.cpp            ResizeMoveByRebuildingBitmap.cpp
NewVolumeResizeMoveDeal.cpp       ResizeMoveByRebuildingMFT.cpp
OldVolumeResizeMoveDeal.cpp       SecondVolumeMergeDeal.cpp
VolumeMergeDeal.cpp               VolumeResizeMoveDeal.cpp
```

### 7.2 As duas estratégias de resize

**`ResizeMoveByRebuildingBitmap.cpp` / `ResizeMoveByRebuildingMFT.cpp`** — são duas
estratégias distintas de shrink. Isso confirma o que a documentação do Windows diz: para
**encolher**, o caminho barato é reconstruir o `$Bitmap` — os clusters depois do limite são
liberados; para **crescer/mover**, é preciso **reconstruir o `$MFT`** (os data runs de cada
arquivo precisam ser reescritos).

`ResizeMoveAlgorithmCreator.cpp` escolhe entre os dois → é um **Strategy pattern**.
`NtfsFastResizeMove` / `CNtfsMFT::Get0XB0DataRun` são o atalho "sem mover nada" (usado quando
o shrink é só do fim).

### 7.3 A aula de MFT (`CNtfsMFT`, `NTFSLib.mo`)

```
InitMFT / IsInitiated / AdjustMftPos / ClearCache / Has0X20 / GetMFTStartLcn / GetMftRunList
Get0XB0FileRecord / Get0XB0DataRun / Get0XB0AllocSize / GetDataFromAttribute
GetFileRecordCount / GetNtfsVolume
CNtfsFileRecord: InitFromSourceFileRecord / Convert2SourceFileRecord / FixupFileRecord /
                 UnfixupFileRecord / CheckFileRecordMagic / DeleteAttribute
CNtfsRunList: AddSourceDataRun / InsertDataRunRange / RemoveDataRunRange / Split2MultiRunList
CNtfsVolume: GetSectorsPerCluster / GetBytesPerFileRecord / GetSectorsPerIndexRecord /
             GetMftLcnAddress / GetMftMirrorLcnAddress / GetSectorAddressFromLCN
```

`$B0` é o atributo **`$DATA` não-residente do próprio $MFT** — é ele que guarda onde os
registros do MFT estão. `Has0X20` = registro com atributo `$20` (ATAL/índice).
`CNtfsMultiFileRecord` = registro base + extensão (`$ATTRIBUTE_LIST`), o caso dos arquivos
grandes.

### 7.4 Deal = fase da operação

`Old/New/SecondVolumeMergeDeal`, `Old/NewVolumeResizeMoveDeal`, `VolumeResizeMoveDeal` —
padrão **Chain of Responsibility**: cada "deal" encapsula uma etapa e passa a próxima.
`NTFSClusterResizePreProc` é o pré-requisito (checa things antes de começar).

**Fases em texto** (extraídas de `sub_1800280D0`, o `switch` de progresso do ToolBox):

```
Creating partition → Formatting partition → Setting active → Resizing/Moving partition →
Hiding partition → Deleting partition → Setting label → Changing drive letter →
Deleting all partitions → Calculating sectors to move → Moving data →
Updating file records → Writing metadata → Updating system information... →
Locking volume / Unlocking volume → Creating volume / Deleting volume →
Rebuild MBR → 4K alignment → Convert MBR to GPT / GPT to MBR → Merge partition →
Allocate space → Quick partition → Adjust disk layout → Change cluster size →
Repair RAID5 → Turn off BitLocker drive → Repair partition table
```

### 7.5 Endpoints exportados (assinatura real)

```c
// NTFSUtil.mo
unsigned long __cdecl NTFSUtilResizeMoveVolume(callback, void*, IManagerItem*,
                                               IBlockDevice*, long long, ..., int);
unsigned long __cdecl NTFSUtilCopyVolume (callback, void*, IManagerItem*, IBlockDevice*, ...);
unsigned long __cdecl NTFSUtilMergeVolume(callback, void*, IManagerItem*, IBlockDevice*, ...);
unsigned long __cdecl NTFSUtilClusterResize(callback, tagOC_PARAM*, void*, ..., IManagerItem*, ...);
bool          __cdecl NTFSUtilConvertToFat(callback, void*, IManagerItem*, int);
unsigned long __cdecl NTFSUtilCalcUsedSizeWhenSectorSizeChanged(IBlockDevice*, int, ...);
```

O callback é o `CCallBackOperate` (progresso/cancelamento).

### 7.6 `IfMustUseNTFSUtilCopy`

Função que decide: "nesta partição, o motor genérico de cópia serve, ou é obrigatório usar
o motor de cópia NTFS dedicado?". Checagem **antes** de operar.

---

## 8. Clone / Migrate OS

### 8.1 `CEUCloneMgr` (`CloneModule.dll`, 124 exports)

```
GetBootPart / GetSystemPart / GetBootPartitionGUID / GetSystemPartitionGUID
GetBootDrvLetterInPE / GetDefBootLoaderInNtldr / GetDefBootLoaderInNtldr
GetSavedCloneParameter / GetSavedDiskCloneParameter / GetSavedPartCloneParameter
ConvertPartInfo / ConvertPartInfoByDisk / AddVmdkDisk / AftercareProc / CanMigrateOSDo
```

`CBcdBootProc::TransferBootStaff` e `CBcdEditProc::{GetCurrentBootLoader,
GetCurDefaultBootLoader, IsIdentifierExist}` — ou seja, **o clone também refaz o boot do disco
destino** (mesmo caminho do reparo).

`CEUPreOsMgr`: `SetISOPath`, `DelBootEntry`, `IsAlreadyDone`, `GetBootPart`, `GetSystemPart`
→ é o **pré-OS do WinPE** (roda antes do Windows, após o reboot).

### 8.2 `Clone` (`Clone.dll`, 152 exports)

```
DiskCloneSourceValid / DiskCloneTargetValid / CheckDiskinValid / CompareSectorSize
CompareSize / HasDiskConvert / AddDiskConvertOperation / IfCanCreateLogicalPartition
GetActiveSource / GetSavedDiskCloneParameter / GetLogicErrorInfo
```

Fluxo em 3 operações: `CloneDiskInitialization` → `CloneDisk` → `CloneDiskFinalization`.

**Política de tamanho** (mensagens literais):
```
Destination disk is too small, please select an equal-sized or bigger disk
Unable to migrate OS as the new disk space is not equal to or bigger than that of OS/boot partition.
4k disk cannot be selected as target.
Clone failed, there may be bad clusters on operating partition.
FAT clone unknown error
No enough free space for current partition clone.
```

### 8.3 `RawFixer.dll` — o "Bad Sector Scan" **não é** teste de superfície

Este é o achado que mais surpreendeu. O módulo se chama "RawFixer" e as funções são:

```
CRawFixer::Start / GetErrorInfo
NTFSFixer::FixByScanSector
FATFixer::FixByScanSector
ExFATFixer::FixByScanSector / ExFATFixer::CopySectors
RawInfoCollector
```

Fontes: `rawfixer.cpp`, `ntfsfixer.cpp`, `fatfixer.cpp`, `exfatfixer.cpp`,
`rawinfocollector.cpp`, `rawfixerlog.cpp`.

Ou seja: **o "Bad Sector Scan" varre os setores para achar clusters ruins e CONSERTA o
metadado do sistema de arquivos** (não é só mapear). Um teste de superfície puro seria mais
simples e não precisaria de um parser de FS.
Extra: tem **log retomável** (`Parse resumable copy record failed`, `Source object has been
modified since last copy`, `Copy record is invalid, it has been modified.`).

### 8.4 `Discovery.dll` — geometria CHS legada

```
DISK_CONFIG DiskSig:%u, Cylinders:%d, Heads:%d, SectorsPerTrack:%d, TotalSectors:%I64d
Batch  : Cylinder:%d Head:%d SectorPerTrack:%d Total:%I64d
Current: Cylinder:%d Head:%d SectorPerTrack:%d Total:%d
```

É a compatibilidade com geometria CHS (necessária para ler tabelas MBR antigas e para
alinhamento por head). Também tem `CProgressMgr`, `CCloneController`, `CBitLockerThread`.

### 8.5 `SectorCopy.mo` / `NTFSCopy.mo`

Cópia bruta por setor (`CSectorCopyModule`) e cópia com Tailwind Merge (`ExtendMFT.cpp`,
`TargetVolume.cpp`, log `[NtfsCopy] CopyClusters, Write Sector failed!`).

---

## 9. BitLocker, Recovery e outros

### 9.1 `BitLockerLib.dll` — BitLocker inteiro, in-house

```
Encrypt/Decrypt, LockVolume, UnlockVolumeAndRefresh
AesEncryptSectors / AesDecryptSectors / ReadRawData / WriteRawData
CreateBitLockerFveControl / GenerateKeys / GetFullVolumeEncryptionKey
GetProtectors / SetProcessProtectorKey / SetProcessProtectorKeyFromBEK
GetAutoUnlockKey / BuildMetadataInformation / BuildMetadataEOWInformation
BackupBootSectors / AdjustInformationOffset / GetInformationOffset
PercentageEncrypted / IsBitLockerEncrypted / IsBitLockerLocked
```

Strings de metadados FVE: `FVE-EOWBM`, `FVE-EOWBMR`, `FVE-EOWBR`.
Usa **Crypto++** (`f:\study\cryptopp890`) para AES.
Interage com o Windows por: WMI `System.Volume.BitLockerProtection`,
PowerShell `Unlock-BitLocker -MountPoint '`, e
`SELECT * FROM Win32_EncryptableVolume Where DriveLetter='%s'`.

> **Achado mais útil para o Kit:** a classe WMI **`Win32_EncryptableVolume`**
> (namespace `root\CIMV2\Security\MicrosoftVolumeEncryption`) tem os métodos `Protect()`,
> `Unprotect()`, `Lock()`, `Unlock()` com **a senha como argumento**. Isso evita spawnar
> `manage-bde`. O `PartitionManager` hoje só *consulta* se há BitLocker
> (`IsBitLockerProtected`); com essa classe ele poderia **desbloquear temporariamente** para
> redimensionar — que é o que os produtos de disco fazem.

### 9.2 `PartitionRecovery.dll` — recuperação de partição

```
PartitionRecovery::doScan(bool) / doConScan() / calc / canContinue / getRecoveryDuration
PartitionRecovery::getRecoveryParts / getLogicDriver / onPartitionInfoReceived
IRecoverySearchEngine
historyIndex / historyPercent / hasHistory / isConScan / isScanning / lastScaned / lastTotal
sigProgress(int,int) / 1onProgressChanged(int,int)
```

Telemetria: `#partition recovery# #recover deleted partition#`,
`-partitions-recovered:{[`.
Suporta **retomar** a varredura (`doConScan`, histórico de índice/percentual).

> O valor para o Kit: o padrão **"procurei X e o índice está em Y%"** persistido, para uma
> varredura de disco (que pode levar horas) poder ser retomada. É UX, não algoritmo.

### 9.3 `RecoveryPartitionFixer.mo` — WinRE

`RecoveryPartitionFixer.cpp`, `Reagent.h` (WinRE), `CurrentWindowsVersion.h`,
`DiskPart.cpp` (único uso de diskpart: criar a partição de recovery),
`SystemUtils.cpp`, `HttpClient.h`, `SpdLogWapper.cpp`.

### 9.4 AddDrivers

`AddDrivers.exe` (473 KB) + `GetDriver.exe`: imports de `SetupAPI`/`CfgMgr`,
`ShellExecuteA`, `RegSetValueExA`. É o "Add Driver" do rodapé da UI (print 1).

---

## 10. Como reproduzir (sem re-fazer reversing)

Tudo o que está acima sai destes 4 scripts versionados + 1 IDA:

```bash
# 0) copiar as DLLs de trabalho (originais intocados)
#    -> %TEMP%/epm_analysis/targets/

# 1) mapa completo: exports + classes + metodos (3.051 exports)
python docs/epm_exports.py

# 2) arquitetura interna: 177 caminhos de fonte em 37 diretorios
python docs/epm_srcmap.py

# 3) TODAS as strings de comando (aquiesta a resposta sobre diskpart/bcdedit)
python docs/epm_cmds.py
python docs/epm_cmds.py --diskpart

# 4) imports + strings por categoria de feature
python docs/epm_map.py                    # ou: --exports-only

# 5) IDA so quando o export nao basta (ex.: o corpo do Rebuild MBR)
cd %TEMP%/epm_analysis
MSYS_NO_PATHCONV=1 "<IDA>\idat.exe" -A \
  -S"ida_epm_dump.py out.txt <regex_nome> <regex_string> <max>" \
  "C:\...\targets\ToolBox.dll"
```

**Quirks do IDA 9 (já gastroenter):** `ida_auto.auto_wait()` é obrigatório no início;
apague o `.i64` antes de reanalisar; `-S` **precisa de aspas duplas** (aspas simples não
passam o `-S`); use `MSYS_NO_PATHCONV=1`; **evite parênteses no regex** do `-S` (o shell do
Git Bash quebra o argumento).

**Armadilha do pefile:** não filtre exports começando com `?` — são exatamente os nomes
mangled MSVC que dão a API limpa. (`IGNORE_EXPORTS = ^(\?|__)` zera o mapa inteiro.)

---

## 11. Mapa EPM → KitLugia (o que já existe, o que falta)

### 11.1 Já implementado no Kit

| EPM | KitLugia | Arquivo |
|---|---|---|
| `isMBR2GPTValid` / `isGPT2MBRValid` (12 regras) | `DiskConverterManager.Analyze()` | `DiskConverterManager.cs` |
| `Convert2GPT` / `Convert2MBR` | `DiskConverterManager.ConvertAsync()` (Storage API → diskpart) | idem |
| `InitializeToMBRDisk` / `InitializeToGPTDisk` | `InitializeAsync()` | idem |
| "Rebuild MBR" (via `bootsect /nt60 /mbr`) | `ReadMbr`/`BackupMbrAsync`/`RestoreMbrAsync` (446 bytes + tabela atual, SHA256) | idem |
| "4K Alignment" | `AnalyzeAlignment()` (diagnóstico honesto) | idem |
| `TransferBootStaff` (bcdboot + bootsect) | `BcdRepairManager.RebuildAsync()` | `BcdRepairManager.cs` |
| NVRAM `BootNext` | `CreateDirectNvramBoot` (`/set {fwbootmgr} bootsequence`) | `WinbootManager.cs` |
| "Rebuild partition table" | `RecoveryPartitionFixer` (não implementado) | — |
| VDS resize | escada `MSFT_Partition.Resize` → IOCTL → diskpart | `PartitionManager.cs` |

### 11.2 Lacunas reais (ordenadas por valor/segurança)

| # | Lacuna | Base no EPM | Risco | esforço |
|---|---|---|---|---|
| 1 | **Retry assíncrono de lock de volume** | `CAsynLockVolume` (`MAX_RETRY_TIMES`, thread dedicada) | baixo | pequeno — resolve "extend falhou" por file handle aberto |
| 2 | **Limpar read-only antes de operar** | `echo attributes disk clear readonly \| diskpart` | baixo | pequeno (IOCTL) |
| 3 | **Write Protection (ligar/desligar)** | `attributes disk N readonly` | médio | pequeno |
| 4 | **Detecção de disco 4Kn nativo** | `4k disk cannot be selected as target` | baixo | pequeno (`IOCTL_DISK_GET_LENGTH_INFO` → `PhysicalSectorSize`) |
| 5 | **Reduzir shadow storage antes de alinhar** | `vssadmin resize shadowstorage /maxsize=401MB` | baixo | pequeno — libera espaço real para o 4K align |
| 6 | **BitLocker via WMI `Win32_EncryptableVolume`** | `SELECT * FROM Win32_EncryptableVolume Where DriveLetter=` | médio | médio — permite desbloquear p/ redimensionar |
| 7 | **Alinhar de verdade** (mover partição p/ 1 MiB) | `PartAlignProc.cpp` + `ResizeMoveByRebuildingBitmap/MFT` | **alto** | muito alto — precisa do motor NTFS; hoje é diagnóstico |
| 8 | Clone/Migrate OS | `CEUCloneMgr` + Tailwind + refazer boot | **alto** | muito alto |
| 9 | Recuperação de partição | `IRecoverySearchEngine` | médio | alto |
| 10 | "Bad Sector Scan" que repara FS | `NTFS/FAT/ExFATFixer::FixByScanSector` | médio | alto |

---

## 12. O que **não** copiar do EaseUS

1. **Não implementar BitLocker do zero.** O EPM tem anos de FVE nesta area; no Windows,
   `manage-bde`/WMI faz o mesmo — copiar essa superfície seria um passivo, não uma vantagem.
2. **Não usar VDS.** Deprecado desde o Windows 8; a Storage Management API é o sucessor.
3. **Não escrever BCD direto.** O `bcdedit` da Microsoft é o caminho suportado; a biblioteca
   binária do EPM é proprietária porque eles precisam funcionar onde o `bcdedit` não está.
4. **Não prometer 4K alignment** onde só há diagnóstico (o item 7 da §11.2 é real work).
