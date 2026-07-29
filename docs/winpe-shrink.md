# WinPE Shrink — Documentação Completa

## Visão Geral

O **WinPE Shrink** é um sistema que permite reduzir (shrink) partições NTFS que o Windows em execução não consegue reduzir. Ele faz isso bootando um Windows PE mínimo em RAM disk, executando o diskpart de lá (onde o volume alvo está offline e pode ser manipulado livremente), e depois reiniciando de volta ao Windows.

### Arquitetura

```
Fase 1 — Windows (KitLugia GUI)

  WinpeToolsPage / PartitionsPage / WinbootPage
       |
       v
  WinbootManager.PrepareWinpeBoot()
    +-- Obtem WinPE base (GitHub release -> cache LocalAppData)
    +-- Customiza boot.wim com startnet.cmd
    +-- Cria entrada BCD ramdisk (sem afetar Windows)
    +-- Pronto em C:\KL_WINPE\

  WinbootManager.ScheduleWinpeShrink(drive, tamanhoMB)
    +-- Escreve shrink_config.ini (DISK_N, PART_N, SHRINK_MB)
    +-- Injeta config dentro do boot.wim (X:\shrink_config.ini)
    +-- Configura bootsequence (boot unico no WinPE)
    +-- Agenda reboot (shutdown /r /t 10)
                      |
                      v
Fase 2 — WinPE (RAM Disk X:\)

  BIOS/UEFI -> Windows Boot Manager -> BCD -> boot.wim -> RAM disk

  startnet.cmd (em X:\Windows\System32\)
    |
    +-- Batch shrink:
        1. Valores embutidos (DISK_N/PART_N/SHRINK_MB hardcoded)
        2. Ou le X:\shrink_config.ini
        3. Scan: for d(0-3) for p(1-8):
             assign letter=Z
             if exist Z:\Windows\System32\config\SOFTWARE -> found
             remove letter=Z
        4. :run:
             select disk N
             select partition N
             assign letter=Z    <- TRAZ VOLUME ONLINE
             shrink desired=N
             remove letter=Z
        5. wpeutil reboot (volta ao Windows)
```

## Pre-requisitos

### Para o WinPE base (Package Manager/Fabricante)
- **7-Zip** embutido em `Resources/App/7Zip/7z.exe`
- **WinPE base** hospedado no GitHub Releases: `WinPE-base.7z`
  - Contem: `sources/boot.wim` + `sources/boot.sdi` + `efi/`
  - O WinPE base deve ter packages minimos: WMI, NetFX, StorageWMI, Scripting
  - Criado com ADK + `BuildKitLugiaWinpe()` (WinpeBuilder.cs)

### Para o usuario final (Windows)
- Acesso a internet (primeira vez: download do base do GitHub)
- OU WinRE presente (fallback: `WinpeBuilder.UseWinreAsBaseAsync()`)
- UEFI + Secure Boot pode exigir bootmanager Microsoft (padrao, sem alteracoes)
- .NET 10 Desktop Runtime (para o KitLugia GUI)
- Execucao como Administrador

## Estrutura de Arquivos

```
C:\KL_WINPE\                          # Pasta de trabalho (criada pelo Prepare)
+-- boot.wim                           # WinPE customizado com startnet.cmd
+-- boot.sdi                           # RAM disk SDI
+-- shrink_config.ini                  # Config do shrink (backup)

%LOCALAPPDATA%\KitLugia\WinPE\        # Cache persistente do WinPE base
+-- sources\
|   +-- boot.wim                       # WinPE base baixado (nao customizado)
|   +-- boot.sdi
+-- efi\                               # Estrutura EFI para ISO (nao usado no ramdisk)

%ProgramFiles%\KitLugia\WinPE\        # Config de instalacao
+-- shrink_config.ini                  # Backup da ultima config usada
```

## Classes e Metodos Principais

### `WinbootManager.cs`

