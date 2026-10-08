# EaseUS Partition Master — Versão DESKTOP (17.9.0) vs WinPE Edition 20.8

**Analisado em:** 03/10/2026
**Alvo:** `epm_trial_ob.exe` (instalador Inno Setup 6.1.0, 77 MB) → `app\` (338 MB extraídos)
**Complementa:** [`EASEUS_EPM_MAP.md`](EASEUS_EPM_MAP.md) (mapa do WinPE) e [`EASEUS_EPM_ANALYSIS.md`](EASEUS_EPM_ANALYSIS.md)

---

## 0. Resposta direta: COMO ele "burla o shrink"

O que o usuário viu — **reboot → tela preta com "EaseUS Partition Master" escrito no topo → operação → Windows volta** — é uma **entrada de boot UEFI criada na BCD que carrega um WinPE inteiro (`EASEUSEPMPE.WIM`) como ramdisk**, montado pelo próprio EPM no Windows antes do reboot.

O fluxo é **identico ao que o KitLugia já faz** (`ScheduleWinpeShrink`). Não há milagre nem técnica oculta — é injeção de ramdisk na BCD. As diferenças são de **robustez e de UX**, não de mecanismo:

| Aspecto | EaseUS Desktop | KitLugia (hoje) |
|---|---|---|
| WIM usado | `C:\peboot\EASEUSEPMPE.WIM` (688 MB, baixado do CDN) | `C:\KL_WINPE\boot.wim` (criado/injetado local) |
| SDI (boot.sdi) | `C:\peboot\EASEUSEPMPE.SDI` (copiado) | idem |
| GUID do ramdisk | fixo `{B40D316B-B159-4229-8D1B-4A388A093C5B}` | fixo `{2c9f4b6a-1e7d-4a8f-9c3b-5f6d7e8a9b0c}` |
| Descrição da entrada | `"EaseUS Partition Master PreOS"` | `KitLugia WinPE` |
| Como entra no menu | `/displayorder` + `timeout` | `/bootsequence` (one-shot NVRAM) |
| Interface no PE | GUI completa (`EPMUI.exe` via `winpeshl.ini`) | cmd + log (`startnet.cmd`) |
| Limpeza pós-op | automática (`IsAlreadyDone`) + `bootsequence` removido | log persistente + reboot |

**A tela preta com o nome no topo** é o `winload.efi` do WinPE com o `winpeshl.ini` customizado que o EPM injeta no WIM — a "marca" do produto aparece antes de qualquer GUI.

---

## 1. O payload: `app/BUILDPE/` + CDN

O instalador traz um **moldurador de WinPE completo** e baixa o WIM sob demanda.

### 1.1 Estrutura embarcada

```
app/BUILDPE/
├── DownloadFileList.json          <-- lista de downloads em runtime
├── EaseUS-x64/epm/                <-- motor que vai DENTRO do PE
│   ├── bin/     (AddDrivers.exe, EUCloneClient.dll, config.ini, mfc90…)
│   └── DC/bin/  (drivers, EPMUI e companhia)
├── x64/
│   ├── Windows/System32/
│   │   ├── winpeshl.ini           <-- o "launcher" do PE (marca do produto)
│   │   ├── Unattend.xml           <-- 1024x768, 32-bit
│   │   ├── ebrntdrv.sys, epmdkdrv.sys, msi.dll, oledlg.dll
│   │   └── drivers/EUDCPEPM.sys, EUEDKEPM.sys
│   └── drv/    (vazio — reservado)
└── x86/Windows/system32/          (variante 32-bit)
```

### 1.2 `DownloadFileList.json` — o WIM vem do CDN

```json
{ "FileName": "boot.wim",
  "DownloadUrlList": [ "https://download.easeus.com/winpe/boot.wim" ],
  "MD5Value": "ec1c96060f7faf5c6b34998f27d9d224" }
{ "FileName": "tools.zip",
  "DownloadUrlList": [ "https://download.easeus.com/winpe/tools.zip" ],
  "MD5Value": "00609c65b30d10457eeabb97cdcacf59" }
