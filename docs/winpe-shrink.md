# WinPE Shrink — Documentação Completa

## Visão Geral

O **WinPE Shrink** reduz partições NTFS que o Windows em execução não consegue
(a partir de ~3 GB surgem "arquivos imóveis" — pagefile, hibernate, MFT).
Boota um WinPE mínimo em RAM disk, executa o shrink lá (volume offline), e
reinicia ao Windows.

## Status Atual (30/07/2026) — TESTADO E FUNCIONANDO

```
X:\windows\system32\cmd.exe - startnet.cmd
KitLugia WinPE Shrink (RAMDISK)
Embedded disk/part invalid, falling through...
Scanning for KL_SHRINK_TARGET.dat marker...
Found marker: DISK=0 PART=3 SHRINK=5000
...
DiskPart successfully shrunk the volume by: 5000 MB
Shrink done. Writing persistent log...
Log saved to Z:\KitLugia_WinPE_Log.txt
Rebooting...
```

## Arquitetura

```
Fase 1 — Windows (KitLugia GUI)

  ScheduleWinpeShrink(drive, shrinkMB)
    +-- Query WMI (Win32_DiskDrive, Win32_LogicalDiskToPartition)
    +-- Cria marker KL_SHRINK_TARGET.dat com DISK_N, PART_N, SHRINK_MB
    +-- Prepara startnet.cmd com valores embutidos
    +-- Injeta startnet.cmd + marker dentro do boot.wim via wimlib
    +-- Configura bcdedit bootsequence (boot unico WinPE)
    +-- shutdown /r /t 10
                      |
                      v
Fase 2 — WinPE (RAM Disk X:\)

  BIOS/UEFI -> WBM -> BCD -> boot.wim -> RAM disk

  startnet.cmd:
    1. Tenta valores embutidos (DISK_N, PART_N, SHRINK_MB)
       Se PART_N==0, invalido -> fall through
    2. Le KL_SHRINK_TARGET.dat de X:\Windows\System32\
       Se encontrado, usa DISK_N, PART_N, SHRINK_MB
    3. Scan: discos 0-3, particoes 1-8:
       assign letter=Z, check SOFTWARE, found -> DISK_N/PART_N
    4. :run:
       select disk N
       select partition N
       assign letter=Z    <- TRAZ VOLUME ONLINE
       shrink desired=N
       remove letter=Z
    5. Copy log para Z:\KitLugia_WinPE_Log.txt
    6. wpeutil reboot
```

## Lições Aprendidas

| Bug / Problema | Sintoma | Causa | Correção |
|---|---|---|---|
| **WMI retorna PART errada** | Shrink na partição errada | `Win32_LogicalDiskToPartition` pode retornar número diferente do real (ex: PART=2 em vez de PART=3) | Validate via marker KL_SHRINK_TARGET.dat ou scan assign+SOFTWARE |
| **`--command-file` não existe** | Shrink não injeta config | wimlib bundlado (versão antiga) não suporta `--command-file` | Usar `--command` direto com string inline |
| **Volume offline** | "You may not shrink offline volumes" | `select partition N` sem `assign letter` — volume fica offline no WinPE | `assign letter=Z` antes do shrink, `remove letter=Z` depois |
| **Secure Boot barra WinPE** | Erro 0xc0000001 (boot fail) ou tela preta | Boot.wim não assinado pela Microsoft | Desligar Secure Boot temporariamente |
| **BCD `bootsequence` vs `displayorder`** | WinPE não boota sozinho | `displayorder` adiciona à lista mas não força boot único | `bcdedit /set {fwbootmgr} bootsequence {guid}` (BootNext NVRAM) |
| **Dual shrink: shrink gasta espaço dos dois** | Falta espaço | Dois shrinks em sequência gastam espaço dobrado | Apenas um shrink por sessão WinPE |
| **Acenos/acentos no batch** | `{palavra} was unexpected at this time` | cmd.exe do WinPE não reconhece bytes não-ASCII | 100% inglês, sem acentos/cedilha |

## Fluxo Detalhado

### 1. ScheduleWinpeShrink (Windows)

1. Detecta DISK_N, PART_N via WMI
2. Cria `shrink_config.ini` com todos os metadados
3. Gera `startnet.cmd` com `EMBED_DISK_N`, `EMBED_PART_N`, `EMBED_SHRINK_MB` hardcoded
4. Injeta `startnet.cmd` + `shrink_config.ini` via **wimlib** `--command` (não `--command-file`):
   - `wimlib-imagex update boot.wim 2 --command "add startnet.cmd /Windows/System32/startnet.cmd"`
   - `wimlib-imagex update boot.wim 2 --command "add shrink_config.ini /shrink_config.ini"`
5. Insere `KL_SHRINK_TARGET.dat` via wimlib no `Windows/System32/`
6. Cria BCD ramdisk entry (já feita no Prepare)
7. `bcdedit /set {fwbootmgr} bootsequence {guid}` — BootNext na NVRAM
8. `shutdown /r /t 10`