| Metodo | Descricao |
|--------|-----------|
| `PrepareWinpeBoot()` | Fase 1: baixa/cacheia WinPE base, customiza WIM, cria BCD ramdisk |
| `ScheduleWinpeShrink(drive, shrinkMB)` | Fase 2: escreve config, injeta no WIM, agenda bootsequence, reinicia |
| `IsWinpeReady()` | Verifica se `C:\KL_WINPE\boot.wim` existe |
| `RemoveWinpeAsync()` | Remove BCD entry + deleta `C:\KL_WINPE\` + limpa config |
| `GetDiskPartitionInfo(driveLetter)` | WMI query para DISK_N, PART_N, PART_OFFSET, serial, label |
| `RamdiskStartnetCmd()` | Gera o conteudo do startnet.cmd (string embutida, sem arquivo externo) |
| `CreateRamdiskEntry(desc, drive, wim, sdi)` | Cria entrada BCD /application osloader com device=ramdisk |

### `WinpeBuilder.cs`

| Metodo | Descricao |
|--------|-----------|
| `DownloadWinpeBaseAsync()` | Baixa WinPE-base.7z do GitHub, extrai com 7z |
| `UseWinreAsBaseAsync()` | Fallback: copia winre.wim do Windows e converte para WinPE |
| `CustomizeWinpeWimFlatAsync(wimPath, startnet)` | Monta WIM, injeta startnet.cmd, commita |
| `InjectConfigIntoWimAsync(wimPath, config)` | Monta WIM, injeta shrink_config.ini na raiz (X:\), commita |
| `FindBundled7Zip()` | Localiza 7z.exe (embutido ou system-wide) |

## Fluxo Completo (Passo a Passo)

### 1. Preparar WinPE (sem reboot)
```
Usuario clica "PREPARAR WINPE" -> PrepareWinpeBoot()
```

1. Verifica se `C:\KL_WINPE\boot.wim` ja existe
2. Se nao existe:
   a. Tenta baixar de `https://github.com/luigiarrud4/KitLugia-WinPE/releases/latest/download/WinPE-base.7z`
   b. Extrai com 7z para `%LOCALAPPDATA%\KitLugia\WinPE\`
   c. Se download falhar, tenta copiar de `C:\Windows\System32\Recovery\winre.wim`
3. Resolve `boot.sdi` (cache -> `C:\Windows\Boot\DVD\PCAT\boot.sdi`)
4. Gera `startnet.cmd` via `RamdiskStartnetCmd()`
5. Monta boot.wim com DISM, injeta startnet.cmd, commita
6. Cria entrada BCD ramdisk:
   - `bcdedit /create /d "KitLugia WinPE - Shrink" /application osloader`
   - `bcdedit /set {guid} device ramdisk=[C:]\KL_WINPE\boot.wim,{ramdiskoptions}`
   - `bcdedit /set {guid} osdevice ramdisk=[C:]\KL_WINPE\boot.wim,{ramdiskoptions}`
   - `bcdedit /set {guid} path \windows\system32\boot\winload.efi`
   - `bcdedit /set {guid} winpe yes`
   - `bcdedit /displayorder {guid} /addlast`

### 2. Configurar Shrink (agenda reboot)
```
Usuario seleciona particao + tamanho -> ScheduleWinpeShrink("C", 8000)
```

1. Verifica se `C:\KL_WINPE\boot.wim` existe
2. Detecta DISK_N, PART_N via WMI (`Win32_LogicalDiskToPartition`)
3. Escreve `shrink_config.ini`:
   ```
   DISK_N=0
   PART_N=3
   PART_OFFSET=1234567890
   PART_SIZE=500000000000
   VOL_SERIAL=ABCD-1234
   VOL_LABEL=Windows
   SHRINK_MB=8000
   ```
4. Monta boot.wim com DISM, injeta `shrink_config.ini` na raiz (X:\)
5. Monta boot.wim novamente, injeta `startnet.cmd` com valores embutidos (DISK_N, PART_N, SHRINK_MB)
6. Configura `bcdedit /bootsequence {guid}` (boot UNICO no WinPE)
7. Agenda `shutdown /r /t 10`

### 3. Boot no WinPE (automatico)

1. BIOS/UEFI -> Windows Boot Manager -> BCD -> "KitLugia WinPE - Shrink"
2. Windows carrega boot.wim em RAM disk (X:\)
3. `winpeshl.exe` procura `winpeshl.ini` -- nao existe -> executa `startnet.cmd`
4. `startnet.cmd` executa:
   - `wpeinit` (inicializa rede, drivers, discos)
   - Aguarda 5s (ping -n 5)
   - Tenta valores embutidos (DISK_N, PART_N, SHRINK_MB). Se validos, pula scan.
   - Se nao, le `shrink_config.ini` do WIM (se existir)
   - Se ainda sem valores, scan: discos 0-3, particoes 1-8
   - :run: seleciona disco/particao -> `assign letter=Z` -> `shrink desired=N` -> `remove letter=Z`
   - `wpeutil reboot`

### 4. Pos-reboot

1. Windows inicia normalmente
2. Espaco de 8GB (ou o valor configurado) foi liberado no final da particao
3. Esse espaco pode ser usado pelo Winboot para criar particao KITLUGIA

## O Erro Classico (e a Correcao)

### "You may not shrink OEM, ESP, or recovery partitions, or, offline volumes"
**Causa**: No WinPE RAM disk, a particao do Windows **nao tem letra de drive**. O `select partition N` no diskpart seleciona a particao mas o volume fica **offline**. O shrink requer volume online.  
**Correcao**: Sempre fazer `assign letter=Z` ANTES do `shrink` e `remove letter=Z` DEPOIS.

### "nao was unexpected at this time" (2026-07-24)
**Sintoma**: O batch para com erro de parse após `goto :run`. Nenhum comando executado.  
**Causas**:
1. Texto em português com acentos (`ç`, `ã`, `é`) — o cmd.exe minimalista do WinPE não reconhece bytes não-ASCII
2. `if !VAR! geq 0` sem aspas — frágil, especialmente com valores que podem ser 0
3. `rem` imediatamente após `:run` — causa parse error em alguns WinPEs
4. Blocos `if (...)` aninhados com `if defined`, `if lss` — não funcionam no WinPE básico  
**Correcao**: (1) 100% inglês, (2) `if "!VAR!"=="0"` com aspas, (3) sem comentários após labels, (4) comandos single-line com `&`.

### "O batch ignorou DISK_N=0 e fez scan do zero — por quê?"
O batch usa `if not "!DISK_N!"=="0"` — DISK_N=0 é tratado como "não definido". O embedded value só é usado se DISK_N ≥ 1. Isso é INTENCIONAL: o scan por `SOFTWARE` é o método confiável. DISK_N/PART_N embutidos servem apenas para economizar tempo quando o usuário tem múltiplos discos.

### "Por que o scan encontrou PART=3 em vez de PART=2?"
O Windows pode estar na partição 3 se a partição 1 for ESP (System Reserved) e a 2 for MSR (Microsoft Reserved) ou Recovery. O WMI pode retornar `PartitionNumber` diferente da ordem esperada. O scan é a única detecção confiável.

### Bug Secundario: `^>` no scan
O scan usava `echo ... ^> X:\fs.txt` — o `^>` escapa o `>` no batch, fazendo o echo escrever `>` literal na tela **em vez de redirecionar para o arquivo**. O `X:\fs.txt` nunca era criado. Corrigido trocando `^>` por `>`.

## Regras CRÍTICAS para o startnet.cmd

1. **100% INGLÊS, SEM ACENTOS.** O cmd.exe do WinPE (boot.wim Windows 11) é minimalista. Caracteres como `ç`, `ã`, `é`, `º` causam `{palavra} was unexpected at this time` — o parser não reconhece o byte como parte de um comando válido.
2. **`if` com aspas.** Sempre use `if "!VAR!"=="0"` em vez de `if !VAR! equ 0` ou `if !VAR! geq 0`. Aspas previnem parse errors com variáveis vazias ou com espaços.
3. **Nada após `:run`.** A primeira linha após o label `:run` deve ser um comando executável (`if`, `echo`, etc.). Comentários `rem` após o label causam erro de parse em alguns WinPEs.
4. **Sem blocos aninhados complexos.** Prefira `if cond ( cmd & cmd )` single-line em vez de blocos `(...)` com múltiplas linhas. `if defined`, `if lss`, `if geq` podem falhar.
5. **Scan por `SOFTWARE`.** O único método confiável de detectar a partição Windows é escanear discos/partições, atribuir letra Z:, e verificar `Z:\Windows\System32\config\SOFTWARE`. Valores embutidos (DISK_N) servem apenas como fallback.
6. **`assign letter=Z` obrigatório.** Sem letra de drive, o volume fica offline e o shrink falha.

## Configuracao do startnet.cmd

O startnet.cmd e **gerado em tempo real** pelo metodo `RamdiskStartnetCmd()` e injetado no boot.wim via DISM. Nao existe como arquivo fisico no repositorio.

### Estrutura do startnet.cmd gerado (ATUAL — 2026-07-24, funcionando)
```batch
@echo off
setlocal enabledelayedexpansion
wpeinit
echo KitLugia WinPE - Shrink (RAMDISK)
ping -n 5 127.0.0.1 > nul