```

Confirmado ao vivo:
- `boot.wim` = **657 MB**, Windows PE **10.0.19041** (base ADK, não o WinPE da Microsoft Store), 1 índice, `EDITIONID=WindowsPE`.
- `tools.zip` = **193 MB**, MD5 confere. Conteúdo: `TOOLS/DiskMark` (CrystalDiskMark), `TOOLS/DiskHealth`, `TOOLS/Google Chrome`, etc. — os **extras** do PE.

Ou seja: o binário instalado no Windows é pequeno; o PE completo (≈850 MB) é **baixado sob demanda** na primeira operação que precisa pré-OS.

### 1.3 `winpeshl.ini` injetado (a "tela preta com o nome")

```ini
[LaunchApps]
"%systemdrive%\Windows\System32\wpeinit.exe"
"x:\program files\Other\Tools\ChangeRes.exe", setmax
"x:\program files\Other\Tools\launch.exe",  0 "x:\program files\Other\explorer\auto.bat"
"x:\program files\easeus\epm\bin\EPMUI.exe"
"x:\program files\Other\Tools\launch.exe",  2
```

O `X:` é o **ramdisk** montado pelo bootloader. O PE é minimalista: wpeinit → tools → EPMUI → fecha.

### 1.4 `PeMaker.dll` — como o WIM é montado

Fontes reveladas: `d:\epm\_epm_main\epmlogic\mod.pemaker\{makepe,makepe3,makepe4}.cpp`
Classes: `CPeMaker` (wrapper) → `CMakePe` / `CMakePe3` / `CMakePe4` (versão por build de Windows).

Cadeia de montagem (strings em UTF-16 de `PeMaker.dll`):

1. **Download** — `..\BUILDPE\Downloads\boot.wim` + `tools.zip` (confere MD5 do JSON).
2. **Fallback ADK/PETools** — se não houver WIM pronto, monta do zero:
   - `%ProgramFiles%\EaseUS\epm\bin\PETools\amd64\WinPE_FPs`
   - `Windows Kits\8.0\Assessment and Deployment Kit\Windows Preinstallation Environment\amd64\WinPE_OCs`
   - `WinPE-WMI.cab`, `WinPE-StorageWMI.cab`, `WinPE-NetFx.cab`, `WinPE-PowerShell.cab`, `WinPE-Scripting.cab`, `WinPE-SecureStartup.cab`, fontes de idioma
   - `%windir%\Inf\`, `iscsi.inf`
3. **Montagem do WIM via `Wimgapi.dll`** (API nativa de imagem, **não** DISM):
   - `Begin MountImage` / `Mount Image:MountPath:%s, WimFilename:%s, ImageIndex:%d`
   - **DISM só como reforço**: `\mount" /Add-Driver`, `/Add-Package`, `/Set-SysLocale`, `/Set-UserLocale`, `/enable-feature /featurename:`, `/Set-ScratchSpace:256`
   - Copia `\BuildPE\EaseUS-X64\*` → `x:\Program Files\EaseUS\epm\`, `x64\Windows\*`, `x64\regdrv.reg`, `x64\software.reg`, `easeus\epm\multi\*`, `easeus\epm\res\*`
4. **Exporta ISO** (`imagePE.iso`, `image.iso`, `imageLinux.iso`) via `oscdimg.exe`:
   - `-n -b"\etfsboot.com"  "\ISO"  ...`
   - `-h -m -o -u1 -udfver102 -bootdata:2#p0,e,b"\efisys.bin" ...`
   - `load` / `unload` = montar/desmontar as ISO montadas antes de copiar
5. Log: `\ExportISO.log`.

`CMakePe::Init` loga `[zhangyong] CMakePe::Init szDrive = %c` e `GetVolumeFreeSpace` — escolhe uma **letra de volume com espaço livre** para staging.

---

## 2. `CPreOsPEBcdProc` — o coração (CloneModule.dll)

**Fonte:** `d:\epm\_epm_main\epmlogic\mod.clonemodule\preospeandbcdproc.cpp`
Classe declarada como `.?AVCPreOsPEBcdProc@@`. Métodos (nomes MSVC preservados no binário):

