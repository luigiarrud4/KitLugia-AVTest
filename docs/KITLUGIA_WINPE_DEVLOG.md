# KitLugia WinPE — Dev Log

## Visão Geral

WinPE customizado para shrink de partição offline (estilo EaseUS Partition Master).
Construído a partir do `boot.wim` do Windows 11 — sem Sergei Strelec, sem third-party.

---

## O Problema Original

O Windows **não permite shrink acima de ~3 GB** enquanto o sistema está rodando
por causa de "arquivos imóveis" (pagefile, hibernate, MFT, etc.).

A solução EaseUS-like:
1. Windows faz um shrink **pequeno** (~1 GB) só pra criar uma partição WinPE
2. **WinPE boota** e faz o shrink **real** (centenas de GB) — OFFLINE, sem limites
3. Reboot de volta ao Windows

---

## Fases do Fluxo Correto

| Fase | Ambiente | Ação |
|------|----------|------|
| **1** | Windows | Shrink mínimo (~1 GB) → cria partição WinPE → copia boot.wim customizado → configura BCD → reboot |
| **2** | WinPE | winpeshl.ini → KitLugiaPE.cmd → CHKDSK → shrink query → shrink desired=N minimum=N (offline) → opcional: cria partição extra → reboot |
| **3** | Windows | Remove partição WinPE → estende C: → remove entrada BCD |

---

## Arquivos Construídos

### `C:\WinPE_KitLugia\KitLugiaPE.iso`
- **Tamanho**: 612 MB (641.976.320 bytes)
- **Base**: Windows 11 boot.wim (index 2, WinPE)
- **Boot**: BIOS (etfsboot.com) + UEFI (efisys.bin / cdboot.efi)
- **Conteúdo**:
  ```
  \bootmgr
  \bootmgr.efi
  \bcd
  \boot.sdi
  \etfsboot.com
  \efi\microsoft\boot\cdboot.efi
  \efi\microsoft\boot\cdboot_noprompt.efi
  \efi\microsoft\boot\efisys.bin
  \sources\boot.wim
  ```

### `boot.wim` (customizado, 629 MB)
- Index 2 (WinPE) modificado via DISM
- **Arquivos injetados**:
  - `\KitLugiaPE.cmd` — script principal de shrink
  - `\Windows\System32\winpeshl.ini` — auto-start do KitLugiaPE.cmd
  - `\Windows\System32\startnet.cmd` — redireciona para KitLugiaPE.cmd

### `KitLugiaPE.cmd`
- Local: `C:\WinPE_KitLugia\KitLugiaPE.cmd`
- Menu interativo com opções:
  1. Shrink automático completo (recomendado)
  2. DiskPart manual
  3. Prompt de comando
  4. CHKDSK + Defrag
  5. Testar shrink máximo (query)
  6. Reiniciar

- Fluxo do shrink automático:
  1. Detecta drive do Windows (C: ou varre A-Z)
  2. CHKDSK /f
  3. `shrink query` para descobrir máximo possível
  4. `shrink desired=N minimum=N` (tentativa principal)
  5. Fallback: `shrink desired=N` (sem minimum)
  6. Fallback: CHKDSK + Defrag + tentar novamente

### Scripts de Build

#### `Build-KitLugiaWinPE.ps1`
- PowerShell script para construir o ISO do zero
- Passos:
  1. Monta ISO do Windows 11
  2. Copia boot.wim (read-only fix)
  3. Copia arquivos de boot (bootmgr, boot.sdi, BCD, etfsboot.com, efisys.bin, cdboot.efi)
  4. Monta boot.wim index 2 com DISM
  5. Injeta KitLugiaPE.cmd
  6. Cria winpeshl.ini
  7. Cria startnet.cmd de redirecionamento
  8. Commit + desmonta
  9. Gera ISO com oscdimg.exe (ADK)

#### `Build-KitLugiaPE.cmd`
- Batch com auto-elevação via PowerShell (`Start-Process -Verb RunAs`)
- Mesmo fluxo do PS script

