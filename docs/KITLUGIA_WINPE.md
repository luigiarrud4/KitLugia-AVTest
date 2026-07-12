# KitLugia WinPE — Fluxo de Operação

## Princípio Fundamental

**O WinPE faz o trabalho pesado.** O Windows normal nunca tenta fazer um shrink grande.
O Windows só faz um shrink mínimo para criar espaço para o WinPE — e o WinPE faz todo o
resto, exatamente como EaseUS Partition Master, MiniTool, etc.

```
┌─────────────────────────────────────────────────────────────────────────┐
│  FASE 1 (Windows)                  FASE 2 (WinPE)         FASE 3       │
│                                                                         │
│  shrink PEQUENO (~1 GB)   reboot   shrink GRANDE (N GB)   reboot       │
│  cria partição WinPE      ─────►   cria partição alvo     ─────►  OK!  │
│  copia boot.wim + BCD              remove entrada BCD                   │
│  agenda reboot                     reinicia                             │
└─────────────────────────────────────────────────────────────────────────┘
```

### Por que isso funciona

- No WinPE, C: é um volume de dados **offline** (X: é o boot)
- Pagefile.sys, hiberfil.sys, registry hives — todos ausentes/desmontados
- `diskpart shrink` enxerga **todo o espaço livre real** sem limitação de 2.5 GB
- Se mesmo assim o shrink falhar, o WinPE tem opções: chkdsk, defrag, shrink query

---

## Como o WinPE é construído

**Base:** `boot.wim` do Windows 11 ISO (arquivo oficial da Microsoft, 597 MB)

Não usamos Sergei Strelec, nem antiX Linux, nem ADK WinPE add-on.
Usamos o `boot.wim` que já vem no ISO de instalação do Windows 11 —
é um WinPE completo da própria Microsoft.

### Build: `Build-KitLugiaWinPE.cmd`

1. Monta `H:\sources\boot.wim` (índice 2) com DISM
2. Substitui `Windows\System32\startnet.cmd` → chama `X:\KitLugiaPE.cmd`
3. Adiciona `KitLugiaPE.cmd` na raiz do WIM
4. Desmonta e commita
5. Copia boot.wim + bootmgr + boot.sdi + BCD + EFI boot sectors para `media\`
6. Executa `oscdimg.exe` para gerar `KitLugiaPE.iso`

**Nenhuma dependência externa — apenas ADK Deployment Tools (oscdimg) + DISM.**

---

## Fluxo Completo (C#)

### `WinbootManager.PrepareWinpeBoot()` — Fase 1 (roda no Windows)

```
1. Shrink C: em ~1 GB              (sempre funciona, shrink pequeno)
2. Cria partição primária          (NTFS, label KITLUGIA_WINPE, letra A:)
3. Extrai KitLugiaPE.iso p/ A:     (ou copia boot.wim + boot.sdi)
4. Cria entrada BCD ramdisk        (aponta para A:\boot.wim)
5. Agenda reboot
```

**Nunca tenta fazer shrink grande no Windows.** O shrink grande é responsabilidade
exclusiva do WinPE.

### `WinbootManager.ContinueShrinkInWinpe()` — Fase 2 (roda no WinPE)

Este método é substituído pelo `KitLugiaPE.cmd` que executa automaticamente
dentro do WinPE através do `startnet.cmd` modificado:

```
1. Detecta drive do Windows        (procura \Windows\System32\config\SOFTWARE)
2. CHKDSK /f                       (corrige filesystem antes)
3. shrink query                    (consulta shrink máximo possível)
4. shrink desired=N minimum=N      (shrink agressivo)
5. Se falhar: tenta sem minimum
6. Se falhar: oferece DiskPart manual
7. Remove entrada BCD do WinPE     (para não loop infinito)
8. wpeutil reboot                  (volta ao Windows)
```

---

## Arquivos

| Arquivo | Descrição |
|---------|-----------|
| `C:\WinPE_KitLugia\KitLugiaPE.cmd` | Script que roda dentro do WinPE |
| `C:\WinPE_KitLugia\Build-KitLugiaWinPE.cmd` | Script builder (admin) |
| `C:\WinPE_KitLugia\KitLugiaPE.iso` | ISO final (~600 MB) |
| `KitLugia.Core\WinpeBuilder.cs` | Builder C# (ADK copype path — obsoleto) |
| `KitLugia.Core\WinbootManager.cs` | `PrepareWinpeBoot()` + `ContinueShrinkInWinpe()` |
| `KitLugia.GUI\Pages\ShrinkPage.xaml` | Interface de shrink (modo WinPE) |

## Regras (nunca esquecer)

1. **Windows nunca faz shrink grande.** Shrink grande é no WinPE.
2. **WinPE é construído do boot.wim do Windows 11**, não de Sergei Strelec ou antiX.
3. **Fase 1** prepara o terreno (shrink pequeno + partição + BCD).
4. **Fase 2** faz o trabalho real (shrink grande + partições).
5. **Fase 3** é só reboot e fim.