rem --- Batch shrink ---
set EMBED_DISK_N=0
set EMBED_PART_N=3
set EMBED_SHRINK_MB=10000
set DISK_N=!EMBED_DISK_N!
set PART_N=!EMBED_PART_N!
set SHRINK_MB=!EMBED_SHRINK_MB!
if not "!DISK_N!"=="0" if not "!PART_N!"=="0" goto :run

rem --- Fallback: read shrink_config.ini from WIM ---
if exist X:\shrink_config.ini (
  for /f "tokens=1,2 delims==" %%a in (X:\shrink_config.ini) do (
    if /i "%%a"=="DISK_N" set DISK_N=%%b
    if /i "%%a"=="PART_N" set PART_N=%%b
    if /i "%%a"=="SHRINK_MB" set SHRINK_MB=%%b
  )
)
if not "!DISK_N!"=="0" if not "!PART_N!"=="0" goto :run

echo Scanning disks for Windows partition...
for /l %%d in (0,1,3) do (
  for /l %%p in (1,1,8) do (
    echo select disk %%d > X:\fs.txt
    echo select partition %%p >> X:\fs.txt
    echo assign letter=Z >> X:\fs.txt
    diskpart /s X:\fs.txt >nul 2>&1
    if exist Z:\Windows\System32\config\SOFTWARE (
      set DISK_N=%%d & set PART_N=%%p
      echo select volume Z > X:\fr.txt
      echo remove letter=Z >> X:\fr.txt
      diskpart /s X:\fr.txt >nul 2>&1
      echo Found Windows: DISK=%%d PART=%%p
      goto :run
    )
    echo select volume Z > X:\fr.txt 2>nul
    echo remove letter=Z >> X:\fr.txt
    diskpart /s X:\fr.txt >nul 2>&1
  )
)
:run
if "!PART_N!"=="0" ( echo ERROR: Target partition not found. Rebooting... & wpeutil reboot )
echo select disk !DISK_N! > X:\s.txt
echo select partition !PART_N! >> X:\s.txt
echo assign letter=Z >> X:\s.txt
echo shrink desired=!SHRINK_MB! >> X:\s.txt
echo remove letter=Z >> X:\s.txt
diskpart /s X:\s.txt
echo Shrink done. Writing persistent log...
echo [KitLugia WinPE Shrink] > X:\result.log
echo Status: OK >> X:\result.log
echo Disk: !DISK_N! Part: !PART_N! Size: !SHRINK_MB!MB >> X:\result.log
rem --- Mirror log to Windows partition ---
echo select disk !DISK_N! > X:\l.txt
echo select partition !PART_N! >> X:\l.txt
echo assign letter=Z >> X:\l.txt
diskpart /s X:\l.txt >nul 2>&1
if exist Z:\ (
  copy /y X:\result.log Z:\KitLugia_WinPE_Log.txt >nul
  echo select volume Z > X:\lr.txt
  echo remove letter=Z >> X:\lr.txt
  diskpart /s X:\lr.txt >nul 2>&1
  echo Log saved to Z:\KitLugia_WinPE_Log.txt
) else (
  echo WARNING: Could not reassign Z: for persistent log
)
echo Rebooting...
wpeutil reboot
```

## Arquivos Modificados

| Arquivo | O que faz |
|---------|-----------|
| `KitLugia.Core/WinbootManager.cs` | `PrepareWinpeBoot()`, `ScheduleWinpeShrink()`, `IsWinpeReady()`, `RemoveWinpeAsync()`, `GetDiskPartitionInfo()`, `RamdiskStartnetCmd()` |
| `KitLugia.Core/WinpeBuilder.cs` | `DownloadWinpeBaseAsync()`, `UseWinreAsBaseAsync()`, `CustomizeWinpeWimFlatAsync()`, `InjectConfigIntoWimAsync()`, `InjectStartnetCmdIntoWimAsync()` |
| `KitLugia.GUI/Pages/WinpeToolsPage.xaml` (+ .cs) | UI: Step 1 "Preparar WinPE", Step 2 "Shrink WinPE", "Remover WinPE" |
| `KitLugia.GUI/Pages/PartitionsPage.xaml` (+ .cs) | Botao "Shrink via WinPE" na sidebar |
| `KitLugia.GUI/Pages/WinbootPage.xaml.cs` | Pre-check de espaco antes de criar boot, sugere WinPE |

## Build

```powershell
dotnet build
```

## Manutencao

### Para atualizar o WinPE base
1. Tenha ADK instalado ou use `WinpeBuilder.BuildKitLugiaWinpe()`
2. O .7z gerado deve conter: `sources/boot.wim` + `sources/boot.sdi` + `efi/boot/bootx64.efi`
3. Faca upload como GitHub Release
4. Atualize `WINPE_BASE_URL` em `WinpeBuilder.cs`

### Para adicionar packages ao WinPE base
Edite `WinpeBuilder.AddPackagesToWimAsync()` para incluir `dism /Add-Package` com packages adicionais do ADK.

### Logs de diagnostico
- Durante o shrink em WinPE: `X:\s.txt`, `X:\fs.txt`, `X:\fr.txt`, `X:\result.log`
- No KitLugia GUI: guia LOG DETALHADO em cada pagina

## Troubleshooting

### "You may not shrink OEM, ESP, or recovery partitions, or, offline volumes"
**Causa**: Falta `assign letter=Z` antes do shrink.
**Solucao**: Verificar se o `:run` no startnet.cmd contem `assign letter=Z` antes de `shrink desired`.

### "Windows nao encontrado" no scan
**Causa**: Nenhuma particao contem `\Windows\System32\config\SOFTWARE`.
**Solucao**: Verificar se o disco tem Windows instalado. O scan so busca discos 0-3 e particoes 1-8.

### WinPE nao boota
**Causa**: BCD entry corrompida ou boot.wim ausente.
**Solucao**: Reexecutar "Preparar WinPE" ou usar "Remover WinPE" e tentar novamente.

### "boot.wim nao encontrado"
**Causa**: Execute "Preparar WinPE" primeiro.
**Solucao**: Passo 1 na WinpeToolsPage.
