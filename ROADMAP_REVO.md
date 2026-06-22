# Roadmap: Replicar Revo Uninstaller

## Comparação Revo Pro × KitLugia

| Funcionalidade | Revo Pro | KitLugia | Status |
|---|---|---|---|
| **Deep Uninstall (pré→pós diff)** | ✅ | ✅ `DeepUninstaller.DeepUninstallProgram()` | ✅ Completo |
| **Registry scanner paralelo** (10 buckets) | ✅ | ✅ `ScanLeftoverRegistry()` | ✅ Completo |
| **3 modos de scan** (Safe/Moderate/Advanced) | ✅ | ✅ `ScannerMode` enum existe | ⚠️ Não está no UI |
| **Classificação de segurança** (Safe/Moderate/Uncertain) | ✅ | ✅ `CleanupSafety`, `ScanEntry.Safety` | ⚠️ Não reflete no UI |
| **Safety icons (🟢🟡🔴)** | ✅ | ✅ AppCleanupItem tem SafetyIcon | ⚠️ Parcial no UI |
| **Hunter Mode** | ✅ | ✅ `HunterWindow` | ⚠️ Básico, pode melhorar |
| **Restore point** | ✅ | ✅ `TryCreateRestorePoint()` | ✅ Completo |
| **Backup .reg + lixeira** | ✅ | ✅ `BackupRegistryKey`, recycle bin | ✅ Completo |
| **Deletion log** | ✅ | ✅ `DeletionLogFile` em `UninstallResult` | ✅ Completo |
| **Forced Uninstall** (programa quebrado) | ✅ | ⚠️ `ForceDeleteProgram()` existe | ❌ Não tem UI |
| **Evidence Remover** (MRU, RecentDocs, Prefetch, JumpLists, UserAssist, TypedURLs) | ✅ | ❌ Só Prefetch básico no CleanupManager | ❌ **FALTA** |
| **Portable Apps Scanner** | ✅ | ❌ | ❌ **FALTA** |
| **Install Monitor** (tempo real) | ✅ | ❌ | ❌ **FALTA** |
| **Autorun Manager completo** (Winlogon, AppInit, BHO, BootExecute, etc.) | ✅ | ⚠️ Só Run/RunOnce + Startup Folder | ❌ **FALTA** |
| **Shell Extensions Manager** | ✅ | ❌ | ❌ **FALTA** |
| **File Type / Association Manager** | ✅ | ❌ | ❌ **FALTA** |
| **Batch uninstall paralelo** | ✅ | ✅ UWP (3x) + Programs (2x) | ✅ Completo |
| **Select All / Deselect All** | ✅ | ✅ Adicionado | ✅ Completo |
| **Junk Cleaner (temp, cache, logs)** | ✅ | ✅ `CleanupManager`, `Toolbox` | ✅ Completo |
| **Browser cache/history cleaner** | ✅ | ⚠️ Só cache | ❌ Falta history/cookies |
| **Startup optimizer (Turbo Boot)** | ❌ | ✅ `StartupManager.TurboBoot` | ✅ Diferencial |
| **Privacy settings (O&O ShutUp)** | ❌ | ✅ `PrivacyPage` + `OOShutUpManager` | ✅ Diferencial |
| **Registry cleaner (órfão)** | ✅ | ✅ `RegistryCleaner` | ✅ Completo |
| **Services optimizer** | ✅ | ✅ `ServicesPage` + presets | ✅ Completo |

## ✅ Já implementado e funcionando

### Deep Uninstall (Revo-style)
- `DeepUninstaller.cs`: 3756 linhas
- `DeepUninstallProgram()`: pré-scan → uninstall → pós-scan → diff (confirmed vs heuristic)
- `ForceDeleteProgram()`: forced uninstall sem uninstaller (mata processos, deleta pastas, limpa registro)
- `ScanLeftovers(displayName, publisher, mode)`: scan de arquivos + registro
- `PerformCleanup()`: deleta com backup, recycle bin, safety checks
- `CleanupSafety` enum: `Safe`, `Moderate`, `Uncertain`
- `ScannerMode` enum: `Safe`, `Moderate`, `Advanced`
- `UninstallResult` com: `LeftoverFiles`, `LeftoverRegistry`, `HeuristicFiles`, `HeuristicRegistry`, `BaselineFileCount`, `BaselineRegistryCount`, `BackupFiles`, `DeletionLogFile`
- 10 buckets de scan de registro em paralelo
- Proteção de sistema: `ProhibitedLocations`, `IsSystemFolder()`, `IsTooBroadForDeletion()`
- `KillProcessesWithTree()`: mata processos do app antes de deletar
- `BackupRegistryKey()`: backup .reg antes de deletar chave