```
CPreOsPEBcdProc::Install
CPreOsPEBcdProc::BcdPEProc              <-- cria a entrada UEFI (ramdisk)
CPreOsPEBcdProc::IniPEProc              <-- caminho legado BIOS (boot.ini / ntldr)
CPreOsPEBcdProc::_createEntryInBcd     <-- cria {GUID} e põe a descrição
CPreOsPEBcdProc::_createBcdEntry
CPreOsPEBcdProc::DelBcdEntry
CPreOsPEBcdProc::IsBcdEntryExist
CPreOsPEBcdProc::CreateBootFile         <-- copia winload.efi / winpeshl.ini
CPreOsPEBcdProc::GetBootType            <-- UEFI vs BIOS vs VHD
CPreOsPEBcdProc::IsSupportCurOs
CPreOsPEBcdProc::ChangeWritePrivilege   <-- remove o read-only da BCD
CPreOsPEBcdProc::_createEfiBootFile
CPreOsPEBcdProc::_createBiosBootFile
CPreOsPEBcdProc::_createNtldrBootFile
CPreOsPEBcdProc::_editNtldrBcd4Boot
CPreOsPEBcdProc::_createDirectory
CPreOsPEBcdProc::_isoGetFileProc / _isoGetDirectoryProc / _isoGetFileInfo
CPreOsPEBcdProc::_isoReadPeMark / _isoSetLocalFileTime
```

### 2.1 Os GUIDs (extraídos por XREF e confirmados no decompilado)

| GUID | Papel | Aparece em |
|---|---|---|
| `{B40D316B-B159-4229-8D1B-4A388A093C5B}` | **RAMDISK do PreOS** (o WIM) | `sub_1800ADFB0`, `sub_1800AE8A0` (`BcdPEProc`), `sub_1800B2670` (`_createEntryInBcd`) |
| `{1941F361-6968-4258-BA93-44E099AF5E55}` | **DEVICE objeto** (ramdisksdidevice) | mesmos + `DelBcdEntry` |
| `{77128171-3112-48b6-8E24-0DF383CD8824}` | RAMDISK legado (CPEBcdProc / Data Center) | `sub_1800A3F20`, `sub_1800A7CB0` |
| `{DC6EAD74-00DF-4C94-A548-35705DFB3518}` | DEVICE objeto legado | idem |
| `{9dea862c-5cdd-4e70-acc1-f32b344d4795}` | objeto template do **ramdisk options** (`boot.sdi`) | `sub_1800D9A30` |

### 2.2 As linhas de BCD — strings literais, em sequência no `.rdata`

Bloco do `CPreOsPEBcdProc::BcdPEProc` (offsets `0x15DAE0`–`0x15E2F8`), na ordem em que são consumidas:

```
{B40D316B-B159-4229-8D1B-4A388A093C5B}
ramdisksdidevice
OptIn
partition=C:                          <--wsprintf com a letra do volume do WIM
{B40D316B-B159-4229-8D1B-4A388A093C5B}
ramdisksdipath
\peboot\EASEUSEPMPE.SDI              <-- boot.sdi do PreOS
{1941F361-6968-4258-BA93-44E099AF5E55}
ramdisk=[C:]\peboot\EASEUSEPMPE.WIM,{B40D316B-B159-4229-8D1B-4A388A093C5B}
{1941F361-6968-4258-BA93-44E099AF5E55}
path
\windows\system32\boot\winload.exe
{1941F361-6968-4258-BA93-44E099AF5E55}
osdevice
ramdisk=[C:]\peboot\EASEUSEPMPE.WIM,{B40D316B-B159-4229-8D1B-4A388A093C5B}
Val / systemroot / \windows
NTLDR
Delete
inherit / {bootloadersettings} / detecthal / {bootmgr} / bootsequence
ESMGR / SAM / SYSTEM                  <-- hive SYSTEM para injetar a config no PE
```

### 2.3 Decompilado (`sub_1800AE8A0` = `CPreOsPEBcdProc::BcdPEProc`)

```c
// pseudocode reconstruído
if (!isPEMounted() || !*this->pePath)          fail("[CPreOsPEBcdProc] BcdPEProc fail!");
if (this->isWin11_22H2plus)   wsprintf(bcdPath, L"%s\\Boot\\BCD",        this->pePath);
else if (this->isUEFI)        wsprintf(bcdPath, L"%s\\EFI\\Microsoft\\Boot\\BCD", this->pePath);
else                          wsprintf(bcdPath, L"%s\\Boot\\BCD",        this->pePath);

openBcd(bcdPath)                              // sub_180004827
createObject("{1941F361-...}")                 // sub_18000793C  device object
createObject("{B40D316B-...}")                 // sub_18000793C  ramdisk object
if (!ok) retryWithExplicitPath(bcdPath)        // sub_180004124 -> fallback

for (i = 0; i < 11 && ok; ++i)                 // 11 PROPRIEDADES da entrada
    setValue(bcd, name[i], value[i], ...)      // sub_18000377E

setValue(bcd, "{1941F361-...}", 0, 0)          // sub_180008AF3  (displayorder/default)
destroy(bcd)
```

