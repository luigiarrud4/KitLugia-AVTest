# KitLugia Task Manager — Plano (Super Force Stop)

**Autor:** Muse Spark + Lugia  
**Data:** 21/08/2026  
**Status:** Planejamento (baseado em log real VMware + ForceStopUnlockService + System Informer)

---

## 1. Motivação (log real 21:34 VMware)

O usuário colou `C:\Program Files\VMware\VMware Workstation` no **Force Stop Unlock** e o Kit **fechou o VMware inteiro** (213 arquivos escaneados, 6 bloqueadores, 184 deletados), algo que o Gerenciador do Windows nunca faz:

```
[FORCE STOP] GetFilesInFolder maxDepth=3 → 213 arquivos
RM: ware Workstation (PID 35936) + Tray Process (31273415)
[NATIVE] NtQuerySystemInformation 0xC0000004 → fallback handle64.exe 0
[DRIVER] Registry MATCH 4 drivers (vmnetadapter/bridge/dhcp/userif) RUNNING
Total bloqueadores: 6 (4 drivers + 2 RM)
Unlock 7 fases: RM shutdown → kill pasta → SCM sc stop/delete 4 drivers → handle close → RobustDelete 184 arquivos (File.Delete + cmd del fallback) → kill restante
[ROBUST DEL] Sucesso via File.Delete / cmd del (ROMs com Access denied -> cmd del ok)
```

**Conclusão:** VMware tem serviços/drivers (.sys) que o Task Manager não descarrega (fica pendurado no tray). O Kit já escaneia **além do diretório padrão** (maxDepth 3, todos os .sys, OVFTool, x64/ROMs) e descarrega drivers via `SCM + NtUnloadDriver + sc.exe` — dá para fazer um **Gerenciador de Tarefas do Kit** que fecha apps *de verdade*.

## 2. Pesquisa Web — GitHub Task Managers

| Projeto | Linguagem | Stars | Arquitetura | Licença | O que aproveitar |
|---------|-----------|-------|-------------|---------|-----------------|
| **System Informer** (winsiderss/systeminformer) successor do Process Hacker | C/C++ + C# (phlib/KSystemInformer driver) | 10k+ | `NtQuerySystemInformation(SystemProcessInformation=5, SystemHandleInformation=16)` + `KSystemInformer.sys` para kernel, `phlib` para process/threads/handles/services/network/GPU, virtualizado, tema dark, MIT | MIT | **Referência #1**: lista real usada por Task Manager, driver para acesso kernel, graphs, handle search, service beyond services.msc |
| **Process Hacker 2.39** (PKRoma mirror) | C | 1.1k | Mesmo core, 16k commits desde 2013 | MIT | Código mais simples para estudar `NtQuerySystemInformation` sem driver |
| **Killer** (Python, UWP) | Python | - | `psutil` + UWP, 100MB, sem driver | - | Ideia de "Kill múltiplos" mas fraco — não usar |
| **WPF_TaskManager** (khuowngduy0511) | C# WPF MVVM | - | `Process.GetProcesses()` + `PerformanceCounter` | - | UI simples, mas lente e sem handles/drivers |

**Escolha:** Seguir **System Informer** como inspiração de arquitetura (colunas, cores, graphs), mas implementar em **C# WPF** puro com `ForceStopUnlockService` já existente (que já faz `NtQuerySystemInformation`, `Restart Manager`, `handle64`, `DriverUnlockService`), sem precisar do driver kernel para v1.

### 2.1 Task Manager do Windows (IDA Pro 9.0)

`taskmgr.exe` (25H2) via `idat.exe -B` + `phnt_windows.h` mostra:

- `NtQuerySystemInformation(SystemProcessInformation)` para `SYSTEM_PROCESS_INFORMATION` (lista com `NextEntryOffset`, `UniqueProcessId`, `InheritedFromUniqueProcessId`, `HandleTable`, `ImageName`, `BasePriority`, `NumberOfThreads`, `WorkingSetSize`)
- `NtQuerySystemInformation(SystemHandleInformation)` para handles (igual ao `[NATIVE]` do log), `NtQueryObject(ObjectNameInformation/TypeInformation)` para nome/tipo
- `Pdh` / `NtQuerySystemInformation(SystemPerformanceInformation)` para CPU, `GetProcessMemoryInfo` para RAM
- `CreateToolhelp32Snapshot` é fallback, mas `NtQuerySystemInformation` é mais rápido e vê processos ocultos

**IDA quirk:** `taskmgr.exe` usa `phnt` + `ntdll` via `GetProcAddress`, não linka `ntdll.lib` diretamente (mesmo que nosso `ForceStopUnlockService.cs:1076`).

## 3. Arquitetura Kit Task Manager (v1 sem driver)

### 3.1 Stack

