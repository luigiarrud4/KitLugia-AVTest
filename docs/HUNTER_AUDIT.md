# Auditoria do Hunter (KitLugia Spy / HunterWindow) — 31/10/2026

Escopo: `KitLugia.GUI/Windows/HunterWindow.xaml.cs` (1000 linhas). Problemas
identificados por leitura de código + prova em runtime (guards via reflection +
`PerformCleanup` real). Build GUI: 0 erros.

## Problemas identificados e corrigidos

| # | Gravidade | Problema | Correção |
|---|---|---|---|
| 1 | **CRÍTICO** | `BtnKillDel_Click` fazia `Directory.Delete(d, true)` direto na pasta do processo alvo — **sem** `IsProhibitedLocation`/`IsKitLugiaSelfPath`/`IsTooBroadForDeletion`/`IsExcludedPath`, sem backup, sem lixeira. Alvo de sistema (ex.: `svchost` em `System32`) → tentava apagar o `System32`. | Agora chama `CaptureCurrentInfo()` e roteia a pasta pelo `PerformCleanup(..., forceToRecycleBin: true)`: guards + backup obrigatório + Lixeira. Prova: reflection mostrou todos os caminhos perigosos bloqueados (`C:\Windows\System32`, `C:\Windows`, `C:\`, `C:\Program Files`, `C:\Program Files\app.dll`, `C:\Users` → BLOQUEADO=True) e um temp legítimo passou (`FilesDeleted=1`, foi para a lixeira). |
| 2 | Alto | `BtnKillDel_Click` não chamava `CaptureCurrentInfo()` (o `BtnKill` chamava) → PID obsoleto; reuso de PID podia matar processo errado. | `CaptureCurrentInfo()` no início + `if (_detectedPid == 0) return`. |
| 3 | Médio | Vazamento do contorno dourado (overlay): `StopHunting(showMenu:true)` retornava **antes** de `DestroyOverlay()` → retângulo dourado ficava na tela após escolher a ação do menu. | `DestroyOverlay()` movido para antes do ramo do menu (sempre destrói). |
| 4 | Médio (perf) | `HuntTimer_Tick` chamava `Process.GetProcessById` + `MainModule.FileName` a **cada tick de 30 ms**, mesmo parado na mesma janela. | Nome/caminho agora cacheados (`_curName`/`_curPath`) e só recalculados quando `hWnd` muda. |
| 5 | Médio | Limpezas manuais do Hunter (`BtnUninstall` forçado e o deep-cleanup) chamavam `PerformCleanup` **sem** `forceToRecycleBin` → ficavam sujeitas ao setting global, diferente dos botões manuais do AppsPage. | Passam `forceToRecycleBin: true` (vão para a Lixeira, com backup obrigatório). |
| 6 | Baixo | `BtnUninstall` procurava desinstalador só no HKLM `Uninstall` → apps por-usuário caiam no "forçar remoção". | Loop sobre HKLM **e** HKCU. |
| 7 | Baixo | `BtnProperties` fazia o mesmo que `Abrir pasta` (`explorer /select`) — não abria propriedades. | `Process.Start` com `Verb="properties"`, fallback para `/select`. |
| 8 | **Alto** | Hit-test ignorava a janela da frente (ver detalhe abaixo). | `ChildWindowFromPointEx` com `CWP_SKIPTRANSPARENT` + fallback `WindowFromPoint`; `FindBestChild` pula `WS_EX_TRANSPARENT`. |
| 9 | Extra | Botões Kill/Kill+Del do Hunter usavam só `Process.Kill()` (falha em processo teimoso/protegido). | Agora usam `ForceStopUnlockService.ForceKillProcess(pid)` (novo wrapper público do force stop: Kill em árvore + `NtTerminateProcess` nativo com `SeDebugPrivilege`, com guardas de sistema/PPL), com fallback no Kill simples. |

## Verificação

- Build `KitLugia.GUI` Release: `Compilação com êxito, 0 Erro(s)`.
- Guards (reflection em `DeepUninstaller`): ver tabela acima — caminhos perigosos
  bloqueados, caminho legítimo liberado.
- `PerformCleanup(temp, forceToRecycleBin:true)`: removeu do disco e foi para a
  Lixeira (comportamento já provado end-to-end no probe do `RecycleBinOp.Recycle`:
  `permanent=false` → `Recycled` + `NA_LIXEIRA=True`).

## Observações (não são bug, mas anotadas)

- `OnClosing` já limpava timers e todos os forms (OK).
- `WindowFromPointEx` (FindBestChild estilo WinSpy++) está correto; o overlay é
  atualizado em `UpdateInfo` (OK).
- O hunt inicia no LMB do botão finder e para no release do LMB (com menu) ou RMB
  (sem menu) — design intencional.

---

## DETALHE — como foi resolvido o problema "o Hunter ignora a janela da frente" (problema #8)

**Sintoma (reportado pelo usuário):** Opera em tela cheia; um app em janela por cima.
Passar o mouse no Opera → o Hunter pega o Opera. Passar o mouse no app em janela na
frente → o Hunter **ignora** (fica preso no Opera).

**Causa raiz:** a mira (crosshair), o contorno (overlay) e o balão de info do próprio
Hunter são janelas `TopMost = true` + `WS_EX_TRANSPARENT` que **seguem o cursor**. Nessas
condições o `WindowFromPoint(x, y)` podia devolver a própria janela do Hunter (topmost no
ponto). O código antigo fazia:

```csharp
IntPtr raw = WindowFromPoint(x, y);
if (raw == IntPtr.Zero || GetWindowThreadProcessId(raw, out _) == _selfPid)
    raw = WindowFromPhysicalPoint(x, y);          // ainda devolvia a janela do Hunter
if (raw == IntPtr.Zero || GetWindowThreadProcessId(raw, out _) == _selfPid)
    return IntPtr.Zero;                            // BUG: ignora em vez de pular para a janela de trás
```

Quando devolvia `IntPtr.Zero`, o `HuntTimer_Tick` não trocava `_currentHwnd` (o `else if`
exige `hWnd != _currentHwnd` e `hWnd != 0`), então o Hunter **mantinha o alvo anterior** —
exatamente o sintoma.

**A correção (padrão de ferramentas spy, ex.: WinSpy++):** usar
`ChildWindowFromPointEx` no desktop com `CWP_SKIPTRANSPARENT`, que **pula janelas
transparentes** — ou seja, pula a própria mira/overlay/info do Hunter e devolve o app real
embaixo:

```csharp
var pt = new POINT { X = x, Y = y };
IntPtr raw = ChildWindowFromPointEx(GetDesktopWindow(), pt,
    CWP_SKIPINVISIBLE | CWP_SKIPDISABLED | CWP_SKIPTRANSPARENT);
if (raw == IntPtr.Zero || GetWindowThreadProcessId(raw, out _) == _selfPid)
    raw = WindowFromPoint(x, y);                   // fallback
if (raw == IntPtr.Zero || GetWindowThreadProcessId(raw, out _) == _selfPid)
    return IntPtr.Zero;
```

E no `FindBestChild` (EnumChildWindows) agora também se pula `WS_EX_TRANSPARENT`:

```csharp
if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0) return true;
```

**Prova em runtime** (probe `winprobe`: janela `back` grande + janela `front` por cima +
overlay transparente topmost no cursor, imitando a mira):

```
[sobre a JANELA DA FRENTE] esperado=FRONT   OLD=outro   NEW=FRONT   ✅
```

A lógica nova seleciona a janela da frente; a antiga não. Build GUI: 0 erros.

**Como re-testar no Hunter real:** Opera fullscreen atrás + app em janela na frente →
segurar a mira: em cima do Opera pega Opera; em cima do app da frente **tem que trocar**
para o app. Se um app específico ainda for ignorado, ele pode ser `WS_EX_LAYERED`
(Chromium/Electron) — nesse caso avisa que ajustamos para cobrir layered também.

**P/Invokes adicionados** (perto das outras, no topo do arquivo):
`GetDesktopWindow`, `ChildWindowFromPointEx` + consts `CWP_SKIPINVISIBLE/SKIPDISABLED/SKIPTRANSPARENT`.

---

## Extra — Kill do Hunter agora usa o Force Stop do kit (problema #9)

`BtnKill_Click` e `BtnKillDel_Click` antes usavam só `Process.Kill()`. Agora chamam:

```csharp
ForceStopUnlockService.ForceKillProcess((int)_detectedPid)
```

Novo wrapper público em `ForceStopUnlockService` que expõe o force stop existente:
habilita `SeDebugPrivilege` e chama o `KillProcess` interno (Kill na árvore + 
`NtTerminateProcess` nativo). **Guards preservadas:** nunca mata processo de sistema
(`SystemProcessNames`), nunca mata o próprio KitLugia, e detecta PPL (AV/EDR) — nesse
caso reporta que não dá para encerrar do user-mode. Fallback para o `Process.Kill()`
simples se o serviço não conseguir.