### 2. Boot no WinPE

1. Firmware carrega Windows Boot Manager (Secure Boot precisa estar OFF)
2. WBM lê BCD, encontra "KitLugia WinPE - Shrink"
3. Carrega `boot.wim` em RAM disk via `boot.sdi`
4. `winpeshl.exe` → `startnet.cmd`

### 3. startnet.cmd (lógica de shrink)

```
1. wpeinit
2. Tenta EMBED_DISK_N, EMBED_PART_N, EMBED_SHRINK_MB
   - Se EMBED_PART_N == 0 → inválido (DISK_N=0 é válido, mas PART_N=0 não)
3. Se inválido: le X:\shrink_config.ini (do WIM)
4. Se inválido: le X:\Windows\System32\KL_SHRINK_TARGET.dat
5. Se inválido: scan disco 0-3, partição 1-8
   - assign letter=Z
   - check Z:\Windows\System32\config\SOFTWARE
   - found → DISK_N/PART_N definidos
   - remove letter=Z
6. :run:
   - select disk N
   - select partition N
   - assign letter=Z
   - shrink desired=N
   - remove letter=Z
7. Copy X:\result.log → Z:\KitLugia_WinPE_Log.txt
8. wpeutil reboot
```

### 4. Pós-reboot

1. Windows inicia normalmente
2. Log em `C:\KitLugia_WinPE_Log.txt`
3. Espaço shrinkado disponível no final da partição

## Classes e Métodos

### WinbootManager.cs

| Método | Descrição |
|--------|-----------|
| `PrepareWinpeBootAsync()` | Baixa/cacheia WinPE base, customiza WIM, cria BCD ramdisk |
| `ScheduleWinpeShrink(drive, shrinkMB)` | Agenda shrink+reboot |
| `IsWinpeReady()` | Verifica se C:\KL_WINPE\boot.wim existe |
| `RemoveWinpeAsync()` | Remove BCD entry + deleta C:\KL_WINPE\ + limpa config |
| `GetDiskPartitionInfo(driveLetter)` | WMI query: DISK_N, PART_N, serial, label, offset, size |
| `RamdiskStartnetCmd()` | Gera startnet.cmd com embedded values + scan + marker |
| `CreateRamdiskEntry()` | Cria BCD osloader com device=ramdisk |

### WinpeBuilder.cs

| Método | Descrição |
|--------|-----------|
| `DownloadWinpeBaseAsync()` | Baixa WinPE-base.7z do GitHub, extrai com 7z |
| `InjectConfigIntoWimAsync()` | Injeta `shrink_config.ini` + `KL_SHRINK_TARGET.dat` via wimlib `--command` |
| `UpdateWimWithScriptAsync()` | Injeta `startnet.cmd` via wimlib `--command` |

## Arquivo Marker: KL_SHRINK_TARGET.dat

Formato:
```
DISK=0
PART=3
SHRINK=5000
```

Injetado em `X:\Windows\System32\KL_SHRINK_TARGET.dat` dentro do WIM.
Prioridade: valores embutidos > shrink_config.ini > marker > scan manual.

## Log de Exemplo (pós-reboot)

```
[KitLugia WinPE Shrink]
Status: OK
Disk: 0 Part: 3 Size: 5000MB
```

## Observações Importantes

- **Secure Boot**: Boot.wim personalizado NÃO é assinado — desligar Secure Boot no firmware
- **BitLocker**: Se C: tiver BitLocker ativo, NÃO fazer shrink offline sem suspender antes
- **Wimlib**: Usar `--command` em vez de `--command-file` (versão bundled não suporta)
- **DISK_N=0**: É válido (primeiro disco). A validação é `PART_N==0` (partição inválida)
- **PART_N correto**: O scan assign+SOFTWARE é o único método confiável; WMI pode errar

## Caso Real: WMI Errou, Marker Salvou

Em 30/07/2026, num PC de terceiros:

1. **Embedded values** (via WMI `GetDiskPartitionInfo`) continham PART_N errado
2. **Scan manual** das partições inicialmente não encontrou o Windows
3. **Fallback leu `KL_SHRINK_TARGET.dat`** (injetado no WIM em `X:\Windows\System32\`) — continha os valores corretos e o shrink foi executado com sucesso

Isso valida a estratégia de **múltiplos níveis de fallback**: nenhum método isolado é 100% confiável, mas a combinação garante resiliência.

## Troubleshooting

| Erro | Causa | Solução |
|------|-------|---------|
| `0xc0000001` no boot | Secure Boot bloqueia boot.wim | Desligar Secure Boot no BIOS |
| `0xc000007b` no boot | WBM tentou chainload EFI não-Windows | Usar `bootsequence` NVRAM (BootNext) |
| "offline volumes" no shrink | Falta `assign letter=Z` | Verificar startnet.cmd |
| "File not found" wimlib | `--command-file` usado | Trocar por `--command` |
| "was unexpected at this time" | Acento/caractere especial no batch | Manter 100% inglês |
