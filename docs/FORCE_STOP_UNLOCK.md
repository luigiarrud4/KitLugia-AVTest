# Force Stop Unlock — Documentação Completa

## Visão Geral

O Force Stop Unlock é uma ferramenta integrada ao KitLugia que libera arquivos bloqueados por processos, drivers, ou handles no Windows. Suporta **todos os tipos de arquivo**: `.sys`, `.dll`, `.exe`, e qualquer outro.

## Pipeline garantido (28/09/2026) — "sempre funciona, sem parar no meio"

Antes: cada botão fazia UMA coisa (matar processos OU assumir dono OU deletar) e **parava no
primeiro obstáculo** — pedia para relançar elevado e encerrava o clique, ou devolvia "falhou"
quando o arquivo continuava bloqueado. Agora existe um único pipeline, usado pela página e
pelo menu de contexto:

```
FileOpGuarantee.Run(path, action, recursive, fullControl)
  0. Habilita TODOS os privilégios (Debug, TakeOwnership, Backup, Restore, LoadDriver)
  1. Valida o caminho com probe NATIVO (distingue "não existe" de "ACL nega")
  2. Destrava o acesso: TakeOwnership quando a ACL nega (O(1) + fallback takeown/icacls)
  3. Bloqueadores: Restart Manager → handles nativos → handle64 → scan de driver (.sys)
  4. Ação: ForceStop (confere com handle EXCLUSIVO) | TakeOwnership (confere acesso) |
     Delete (6 métodos; pasta recursiva)
  5. Nunca beco sem saída: se ainda houver handle ativo, agenda a remoção no próximo boot
     (PendingFileRenameOperations) e reporta isso como SUCESSO agendado
```

### Sem tirar o usuário da tela (um único UAC)

- `KitLugia.GUI/Services/ElevatedFileOpRunner.cs` — se o processo **não** estiver elevado,
  dispara este mesmo exe como `--worker` com `Verb=runas` (1 UAC), acompanha o progresso por
  arquivo e devolve o resultado para a MESMA página. Se o UAC for cancelado, roda in-process
  no melhor esforço e o resultado diz o que exigia administrador (nada de no-op silencioso).
- `Program.cs` — modo `--worker`: parse de `--action|--path|--recursive|--full-control|
  --result|--progress`, roda ANTES do mutex/da UI (o app principal continua dono da janela) e
  publica uma linha de progresso por passo. O menu de contexto (`--unlock`/`--takeown`, com
  toast) passou a usar o MESMO pipeline.

### Bugs de raiz corrigidos (afetavam "sempre falha")

1. **LUID com alinhamento errado — privilégio NUNCA era habilitado.**
   `TOKEN_PRIVILEGES` estava declarado com `int PrivilegeCount; long Luid; int Attributes;`
   (ou `Pack = 1`). O LUID nativo é `{ DWORD; LONG }` com alinhamento **4**: o campo caía no
   offset 1/8, o Windows lia um LUID inválido e `AdjustTokenPrivileges` devolvia
   **TRUE + ERROR_NOT_ALL_ASSIGNED (1300)** — ou seja, SeDebug/SeTakeOwnership/SeBackup/
   SeRestore **nunca entravam** (o take ownership in-process só funcionava quando caía no
   fallback `takeown.exe`; o scan nativo de handles não achava processo de outro usuário).
   Corrigido com uma struct `LUID` explícita em: `ForceStopUnlockService`, `FileTakeOwnership`,
   `RegistryOwnership` (que não tinha sequer o `PrivilegeCount`), `MemoryOptimizer`,
   `TrayIconService` e no novo `FileOpGuarantee`.
   Evidência (antes → depois, `--worker`):
   `Privilégios habilitados: nenhum` → `SeDebugPrivilege, SeTakeOwnershipPrivilege,
   SeBackupPrivilege, SeRestorePrivilege, SeLoadDriverPrivilege`.
2. **Detecção de elevação**: passou a usar `TokenElevation` (`FileOpGuarantee.IsElevated()`)
   em vez de `IsInRole(Administrator)`, que pode dizer "true" com token filtrado (UAC ligado) —
   era o caso em que o app não elevava e falhava em item protegido.
3. **`EnsureElevatedForFileOp` removido** da página: perguntava "relançar elevado?" e
   encerrava o clique (com SIM, a outra instância fazia a operação em outro lugar, sem
   resultado na tela; com NÃO, rodava sem privilégio e falhava).