### UI de Programas (AppsPage)
- 3 abas: Bloatware (UWP), Programas (Registry), Resíduos (junk tracker)
- Remoção individual com Revo diff (single UWP)
- Batch UWP com Revo diff (pré→pós) + semaphore 3x
- Batch Programs com semaphore 2x
- Hunter Mode
- Select All / Deselect All (adicionado)
- Busca por nome
- Restore point

### UI de Resíduos (Junk Tracker)
- `LeftoverJunkManager` com persistência JSON
- Cards expansíveis com arquivos/registro por app
- Safety icons, heuristic labels
- Cleanup individual por app
- Dedup + max 100 entries

### Outros
- `StartupManager`: Run/RunOnce, Startup Folder, Task Scheduler, Turbo Boot
- `RegistryCleaner`: scan de órfãos (COM, SharedDLLs, AppPaths, etc.)
- `CleanupManager`: temp, cache Windows, prefetch, thumbnails, DNS, logs
- `BrowserCacheManager`: cache de Chrome, Edge, Firefox, Opera, Brave, Vivaldi
- `PrivacyPage`: 130+ settings estilo O&O ShutUp10++
- `ServicesPage`: 4 abas, presets Safe/Gamer
- `Guardian`: detecta 2000+ tweaks nocivos

## ❌ O que falta vs Revo Pro

### 1. Forced Uninstall UI (MÉDIA PRIORIDADE)
- `ForceDeleteProgram()` já existe no Core mas não tem botão no UI
- Adicionar botão "Forçar Remoção" na ProgramsPage
- Criar diálogo: selecionar pasta + confirmar
- Adicionar na aba de Programas do AppsPage também

### 2. Evidence Remover (ALTA PRIORIDADE)
Criar classe `EvidenceCleaner.cs` em KitLugia.Core/:

| Categoria | O que limpar | Implementação |
|---|---|---|
| **RecentDocs** | HKCU\...\RecentDocs | `Registry.DeleteSubKeyTree()` |
| **RunMRU** | HKCU\...\RunMRU | Limpar lista de comandos executados |
| **TypedURLs** | HKCU\...\TypedURLs (IE/Edge) | Limpar URLs digitadas |
| **UserAssist** | HKCU\...\UserAssist | Limpar rastreamento de execução |
| **BagMRU** | HKCU\...\BagMRU + Bags | Limpar histórico de pastas |
| **Jump Lists** | %APPDATA%\Microsoft\Windows\Recent\AutomaticDestinations | Deletar arquivos `.automaticDestinations-ms` |
| **Prefetch** | C:\Windows\Prefetch | Já existe no CleanupManager |
| **Windows Timeline** | ActivitiesCache.db | Deletar db de atividades |
| **Clipboard History** | %LOCALAPPDATA%\Microsoft\Windows\Clipboard | Limpar histórico |
| **Office MRU** | HKCU\...\Office\*\MRU | Limpar documentos recentes do Office |
| **Visual Studio MRU** | HKCU\...\VisualStudio\*\MRU | Limpar projetos recentes |
| **Browser History** | Chrome, Edge, Firefox históricos | Estender BrowserCacheManager |
| **Browser Cookies** | Cookies dos browsers | Estender BrowserCacheManager |

Recursos: opera uma vez sob demanda (não em bg). Zero impacto em bg.

### 3. Portable Apps Scanner (MÉDIA PRIORIDADE)
Criar classe `PortableAppScanner.cs`:

- Escanear %USERPROFILE%\Downloads, Desktop, D:\ (drives externos)
- Detectar executáveis que não passaram por install (sem registro)
- Listar com opção de "limpar" (deletar pasta)
- Heurística: .exe com .dll, .ini, _data folder = provavelmente portable
- Falso positivo: não listar programas do sistema

Recursos: opera sob demanda. Scan é rápido (só metadados).