O `sub_18000377E` é o **wrapper de `bcdedit /set`**. As 11 tuplas `(key, value, ?)` são exatamente as strings da §2.2.

### 2.4 Decompilado (`sub_1800B2670` = `_createEntryInBcd`)

```c
ok = bcdCreate(a2, "{1941F361-6968-4258-BA93-44E099AF5E55}",
                   "\"EaseUS Partition Master PreOS\"", /*isObject*/ 1);
if (ok)
    ok = bcdCreate(a2, "{B40D316B-B159-4229-8D1B-4A388A093C5B}",
                       "\"EaseUS Partition Master PreOS\"", /*isObject*/ 0);
```

**Confirmação direta:** a descrição que o usuário viu na tela é a string literal
`"EaseUS Partition Master PreOS"`.

O caminho legado (`CPEBcdProc`, GUID `{77128171-…}`, WIM em `C:\boot\`) faz o mesmo com
`"EaseUS Partition Master Windows PE"` e **`[C:]\boot\EASEUSEPMPE.WIM`** — é o fluxo do
Data Center / disk clone, guardando a lista de 12 propriedades (loop `< 12`).

### 2.5 `DelBcdEntry` (`sub_1800ADFB0`)

Mesma construção de path de BCD, remove `{1941F361-…}` e `{B40D316B-…}`.
Isto é o que faz a **entrada sumir depois da operação** — não há "acúmulo de KitLugia".

### 2.6 `IsBcdEntryExist` / detecção sem `bcdedit`

`sub_1800D9A30` é a peça mais interessante: **verifica a existência da entrada lendo o registry de classes da BCD diretamente**.

```c
RegOpenKeyExA(HKLM, "BCD00000000\\Objects\\{9dea862c-...}\\Elements\\24000001", ...);
RegQueryValueExA(hKey, "Element", &Type, Data, &cbData);   // Type == 7 (REG_MULTI_SZ)
for (cada string do multi-sz) {
    sprintf(subKey, "BCD00000000\\Objects\\%s\\Elements\\11000001", guidDaEntrada);
    RegQueryValueExA(phkResult, "Element", &v18, &cbData);
    if (v18[144] == dispositivoEsperado && (!filtrarTam || guid == filtrarTam))
        return 1;   // entrada existe
}
```

Ou seja: **`HKLM\BCD00000000` é a visão do registry da BCD ativa**. Abrir
`\Objects\{guid}\Elements\24000001` dá o identificador do ramdisk; abrir
`\Objects\{guid}\Elements\11000001` dá o **device object** (offset 120 = 16 bytes de
`PARTITION_ELEMENT`; offset 144 = o número do disco). Isso é **100× mais rápido e
language-agnostic** que parsear `bcdedit /enum all` — exatamente o bug que o KitLugia
sofreu e corrigiu com regex em 6 idiomas.

> 💡 **Lição aplicável**: o KitLugia usa `bcdedit /enum` + regex multilíngue
> (`FindBcdGuidsByText`). Ler `HKLM\BCD00000000\Objects\*` direto elimina a dependência de
> idioma **e** do binário `bcdedit.exe`.

---

## 3. `CEUPreOsMgr` — o orquestrador

Classe `.?AVCEUPreOsMgr@@`, exportada (thunks MSVC preservados):

```
??0CEUPreOsMgr@@QEAA@H@Z                          // ctor(int)
?SetISOPath@CEUPreOsMgr@@QEAA_NPEB_W@Z            // bool SetISOPath(const wchar_t*)
?DelBootEntry@CEUPreOsMgr@@QEAA_NXZ               // bool DelBootEntry()
?IsAlreadyDone@CEUPreOsMgr@@QEAA_NXZ              // bool IsAlreadyDone()
?GetBootPart@CEUPreOsMgr@@IEAAPEAUIManagerItem@@XZ
?GetSystemPart@CEUPreOsMgr@@IEAAPEAUIManagerItem@@XZ
```

Aparece em: `CloneModule.dll`, `Clone.dll`, `BootableMedia.dll`, `EPMUI.exe`,
`EPMConsole.exe`, `UnInstallProc.exe` — ou seja, **é a porta de entrada oficial**, e o
`UnInstallProc.exe` também o usa (desinstalar limpa a entrada).

`?SetISOPath@CEUCloneMgr@@QEAA_NPEB_W@Z` é o mesmo método na classe `CEUCloneMgr` (clone).

### `CBoot::GetPreOSTypeEx` (Boot.dll)

`Boot.dll` é `d:\epm\_epm_main\sharelib\dc\diskbackup\mod.boot\` (mesmo módulo serve à UI e
ao motor, mesmoPDB de `Win32Bcd.dll`/`CBCDEdit`). Ele tem o editor de BCD mais completo:

```c
/ create %s /d %s /application osloader
/ create %s /d %s /application bootsector
/ create %s /d %s /device
/ set %s %s %s
/ store %s /create %s /d %s /application osloader      (variante com /store)
/ store %s /set %s %s %s
```

e os templates de ramdisk:

```
ramdisk=[C:]\boot\EASEDCPE.WIM,{8e0b8211-7965-4722-94cb-16c9e078c47e}
ramdisk=[boot]\boot\EASEDCPE.WIM,{8e0b8211-7965-4722-94cb-16c9e078c47e}
```

`EASEDCPE.WIM` = **o PE do Data Center** (clonar disco), com `bootsequence`, `displayorder`,
`ramdisksdidevice`. `BOOT/EASEUSPE.BIN` é o **blob de boot sector** para BIOS.

### `CPreOSBoot` (CmdManager.dll)

```
. ?AVCPreOSBoot@@
[Wallace] GetPreOSType() : GetIsoPath() function failed.
[Wallace] GetPreOSType() : GetBootToolPath() function failed.
Ldq : pPreOsBoot->Init failed
```

Classe que resolve **qual PE usar** (iso vs binário embarcado) e qual ferramenta de boot.

### `DsRestore.dll` — o gate

```
[likai] IsLinuxPreOS =%u
[likai] IsOsSupportBuildPE %u
[likai] IsOsSupportBuildPE PeMaker Return=0x%X , bRet=%u
```

Bloqueia a operação pré-OS se o build do Windows não for suportado, ou se já for Linux-PE.

---

## 4. A UI do "ligar o PreOS"

`UILogic.dll`:

```
CUIPreOsWizard (CreatorByType<IUIEmergencyWinPE>)
"Enable PreOS..."  /  "Enable PreOS Successfully."  /  "IDS_ENABLE_PREOS"
[UILogic] CUILogic::StartPreOSTask entry
[UILogic] CUILogic::StartPreOSTask check data valid sucess
[UILogic] CUILogic::StartPreOSTask invoke find data nRslt = %d , nDataSize = %d
[UILogic] CUILogic::StartPreOSTask invoke release success
```

`StartPreOSTask` é o **pré-checagem antes de rebootar**: `IsFileValid` +
`CheckRecoverDataValid` (o WIM baixou inteiro? os parâmetros gravados são válidos?).
Se algo falhar, **não reinicia**.

`CUIEmergencyWinPE` também gerencia mídia removível: `image.iso`, `imagePE.iso`,
`imageLinux.iso`, `EmergencyDisk.iso`, `System.iso`, e erros honestos:

```
"Failed to create WinPE bootable disk."
"Failed to download WinPE component, please check your Internet connection and try again."
"Failed to export WinPE ISO."
"Grab WinPE from local failed, retry to create it with PETools."
"Unable to execute this operation in the current environment. Please execute this operation in WinPE."
```

---

## 5. Como a operação chega ao PE

O `CDeployTask` (UILogic.dll) mostra a arquitetura de **comunicação** desktop↔PE:

```
[UILogic] CDeployTask::CreateDeployObject tbImageStorage.szPath2 : %s
[UILogic] CDeployTask::CreateDeployObject tbImageStorage.szPath : %s
[UILogic] CDeployTask::CreateDeployObject tbDriverStorage.szPath : %s
[UILogic] CDeployTask::CreateDeployObject diskNo : %d
[UILogic] CDeployTask::CreateDeployObject bUniversal = %d
[UILogic] CDeployTask::CreateDeployObject computer : %s
[UILogic] SendTaskProgress eCommand = %d, nRslt = %d, nErrorCode = %d
[UILogic] CDeployTask::OnDeployTaskFinished operation = %d
SendCommand  CMD_GET_DEPLOY_TASK
```

E o hive `SYSTEM` com `ESMGR`/`SAM`/`SYSTEM` é injetado no WIM para a sessão do PE ter as
credenciais do agente (`EUDCPEPM.sys` é o driver de filtro de volume do "EaseUS Device
Control").

O `CmdManager.dll` (`CAsyncCmdDeal` / `CTaskCmdDeal`) executa com privilégio de serviço
(`SeShutdownPrivilege`) e é quem efetivamente **reinicia** e **limpa a tarefa**.

---

## 6. WinPE vs Desktop — diff objetivo

### 6.1 Módulos

- **35 módulos em comum, 0 idênticos** (MD5), versões diferentes.
  Ex.: `PartitionManager.dll` 6,2 MB (desktop) vs 7,5 MB (WinPE);
  `ToolBox.dll` 1,55 MB vs 2,36 MB.
- **Só no desktop** (≈90 DLLs/EXEs): `PeMaker.dll`, `CmdManager.dll`, `BootableMedia.dll`,
  `Boot.dll`, `MainModule.dll`, `DsRestore.dll`, `Device.dll`, `DeviceAdapter.dll`,
  `DeviceIO.dll`, `DevCtrl.dll`, `EnumDisk.dll`, `Burn.dll`, `FHProcess.dll`,
  `CompressFile.dll`, `CorrectMbr.dll`, `BPVolume.dll`, `EFIBoot.dll`, `BootDriver.dll`,
  `AuthorizedMng.dll`, `BuyNow.dll`, `BuyWnd.dll`, `CloudOperator.dll`, `EraseData.dll`,
  `FileStorage.dll`, `FATFileSystemAnalyser.dll`, `FatResizeMove.dll`, `EPMConsole.exe`,
  `EUCloneServer.exe`, `BootRepair_Console.exe`, `ErrorReport.exe`, `ExpandBoot.exe`,
  `SetupUE.exe`, `syslinux.exe`, `grubinst.exe`, `bcdedit.exe`, `bootsect.exe`, `bcdboot.exe`…
- **Só no WinPE**: `BT_NTFSUtil.mo`, `BitLockerLib.dll`, `LLFProc.mo`, `RawFixer.dll`,
  `RecoveryPartitionFixer.mo`, `VhdVmdk.dll` — o motor de recovery/BitLocker é **exclusivo do PE**.

### 6.2 O motor de disco é o mesmo

O `ToolBox.dll` (contrato das 54 operações, `ResizeMoveByRebuildingBitmap` /
`ResizeMoveByRebuildingMFT`, `CAsynLockVolume`) é **compartilhado**. O desktop **não** tem um
"truque de shrink" — ele só consegue fazer o mesmo resize do PE, porque **delega a operação
para o PE**.

### 6.3 Quem monta o PE

| | WinPE Edition | Desktop |
|---|---|---|
| WIM | pré-empacotado na ISO | baixado do CDN ou gerado do ADK |
| Origem | distribution | `PeMaker.dll` + `BUILDPE/` |
| ISO | pronta | `oscdimg.exe` em runtime |
| Montagem | — | `Wimgapi.dll` + DISM |

---

## 7. Reflexão: o que o KitLugia já faz igual, e o que dá para melhorar

### 7.1 Já idêntico (nada a mudar)

- WIM + SDI + GUID fixo + `ramdisk=` + `ramdisksdidevice partition=X:` + `/bootsequence`.
- Limpeza de entradas antigas antes de criar (o EPM faz `DelBcdEntry`; o Kit faz `CleanupOldWinpeEntries`).
- Log persistente no volume alvo.

### 7.2 Melhorias de baixo risco, alto ganho

1. **Ler a BCD pelo registry** (`HKLM\BCD00000000\Objects\*`), como o EPM faz em
   `sub_1800D9A30`. Elimina:
   - o parsing multilíngue de `bcdedit /enum all` (o bug que já custou uma sessão inteira);
   - a dependência do binário `bcdedit.exe` presente no host.
   Também dá `IsAlreadyDone` de graça: se a entrada existe, não reinicia.

2. **Pré-checagem antes de reiniciar** (`IsFileValid` + `CheckRecoverDataValid`).
   O EPM valida o WIM e os parâmetros *antes* do `shutdown`. O Kit hoje agenda e reinicia
   para só então o script descobrir que o WIM não está lá.

3. **`ChangeWritePrivilege`** — o EPM remove o read-only/sistema da BCD explicitamente.
   O Kit depende do estado herdado do admin.

4. **Detecção de build suportado** (`IsOsSupportBuildPE`) — evita reboot inútil quando a
   operação não pode rodar no PE.

### 7.3 A diferença real: **interface**, não mecanismo

O EaseUS mostra **GUI no PE** (`EPMUI.exe` + `winpeshl.ini`). O KitLugia mostra **cmd com log**.
É por isso que a tela do EaseUS tem o nome do produto no topo e parece "mágica".

Já houve uma tentativa anterior de injetar GUI no PE (`InjectWinXShellIntoWimAsync`) que
dependia de download externo e foi abandonada. O caminho do EPM mostra que **não precisa
baixar nada para isso**: basta escrever um `winpeshl.ini` no WIM apontando para um executável
que já está na sua pasta.

---

## 8. Evidências / artefatos

| Item | Caminho |
|---|---|
| Instalador analisado | `https://download.easeus.com/trial/epm_trial_ob.exe` (77 MB, Inno 6.1.0) |
| Extraído | `%TEMP%\epm_analysis` não; desktop em `C:\epm_dl\app\` |
| Módulos desktop (targets) | `%TEMP%\epm_desktop\targets\` (359 arquivos) |
| Saída do scanner de strings | `%TEMP%\epm_desktop\preos_out.txt` (2.738 linhas) |
| Dumps IDA | `C:\ida_s\out_ramdisk.txt`, `out_props.txt`, `out_pemaker.txt`, `out_pe.txt` |
| `boot.wim` analisado | `C:\epm_dl\boot.wim` (688.601.920 B, Windows PE 10.0.19041) |
| `tools.zip` | `C:\epm_dl\tools.zip` (202.568.944 B, MD5 confere) |
| Script de scan | `%TEMP%\epm_desktop\preos_scan.py` |

**Quirk do IDA usado nesta sessão:** o caminho do `docs/ida_epm_dump.py` contém espaços e
parênteses (`KitLugia-master (25) - Copia...`), o que **quebra o `-S` do idat.exe**. Solução:
copiar o script para `C:\ida_s\dumper.py` e lançar via `subprocess.run` (o `|` dos regex
também é interpretado pelo `cmd.exe` se passar por batch).

---

## 9. Referência rápida — os comandos que o EPM emite

```bat
bcdedit /create {B40D316B-B159-4229-8D1B-4A388A093C5B} /d "EaseUS Partition Master PreOS" /application ramdisk
bcdedit /create {1941F361-6968-4258-BA93-44E099AF5E55} /d "EaseUS Partition Master PreOS" /device
bcdedit /set {B40D316B-...} ramdisksdidevice partition=C:
bcdedit /set {B40D316B-...} ramdisksdipath \peboot\EASEUSEPMPE.SDI
bcdedit /set {B40D316B-...} ramdisk=[C:]\peboot\EASEUSEPMPE.WIM,{B40D316B-...}
bcdedit /set {1941F361-...} path \windows\system32\boot\winload.exe
bcdedit /set {1941F361-...} osdevice ramdisk=[C:]\peboot\EASEUSEPMPE.WIM,{B40D316B-...}
bcdedit /set {B40D316B-...} systemroot \windows
bcdedit /set {B40D316B-...} detecthal Yes
bcdedit /set {B40D316B-...} inherit {bootloadersettings}
bcdedit /displayorder {B40D316B-...} /addlast
bcdedit /bootsequence {B40D316B-...} /addlast
```

> Nota: os 4 primeiros são reconstruídos a partir das 11 propriedades em `BcdPEProc` + a
> forma dos templates em `Boot.dll`. O resto (`Win32Bcd.dll`/`CBCDEdit`) é literal.
> O caminho do WIM **é** literal: `ramdisk=[C:]\peboot\EASEUSEPMPE.WIM,{B40D316B-...}`.