### Evidências dos testes headless (28/09)

| Cenário | Comando | Resultado |
|---|---|---|
| Arquivo travado por outro processo (PowerShell com handle exclusivo) | `--worker --action delete` | 1 bloqueador → handle fechado via Restart Manager → `File.Delete` OK, `R|OK|1`, arquivo removido |
| Árvore com dono do usuário | `--worker --action takeown --recursive` | 5/5 itens, dono `Administradores`, "Acesso confirmado" |
| Arquivo travado (só liberar) | `--worker --action force-stop` | handle fechado, "Caminho liberado: nenhum handle ativo restante", arquivo intacto |

### A TESTAR no app (usuário)

- [ ] Windows → card **Force Stop Unlock** → colar um caminho → **Liberar Selecionados**
      (com o Kit NÃO elevado: aceitar o UAC uma vez e ver o progresso chegar na própria página)
- [ ] Aba **Take Ownership** → **Assumir** em item protegido (ex: `C:\Windows.old`)
- [ ] **Tentar Deletar** em arquivo travado → sucesso imediato ou "agendado para o próximo boot"
- [ ] Botão direito no Explorer → Force Stop / Take Ownership (toast com o resultado)

## Arquitetura

### Fluxo Principal

```
┌─────────────────────────────────────────────────────────┐
│  Explorer Context Menu / KitLugia UI / IPC              │
│  "Force Stop Unlock" → KitLugia.GUI.exe --unlock "path" │
└────────────────────────┬────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────┐
│  FindBlockingProcesses(path)                            │
│                                                         │
│  1. Restart Manager API (RmGetList)                     │
│  2. Native Handle Enumeration (NtQuerySystemInformation) │
│  3. Handle Tool (handle64.exe) [fallback]               │
│  4. Driver Scan (SCM + Registry + sc query)             │
└────────────────────────┬────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────┐
│  Unlock(path, blockers)                                 │
│                                                         │
│  Phase 1: Restart Manager shutdown (fecha handles)      │
│  Phase 2: Kill processos da pasta alvo                  │
│  Phase 3: Descarregar drivers (SCM/NtUnloadDriver/sc)  │
│  Phase 4: Fechar handles individuais                    │
│  Phase 5: Deleção robusta com retry (6 métodos)        │
│  Phase 6: Kill processos restantes                      │
│  Phase 7: Verificação final                             │
└─────────────────────────────────────────────────────────┘
```

### Detecção de Bloqueadores

#### 1. Restart Manager API
- **O que faz**: Detecta processos que tenham handles de arquivo abertos para o alvo
- **Como funciona**: `RmStartSession` → `RmRegisterResources` → `RmGetList` → `RmShutdown`
- **Cobertura**: Arquivos, DLLs, executáveis — qualquer handle de arquivo
- **Limitação**: Não encontra handles do próprio processo

#### 2. Native Handle Enumeration (NtQuerySystemInformation)
- **O que faz**: Enumera TODOS os handles abertos no sistema
- **Como funciona**: `NtQuerySystemInformation(SystemHandleInformation)` → `NtQueryObject(ObjectNameInformation)`
- **Cobertura**: File, Section (memória mapeada), Key, e todos os tipos de handle
- **Vantagem**: Encontra DLLs carregadas via `LoadLibrary` (Section handles)

#### 3. Handle Tool (handle64.exe) — Fallback
- **O que faz**: Identifica e fecha handles individuais de processos específicos
- **Uso**: Apenas quando os métodos nativos não encontram nada

#### 4. Driver Scan
- **SCM Enum**: `EnumServicesStatusEx` com type=0 (todos os tipos)
- **`sc query state=all`**: Busca TODOS os serviços (incluindo Win32 como WinDivert)
- **Registry Scan**: `HKLM\SYSTEM\CurrentControlSet\Services` com matching fuzzy
- **Loaded Driver List**: Compara nomes dos .sys na pasta com drivers carregados

### Matching Fuzzy para Serviços

A função `ServiceMatchesSysFiles()` resolve o problema de nomes diferentes:

```
Serviço: "WinDivert1.4"  ↔  Arquivo: "WinDivert64.sys"
Serviço: "MyDriver"      ↔  Arquivo: "MyDriver64.sys"
```