---

## Histórico de Decisões

### ❌ Strelec WIM abandonado
- Sergei Strelec PE (1.5 GB+) tinha muito bloat
- Dependência de terceiros
- Substituído pelo boot.wim do Windows 11 (597 MB)

### ❌ copype.cmd / MakeWinPEMedia não usados
- WinPE add-on do ADK NÃO está instalado
- boot.wim do Windows 11 já é um WinPE completo
- ADK Deployment Tools (dism + oscdimg) são suficientes

### ✅ winpeshl.ini é crítico
- boot.wim do Windows 11 tem `HKLM\SYSTEM\Setup\CmdLine` apontando para `SetupPlatform.exe`
- Sem `winpeshl.ini`, o WinPE boota direto para o Windows Setup
- `winpeshl.ini` força `X:\KitLugiaPE.cmd` a executar, ignorando o CmdLine

### ✅ Read-Only attribute
- boot.wim vindo da ISO tem atributo Read-Only
- DISM não monta WIMs read-only para escrita
- `attrib -R boot.wim` antes de montar

---

## Problemas no Código C# Atual

### `WinbootManager.cs:4400` — `ContinueShrinkInWinpe()`
- **Roda no Windows**, não no WinPE
- Chamado de `ShrinkPage.xaml.cs:491` (botão na UI)
- Shrink ainda limitado a ~3 GB

### `WinbootManager.cs:4282` — `PrepareWinpeBoot()`
- Tenta `ShrinkPartitionUsingWMI()` primeiro
- Se falha, tenta `ShrinkPartitionUsingRunOnceAdvanced()`
- Depois faz outro shrink via diskpart + cria partição A
- **Dois shrinks em sequência**: o segundo pode falhar porque o primeiro já consumiu o espaço

### WIM baixado do GitHub
- `PrepareWinpeBoot()` baixa WIM genérico de `github.com/KitLugia/KitLugia/releases/...`
- Esse WIM **não tem** `winpeshl.ini` nem `KitLugiaPE.cmd`
- O WinPE boota e para no prompt — ninguém executa o shrink

### `WinpeBuilder.cs` desatualizado
- `GenerateShrinkScriptContent()` gera script antigo (sem winpeshl.ini)
- `BuildKitLugiaWinpe()` usa copype (não temos WinPE add-on)
- `MountAndCustomize()` monta index 1 em vez de index 2

---

## Pendências

- [ ] Integrar `KitLugiaPE.iso` no fluxo C# (ou construir on-the-fly)
- [ ] Reescrever `PrepareWinpeBoot()` — shrink mínimo de ~1 GB + copiar boot.wim customizado
- [ ] Remover `ContinueShrinkInWinpe()` do C# — WinPE faz tudo sozinho
- [ ] Adicionar fase de cleanup pós-WinPE (remover partição WinPE, estender C:)
- [ ] Testar `KitLugiaPE.iso` em VM
- [ ] Atualizar `WinpeBuilder.cs` para usar Windows 11 boot.wim + winpeshl.ini

---

## Comandos Úteis

```powershell
# Montar boot.wim
dism /Mount-Image /ImageFile:C:\WinPE_KitLugia\boot.wim /Index:2 /MountDir:C:\WinPE_KitLugia\mount

# Injetar arquivos
copy C:\WinPE_KitLugia\KitLugiaPE.cmd C:\WinPE_KitLugia\mount\
copy C:\WinPE_KitLugia\winpeshl.ini C:\WinPE_KitLugia\mount\Windows\System32\

# Commit
dism /Unmount-Image /MountDir:C:\WinPE_KitLugia\mount /Commit

# Gerar ISO
oscdimg.exe -m -o -u2 -udfver102 -bootdata:2#p0,e,bC:\WinPE_KitLugia\media\boot\etfsboot.com#pEF,e,bC:\WinPE_KitLugia\media\efi\microsoft\boot\efisys.bin C:\WinPE_KitLugia\media C:\WinPE_KitLugia\KitLugiaPE.iso
```