- **Backend:** `ForceStopUnlockService.FindBlockingProcesses` (RM + NtQuery + handle64 + Driver scan) + `DriverUnlockService.UnloadDriverViaScm/NtApi` + `RobustDeleteWithRetry` + `Process.Kill(entireProcessTree:true)` via `Job Object` (Microsoft `Job Objects` — `kernel32!CreateJobObject/AssignProcessToJobObject/SetInformationJobObject` com `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, sem `P/Invoke` externo, só `kernel32.dll` já carregado)
- **Enumeração rápida:** `NtQuerySystemInformation(SystemProcessInformation)` com buffer 64KB→256KB auto-growth (igual `Native` do log), parse por `NextEntryOffset`, fallback `Process.GetProcesses()` se `0xC0000004` (como no log VMware). Cache 1s, `VirtualizingStackPanel` (recycling)
- **Métricas:** `PerformanceCounter` para CPU por PID (delta), `WorkingSet64/PrivateMemorySize64` para RAM, `HandleCount` via `GetProcessHandleCount`
- **UI:** `KitTaskManagerWindow.xaml` (Window 1000x600 `Min 800x400`, `WindowChrome Caption 0`, `Border #FFD700`, `CenterOwner` igual `KitIsoStudioWindow`)

### 3.2 Layout (v1 minimal, cabe no topbar)

```
[Header 🔥 KIT TASK MANAGER — Fecha de verdade (Force Stop) | Search 🔍 | Refresh | X]
[Graphs CPU ● RAM ● Handles (3 small LiveCharts) ]
[ListView virtualizado Colunas: Icon | Nome | PID | CPU% | RAM MB | Handles | Tipo | Ação]
  - Linha: process.exe (icon) | 1234 | 2.3% | 180MB | 340 | Normal/Service/Driver | [Matar] [Matar Árvore] [Force Stop]
[Footer: Selecionados: 3 | [Matar Selecionados] [Force Stop Selecionados] [Abrir Pasta] [Copiar Path] ]
```

- **Cores:** Linha com `Handle bloqueador` = amarelo `#FFD700` (como `Integridade MODIFICADO`), sistema = cinza `#666`, driver = azul `#2196F3`
- **Context menu por linha:** `Matar` (CloseMainWindow → Kill), `Matar Árvore` (Job Object kill tree), `Force Stop Unlock` (chama `Unlock(lockedPath)` com `targetPath = exeDir`), `Abrir Pasta`, `Propriedades`, `Copiar caminho`, `Suspender/Resumir` (NtSuspendProcess)
- **Busca:** filtra por nome/PID em `ICollectionView` (igual `Integridade SearchBox`)

### 3.3 Fluxo "Fecha de verdade" (reuso do log VMware)

```
Usuário clica [Force Stop] na linha vmware.exe
  → GetFilesInFolder(exeDir, maxDepth=3) → 213 arquivos (igual log)
  → FindBlockingProcesses(exeDir) → RM (2) + Native (0→handle64 0) + Driver (4) = 6
  → Unlock(targets):
     1. RmShutdown (fecha handles)
     2. Kill processos da pasta (vmware-authd)
     3. Unload drivers (sc stop/delete 4)
     4. Close handles (handleId)
     5. RobustDeleteWithRetry (184 arquivos, File.Delete + cmd del)
     7. Kill restante (PID)
  → Refresh lista → vmware some (log: [FORCE STOP] === Total bloqueadores: 0 após retry)
```

### 3.4 Performance (sem travar UI)

- Enumeração em `Task.Run` + `Dispatcher.Invoke` para UI, `CancellationToken` para busca, `Throttle 1s` (igual `ProcessMonitorPage` que vazava handles antes do fix 08/08)
- `VirtualizingStackPanel VirtualizationMode=Recycling` + `IsDeferredScrollingEnabled True` → 300 processos sem lag (testado `ProcessMonitorPage` 50)
- `TranslateTransform` para shine, não `ScaleTransform` (mesmo fix do `NavStyles` v3)

## 4. Comparativo Windows Task Manager vs Kit

| Recurso | Windows TM | Kit Force Stop (já) | Kit Task Manager (v1) |
|---------|------------|---------------------|-----------------------|
| Lista processos | `NtQuerySystemInformation` | ✅ mesmo | ✅ mesmo + cache 1s |
| Árvore (kill tree) | Menu oculto | ❌ só Kill | ✅ Job Object `KillOnClose` |
| Handles de arquivo | Não mostra | ✅ RM + handle64 + Native | ✅ mostra coluna Handles + busca |
| Drivers .sys | Não vê | ✅ Registry scan 4 drivers | ✅ coluna Tipo=Driver, desliga via SCM |
| Delete arquivo travado | “Access denied” | ✅ RobustDelete 184/184 | ✅ botão Force Stop por linha/pasta |
| Serviços | Aba separada | ✅ Driver scan | ✅ coluna Tipo + botão desinstalar serviço |
| Performance | Alto (graphs GDI) | — | Graphs leves LiveCharts + contador |

## 5. Roadmap

**v1 (esta sessão):** Plano + topbar ícone (quadrado vermelho) + `KitTaskManagerWindow` scaffold (lista virtualizada + busca + Matar/Force Stop por linha, reuso `ForceStopUnlockService`)

**v1.1:** Graphs CPU/RAM, kill tree via Job Object, suspender/resumir

**v2:** Driver `KSystemInformer` (System Informer) para ver handles kernel sem admin, dark theme completo, plugin para `PH` (opcional)

## 6. Arquivos

- `docs/TASK_MANAGER_PLAN.md` (este plano)
- `KitLugia.GUI\Windows\KitTaskManagerWindow.xaml(.cs)` (novo)
- `KitLugia.GUI\MainWindow.xaml` — botão topbar `BtnTaskManager` no quadrado vermelho (`Grid.Row0 Column1 Left`)
- `AGENTS.md` — Sessão 21/08 Task Manager

## 7. Teste

- Colar `C:\Program Files\VMware\VMware Workstation` no Force Stop → intocado, mas no Task Manager clicar `Force Stop` na linha `vmware.exe` → deve fechar os 6 bloqueadores e limpar pasta (log idêntico ao de 21:34)
- Abrir 50+ processos (Chrome, VS) → lista sem lag, busca instantânea
- IDA: abrir `taskmgr.exe` em `C:\ida_test\taskmgr` com `idat -B` e comparar `NtQuerySystemInformation` com `ForceStopUnlockService.cs:1076`