Métodos de matching:
1. ImagePath filename match exato
2. Service name contém .sys base name (ou vice-versa)
3. Remove dígitos e compara ("WinDivert64" → "WinDivert")

### Deleção Robusta (6 métodos com retry)

```
1. File.Delete (.NET normal)
2. cmd /c del /f /q
3. NtSetInformationFile (FILE_DISPOSITION_INFO_DELETE)
4. Rename + delete
5. MoveFileEx (delete on reboot)
6. Verificação final
```

Cada método é tentado com retry (3x, 1.5s delay entre tentativas).

### Menu de Contexto

**Fluxo IPC (corrigido em 29/09/2026 — o clique SEMPRE abre o Kit):**
```
Explorer → Clique direito → "Force Stop Unlock (KitLugia)" / "Take Ownership (KitLugia)"
  → KitLugia.GUI.exe --unlock|--takeown "C:\path\to\file"
  → Se o Kit JÁ está rodando: IPC via Named Pipe
      → MainWindow.ShowAndActivateFromTray()  (mostra a janela mesmo na BANDEJA)
      → navega para a página com o path preenchido
      → auto-análise + pipeline garantido (1 UAC se o alvo exigir admin)
  → Se o Kit NÃO está rodando: esta instância abre a UI em modo --unlock/--takeown
      (App.OnStartup) e faz o mesmo acima
```

**Registro:** `HKCU\Software\Classes\{*,Directory,Drive}\shell\<chave>\command`
- `forcestopunlock` → `"KitLugia.GUI.exe" --unlock "%1"`
- `kittakeown` → `"KitLugia.GUI.exe" --takeown "%1"` (entrada **única** de Take Ownership)

**Toggle:** On/Off na ForceStopUnlockPage atualiza o registro
- `SystemTweaks.AddForceStopUnlock()` / `RemoveForceStopUnlock()`
- `SystemTweaks.AddTakeOwnershipKit()` / `RemoveTakeOwnershipKit()` — este último também
  apaga as variantes legadas (`kit_takeownership`, `kit_takeown`, `kit_takeown_super`)

### Entrada única + clique que abre o Kit (29/09/2026)

Dois bugs reportados num clique do Explorer: **nada acontecia** e havia **duplicatas**
("Take Ownership (KitLugia)" + "Take Ownership Super (KitLugia)").

**1. "Nada acontece" — o bootstrap engolia o clique.** `Program.Main` fazia:
1. `--unlock/--takeown` sem admin → relançava o exe elevado (`Verb=runas`) e **encerrava esta
   instância**;
2. a instância elevada, ao encontrar o Kit já aberto (mutex ocupado), rodava um worker
   **headless** e saía — **nenhuma janela**, só um toast que podia falhar em silêncio.

Com o Kit na bandeja (caso comum) o resultado era exatamente "cliquei e nada aconteceu".
Evidência no log, minutos antes do fix:
`[ELEV] Instância elevada + mutex ocupado → worker headless.` (x5 seguidas).

**Correção:** o bootstrap só entrega o comando — IPC para a instância existente ou UI própria.
A elevação passou a ser responsabilidade da **página** (`RunGuaranteedAsync` →
`ElevatedFileOpRunner`), que pede **um** UAC e mostra o progresso quando (e só quando) o alvo exigir.
O worker `--worker` continua existindo — é o processo elevado chamado pelo runner, agora com
progresso visível na página.

**2. Bandeja escondida.** `MainWindow.ShowAndActivateFromTray()` (novo) restaura a janela de
qualquer estado: `CloseToTray` usa `Hide()`, e `Activate()`/`Focus()` numa janela escondida não
mostra nada — era a peça que faltava para o clique "abrir o Kit".

**3. Duplicatas.** Duas chaves registravam a MESMA ação, com o comando idêntico:

| Chave | Label | Origem |
|-------|-------|--------|
| `\shell\kittakeown` | 👑 Take Ownership (KitLugia) | `SystemTweaks.AddTakeOwnershipKit()` (toggle da página) |
| `\shell\kit_takeownership` | 👑 Take Ownership Super (KitLugia) | `ContextMenuQuickAdd.AddTakeOwnershipSuper()` (catálogo super) |

A entrada canônica é `kittakeown`. O item do catálogo super agora delega para
`AddTakeOwnershipKit()/RemoveTakeOwnershipKit()`, e `SystemTweaks.ConsolidateTakeOwnershipMenu()`
(rodado no startup) remove o legado e recria a canônica — migração idempotente para quem já tem
as duas chaves no registro.