### 4. Install Monitor (BAIXA PRIORIDADE - recursos controlados)
Criar classe `InstallMonitor.cs`:

- **Não** usar ETW (consome >10% CPU)
- **Não** usar WMI events (consome >300MB RAM)
- Usar `FileSystemWatcher` em:
  - %ProgramFiles%, %ProgramFiles(x86)%
  - %APPDATA%, %LOCALAPPDATA%
  - %ProgramData%
- Usar snapshot de registro (HKLM\Software, HKCU\Software) via diff periódico
- Periodo: snapshot a cada 5 minutos (não em tempo real)
- Quando `setup.exe` / `installer.exe` / `msiexec` é detectado rodando:
  - Salvar snapshot de arquivos (lista de paths em %ProgramFiles%)
  - Salvar snapshot de registro (subchaves de HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall)
  - Aguardar processo terminar
  - Comparar: o que apareceu = instalado
- Armazenar snapshots em JSON (~50KB cada)
- UI: "Monitor de Instalação" com lista de instalações detectadas

**Limite de recursos em bg**:
- FileSystemWatcher: ~0% CPU, ~5MB RAM
- Snapshot de registro periódico: ~2% CPU por 2 segundos a cada 5 min
- Sem polling, sem loops infinitos
- Thread de bg dorme 99% do tempo

### 5. Autorun Manager Expandido (BAIXA PRIORIDADE)
Estender `StartupManager.cs` para incluir:

| Localização | Status |
|---|---|
| HKLM\...\Run / RunOnce | ✅ Já tem |
| HKCU\...\Run / RunOnce | ✅ Já tem |
| Startup Folder (All Users + Current) | ✅ Já tem |
| Task Scheduler | ✅ Já tem |
| **Winlogon\Shell** | ❌ Falta |
| **Winlogon\Userinit** | ❌ Falta |
| **AppInit_DLLs** | ❌ Falta |
| **KnownDLLs** | ❌ Falta |
| **BootExecute** | ❌ Falta |
| **ShellServiceObjectDelayLoad** | ❌ Falta |
| **Browser Helper Objects (BHO)** | ❌ Falta |
| **ShellExecuteHooks** | ❌ Falta |
| **Context Menu Handlers** | ❌ Falta |

### 6. File Type / Association Manager (BAIXA PRIORIDADE)
- Listar extensões registradas
- Mostrar programa associado
- Permitir trocar/remover associação

### 7. Shell Extensions Manager (BAIXA PRIORIDADE)
- Listar context menu handlers
- Permitir desabilitar/habilitar

## Fases de Implementação

### FASE 1 - Forced Uninstall UI (1 dia)
- [x] `ForceDeleteProgram()` já existe
- [ ] Adicionar botão "Forçar Remoção" na ProgramsPage
- [ ] Diálogo de confirmação com seleção de pasta
- [ ] Relatório do que foi deletado

### FASE 2 - Evidence Remover (2 dias)
- [ ] Criar `EvidenceCleaner.cs` com todos os métodos
- [ ] Adicionar seção "Evidências" na CleanupPage
- [ ] Categorias com checkboxes
- [ ] Execução com progresso

### FASE 3 - Portable Apps Scanner (1 dia)
- [ ] Criar `PortableAppScanner.cs`
- [ ] Adicionar aba na AppsPage ou página separada
- [ ] Lista com nome + caminho + tamanho
- [ ] Opção "Mover para Lixeira"

### FASE 4 - Safety UI + Scanner Mode Selector (1 dia)
- [ ] Adicionar ComboBox de scanner mode (Safe/Moderate/Advanced) no AppsPage
- [ ] Fazer UI refletir SafetyLevel (bold/not bold, cores)
- [ ] "Selecionar Tudo" pular Uncertain

### FASE 5 - Install Monitor (3 dias)
- [ ] Criar `InstallMonitor.cs` com FileSystemWatcher + snapshot diff
- [ ] BG thread com sleep 5 min entre snapshots
- [ ] Detecção de processos instaladores
- [ ] UI: "Monitor" tab com histórico
- [ ] ⚠️ Respeitar limite: ≤10% CPU ≤300MB RAM

### FASE 6 - Autorun + File Types + Shell Extensions (2 dias)
- [ ] Estender StartupManager
- [ ] FileTypeManager básico
- [ ] ShellExtensionsManager básico
