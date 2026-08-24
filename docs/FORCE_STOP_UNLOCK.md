# Force Stop Unlock — Documentação Completa

## Visão Geral

O Force Stop Unlock é uma ferramenta integrada ao KitLugia que libera arquivos bloqueados por processos, drivers, ou handles no Windows. Suporta **todos os tipos de arquivo**: `.sys`, `.dll`, `.exe`, e qualquer outro.

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

**Fluxo IPC:**
```
Explorer → Clique direito → "Force Stop Unlock"
  → KitLugia.GUI.exe --unlock "C:\path\to\file"
  → Se Kit já rodando: IPC via Named Pipe
  → ForceStopUnlockPage abre com path preenchido
  → Auto-análise + unlock
```

**Registro:** `HKCU\Software\Classes\{*,Directory,Drive}\shell\forcestopunlock\command`
- Command: `"KitLugia.GUI.exe" --unlock "%1"`

**Toggle:** On/Off na ForceStopUnlockPage atualiza o registro
- `SystemTweaks.AddForceStopUnlock()` — registra com path do exe atual
- `SystemTweaks.RemoveForceStopUnlock()` — remove todas as variantes

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