**Validação no host (29/09/2026):**

| Cenário | Resultado |
|---------|-----------|
| Startup com as 2 chaves presentes | `kit_takeownership` removida; resta só `kittakeown` (`*` e `Directory`) — log: `[TAKEOWN KIT] Duplicatas consolidadas em uma única entrada: kittakeown` |
| `--takeown <arquivo>` com o Kit na bandeja | `[IPC] Comando recebido: TAKEOWN` + `Comando do menu de contexto entregue à instância existente` e **janela visível** (`MainWindowHandle` válido, título "Kit Lugia - Gold Edition") |
| `--unlock <arquivo>` com o Kit na bandeja | `[IPC] Comando recebido: UNLOCK` (mesmo caminho) |
| `--worker --action takeown` | `R|OK|1`, `OWNED|1`, privilégios `SeDebug/SeTakeOwnership/SeBackup/SeRestore/SeLoadDriver` |

## Auditoria do Force Stop — cobertura e correções (29/09/2026)

Pedido: "o force stop tem que ser infalível e suportar todos os arquivos". Auditoria completa do
pipeline (`FileOpGuarantee` → `ForceStopUnlockService` → `DriverUnlockService`) e correções:

### BUG DE PERDA DE DADOS (corrigido) — "Liberar" agendava a EXCLUSÃO

Na ação `ForceStop`, o ramo de "beco sem saída" chamava
`DriverUnlockService.ScheduleDeleteOnReboot` — que registra o caminho em
`PendingFileRenameOperations` com destino vazio (`MoveFileEx(path, NULL, DELAY_UNTIL_REBOOT)`),
ou seja, **apaga no próximo boot**. Pior: o teste de lock só olhava arquivo
(`!isDir && !IsLockedNow`), então **toda pasta** caía nesse ramo — clicar em *Liberar
Selecionados* numa pasta agendava a exclusão da pasta inteira.

Agora:
- novo `HasActiveLock(target, isDir)`: arquivo = teste exclusivo (share NONE); pasta = amostra de
  até 512 arquivos internos (um handle aberto em qualquer um conta como travado);
- na ação `ForceStop` **não se agenda nada**: se ainda houver bloqueio, o resultado é honesto
  (`StillBlocked`, sem `ScheduledForReboot`) e orienta usar **Tentar Deletar** — que mantém o
  agendamento no boot como último recurso.

### Raiz de unidade

`ForceStop`/`Delete` em `C:\`/`E:\` agora são recusados na hora ("selecione um arquivo ou
pasta"). Antes o motor varria o disco inteiro — registrando cada arquivo no log — e ainda podia
agendar a exclusão da raiz. Medido agora: **0,17 s**.

### Fechamento de handle sem dependência externa

A fase 4 usava só `handle64.exe`; sem essa ferramenta instalada, **cada** handle achado pelo
scan nativo virava erro falso. Agora tenta primeiro `CloseNativeHandle`
(`DuplicateHandle` + `DUPLICATE_CLOSE_SOURCE`, P/Invoke) e só depois o handle64.

### Erro fantasma do Restart Manager

O RM registra o placeholder `HandleId = "RM"`; a fase 4 tentava "fechar RM" com o handle64 e
falhava, poluindo o painel com `Falha ao liberar handle RM de ...` mesmo com a operação
bem-sucedida. `IsRealHandleId()` agora só aceita handle hexadecimal (scan nativo `0x1234` /
handle64) — e o PID do RM segue para a fase 7 (se o RM não liberou, o processo é finalizado).

### Cobertura por tipo de alvo (motor confirmado no código)

| Alvo | Como é destravado |
|---|---|
| Arquivo comum | teste exclusivo + scan de bloqueadores |
| Arquivo com ACL negando | privilégios + ownership antes de continuar |
| Arquivo travado por app | Restart Manager → handles nativos (`DuplicateHandle`) → handle64 → kill |
| `.dll`/`.exe` carregados | mesmos handles (File/Section) + kill do processo |
| `.sys` carregado | driver scan (serviço por nome/registry) → SCM stop+delete → `NtUnloadDriver` → `sc stop/delete` |
| Serviço registrado (WinDivert/goodbyedpi) | SCM stop + delete — foi o que derrubou o goodbyedpi |
| Pasta | handles dos arquivos internos (até 512 no teste final) |
| Processo de sistema (svchost/lsass) | nunca é finalizado; RM tenta fechar, senão reporta travado |
| Raiz de unidade | recusado (não faz sentido "liberar uma unidade") |

**Limitações honestas (por design):** handle mantido por um driver de kernel sem serviço
correspondente não tem como ser fechado → a resposta é "ainda travado" (sem mentira e sem
efeito colateral); arquivos em UNC dependem das ACLs do servidor; processos protegidos (PPL)
não são finalizados.

### Validado no host (exe Debug, worker elevado)

| Cenário | Antes | Agora |
|---|---|---|
| `force-stop` em pasta LIVRE | agendava a exclusão da pasta no boot | `OK=1`, "Pasta liberada: nenhum dos 2 arquivo(s) verificados está travado", **nada em PendingFileRenameOperations**, pasta intacta |
| `force-stop` em pasta com arquivo travado | idem (agendava) | `OK=1`, 1 handle fechado + 1 processo finalizado, **sem erros fantasma** |
| `force-stop` em arquivo livre | agendava a exclusão do arquivo | `OK=1`, "Caminho liberado: nenhum handle ativo restante" |
| `force-stop` em `C:\` | varria e logava o disco inteiro | recusa em 0,17 s |
| `delete` em arquivo travado | inalterado | `OK=1`, `DELETED=1` — removido na hora, sem agendar |
| `delete` em pasta | inalterado | `OK=1`, pasta removida |
| `takeown` em arquivo | inalterado | `OK=1`, `OWNED=1` |

Utilitário de teste: `tests/hold_file_lock.ps1` (trava um arquivo com `FileShare.None` por N
segundos, para reproduzir um alvo bloqueado de verdade).

## Cenários Testados

| # | Cenário | Resultado |
|---|---------|-----------|
| 1 | Serviço WinDivert registrado + RUNNING | ✅ SCM stop+delete + 22/22 arquivos deletados |
| 2 | Serviço marcado para delete (STOP_PENDING) | ✅ Registry scan + kill + deleção |
| 3 | Sem serviço registrado | ✅ Native handles + RM + kill + deleção |
| 4 | IPC via menu de contexto | ✅ Path recebido via Named Pipe |
| 5 | Toggle on/off/on | ✅ Registro corretamente atualizado |
| 6 | DLL lock (processo separado) | ✅ Restart Manager detecta + kill + delete |

## Arquivos Modificados

| Arquivo | Mudanças |
|---------|----------|
| `KitLugia.Core/DriverUnlockService.cs` | Matching fuzzy, fallbacks sem filtro, logging |
| `KitLugia.Core/ForceStopUnlockService.cs` | Native handles, robust deletion, unlock 7 fases |
| `KitLugia.GUI/Pages/WindowsSettings/ForceStopUnlockPage.xaml.cs` | Logging, folder listing |
| `KitLugia.GUI/External/ForceStopUnlock/AddContextMenu.reg` | Updated command |
| `Publish/External/ForceStopUnlock/AddContextMenu.reg` | Updated command |

## APIs Nativas Utilizadas

### P/Invoke declarations
- `NtQuerySystemInformation` — Enumeração de handles do sistema
- `NtQueryObject` — Query de nome/tipo de handle
- `DuplicateHandle` (com `DUPLICATE_CLOSE_SOURCE`) — Fechamento forçado de handles
- `NtUnloadDriver` — Descarregamento direto de driver
- `NtSetInformationFile` — Force delete via FileDispositionInformation
- `MoveFileEx` (MOVEFILE_DELAY_UNTIL_REBOOT) — Agendamento de delete no reboot

### SCM API
- `OpenSCManager` / `OpenService` / `ControlService` / `DeleteService`
- `EnumServicesStatusEx` — Enumeração de serviços

### Restart Manager
- `RmStartSession` / `RmRegisterResources` / `RmGetList` / `RmShutdown`

## Segurança

- **CriticalDrivers**: Lista de drivers críticos do sistema que NUNCA são descarregados (ntoskrnl, hal, tcpip, ndis, etc.)
- **SystemProcessNames**: Processos de sistema que NUNCA são finalizados (csrss, lsass, svchost, etc.)
- **Admin check**: Cada operação loga se está rodando como administrador
- **Logging**: Todas as operações são logadas detalhadamente em `%LocalAppData%\KitLugia\Logs\KitLugia.log`
