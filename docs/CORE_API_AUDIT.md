# Auditoria do KitLugia.Core — APIs atuais x legadas (29/09/2026)

Escopo: 152 arquivos, ~87 mil linhas. Pergunta: "está tudo em ordem? não tem métodos
mais recentes e atualizados?". Ferramentas: `tests/core_audit.py` (amplo),
`tests/core_deep_audit2.py` (confiável, determinístico — 2 runs idênticos),
`tests/core_modern_api.py`, `tests/core_redundant_api.py`,
`tests/core_privilege_layout.py`.

## Veredito

**O Core está em ordem estrutural**: 0 métodos `[Obsolete]` em uso, 0 `Thread.Sleep`
em método `async`, 0 `new Random()` por chamada (todos migrados p/ `Random.Shared`),
0 `new HttpClient()` (12 sites migrados p/ handler compartilhado), layout nativo
`TOKEN_PRIVILEGES`/`LUID` correto nos 5 arquivos (5 OK / 0 problemas). As APIs
"antigas" que restam são majoritariamente **estilo**, não bug — exceto os pontos
corrigidos abaixo.

## Corrigido nesta sessão (build 0 erros)

| # | Arquivo | Antes (legado) | Depois (moderno) | Por quê |
|---|---------|----------------|------------------|---------|
| 1 | `DashboardManager.cs` (reescrito) | Snapshot 100% WMI (`Win32_Processor/OperatingSystem/VideoController/DiskDrive`) | **Nativo primeiro**: registry CPU/GPU/SO + `GetSystemTimes` (carga CPU, 2 amostras) + `GlobalMemoryStatusEx` (RAM) + `PartitionManager.GetAllDisks` (IOCTL); WMI e fallback registry mantidos | Dashboard abria em 100–500 ms dependendo do serviço Winmgmt; agora é milissegundos e funciona até com WMI quebrado |
| 2 | `GitHubUpdater.cs` (2), `NetworkExposureManager.cs` (5), `KitTunnelManager.cs`, `PlayitTunnelAdapter.cs`, `UniversalTunnelAdapter.cs`, `SmartVersionDetector.cs`, `WinpeBuilder.cs` | `using var c = new HttpClient()` em cada chamada | `KitHttp.CreateClient(timeout)` / `KitHttp.Shared` (novo `KitHttp.cs`: `SocketsHttpHandler` com `PooledConnectionLifetime=2 min`) | `new HttpClient` por chamada esvazia o pool de sockets (TIME_WAIT) e pode dispor o handler no meio de um download |
| 3 | `SystemUtils.cs` `GetTotalSystemRamGB` | WMI `Win32_OperatingSystem` primeiro (100–300 ms) | `GlobalMemoryStatusEx` primeiro (1 syscall); WMI cai p/ fallback | 7 call sites, alguns em fluxo de UI |
| 4 | `WinbootManager.cs` | `ShrinkPartitionUsingWMI` + `ShrinkPartitionUsingRunOnceAdvanced` (601 linhas) com **0 chamadores** | Removidos (o fluxo real usa `ShrinkPartitionUsingStorageAPI` + WinPE/marcador) | API morta que confundia a auditoria |
| 5 | `DownloadBoostEngine.cs`, `LatencyAnalyzer.cs` | `Environment.OSVersion.Version.Build >= 26000/22000` | `OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26000/22000)` | Estilo .NET 5+ (no .NET 5+ `OSVersion` não mente mais, mas a API oficial de gating é `OperatingSystem.*`) |
| 6 | `LatencyAnalyzer.cs` (2), `HolePunchingManager.cs` (2), `KitTunnelManager.cs`, `TunnelManager.cs` | `new Random()` (mesma seed do relógio no mesmo tick = colisão real em transaction ID/porta/código de sala) | `Random.Shared` | Thread-safe, .NET 6+ |
| 7 | `DeepUninstaller.cs` `IsEmptyDirectory` | `Directory.GetFiles(dir).Length > 0` (enumera TUDO) | `Directory.EnumerateFiles(dir).Any()` (para na 1ª) | Perf em pastas grandes |
| 8 | `DnsBenchmark.cs` | `static readonly Random _queryIdRng = new()` (não thread-safe, usado em paralelo p/ Query ID) | `Random.Shared` | Correção de concorrência real |

## O que NÃO é bug (avaliado e mantido)

- `Encoding.ASCII` (28x): protocolos de rede/estruturas binárias (DNS, relay,
  SQLite header, BLAKE3 hex) e scripts .cmd/.ini para ferramentas que exigem ASCII
  (diskpart, WinPE). Trocar por UTF-8 seria **errado** aqui.
- `Environment.OSVersion` restante (3x): apenas em `sb.AppendLine` de relatórios
  (DiagnosticCenter, AudioGlitchMonitor, LatencyMonitor) — leitura de exibição.
- `DateTime.Now` (110x): quase tudo é nome de arquivo/log/timestamp de exibição;
  nenhum uso em medição de intervalo crítica encontrada.
- Partials do TaskManager (`AudioGlitchMonitor` ↔ `LatencyMonitor`) e modelos
  (`DomainUser` ↔ `Resolution`): parecença, não duplicação real.
- `NativeDiskIo`/`NativeMft`/`NativeUsn`/`NativeBlake3`/`NativeSha256`: camada de
  performance deliberada (IOCTL/MFT/hash nativo), não API legada.

## Backlog honesto (sem risco de regressão imediata, decidir antes)

1. **136 métodos públicos sem uso** (lista completa no output de
   `core_deep_audit2.py`). Destaques: `GPEditManager` inteiro, `BloatwareManager`
   (coexiste com `SystemTweaks.GetBloatwareAppsStatus`), `ServiceHelper.IsServiceRunning/
   TryStartService/TryStopService`, `DriverManager.CheckForOutdatedDrivers`,
   `RegistryCleaner.CleanIssues`, `SearchEngine.SearchTop`, Winboot `PerformDiagnostics`,
   `ToggleManager.SaveAllToggles/RestoreAllToggles`. Alguns são API "para uso futuro";
   remoção em lote exige confirmação sua (falsos positivos via method group já filtrados).
2. **15 awaits síncronos no Core** (`GetAwaiter().GetResult()`/`.Result`) —
   AdapterManager:366, DriverManager:397, IsoEditorManager:52-53, StoreEngine:528,
   SearchEngine:356-357, SmartVersionDetector:329, SystemUtils:176/228,
   UpdateControlManager:130, WinbootManager:1542/2812, WinpeBuilder:1710/1745.
   Risco de deadlock é baixo (a maioria roda em thread pool), mas o padrão ideal é
   async end-to-end.
3. **28x WMI `Win32_*`** restante (BackgroundProcessManager, DeepUninstaller:4255
   `Win32_Process`, SystemUtils.GetServiceStartMode, DiagnosticCenter...). Migrar
   um a um para a camada nativa/PDH só onde já existe equivalente (como feito no
   DashboardManager).
4. **16x `ServiceController`** coexistindo com `sc.exe`/ServiceHelper — consolidar
   no ServiceHelper (que também tem implementação própria).
5. **Empty catches sem log** (~60+, 13 só no ForceStopUnlockService) — hoje a maioria
   engole exceção silenciosamente; padrão do projeto é `Logger.LogWarning`.
6. **Duplicação de helpers de processo**: `RunExternalProcess/RunProcess/
   ExecuteShellCommand` 385x em 26 arquivos — consolidar no `ProcessRunner` (que já
   existe) é um refactor grande, decisão sua.
7. TODO único: `IntegrityCheckManager.cs:41` (consulta WMI SMART).

## Métricas antes → depois (nesta sessão)

- `new HttpClient()` no Core: 12 → **0**
- `new Random()` não compartilhado: 7 → **0**
- Métodos públicos mortos: 138 → 136 (–601 linhas de API morta)
- Snapshot do Dashboard: WMI-first → **nativo-first** (WMI só como fallback)
- Build: 0 erros (Core + GUI), warnings nullable pré-existentes inalterados


## Sessao 01/10 — Remocao do lote seguro de codigo morto

Continuacao do backlog acima: remocao dos metodos publicos com **0 chamadores**
(confirmados por grep em todo o repo, inclusive `tests/`, alem do auditor).

**Arquivos inteiramente mortos deletados (3):**
- `GPEditManager.cs` (4 metodos) — thin wrappers de `Toolbox`
- `BloatwareManager.cs` (3) — thin wrappers de `SystemTweaks`
- `NativeBlake3.cs` (2 + P/Invoke + ctor) — classe so referenciada por si mesma

**Metodos removidos por arquivo (12 arquivos):**
- `ServiceHelper`: IsServiceRunning, TryStartService, TryStopService (3)
- `SystemInfo`: IsWindows11OrLater, IsWindows10OrLater, IsWindows81OrLater,
  IsWindowsServer, GetProcessorCount (5 + SYSTEM_INFO/GetNativeSystemInfo)
- `Logger`: LogRegistry, ToggleOutputLimit, ToggleVerboseCheck, GetLogPath (4)
- `ToggleManager`: SaveAllToggles, RestoreAllToggles, ClearAllSnapshots (3)
- `SystemUtils`: GetRestorePoints, OpenSystemRestoreWizard, RunPreflightCheck,
  GetRegistryValue (4)
- `TaskManager/NativeMetricsHelper`: GetActiveUserNames (1)
- 1 em cada: RegistryCleaner.CleanIssues, DriverManager.CheckForOutdatedDrivers,
  SearchEngine.SearchTop, ContinuityEngine.GetFolderSizeFast, PathRepair.IsPathHealthy,
  TaskManager/SafeProcessHelper.GetProcessPathFast, TaskManager/StorageDiagnostics.ResetCounters

**Resultado:** 16 arquivos alterados, **718 linhas removidas**.
Metodos publicos mortos: 136 -> **103**. Build: Core 0 erros; GUI `/t:Compile`
0 erros (o build completo so falha no MSB3021 porque o `KitLugia.GUI.exe` estava
aberto). Encoding preservado (CRLF em todos; BOM onde ja havia).

**Ferramenta:** `tests/remove_dead_methods.py <arquivo> <assinatura>` — remove um
metodo por assinatura preservando UTF-8 BOM e CRLF (nao usar `write_file` nesses
arquivos, que grava LF sem BOM).

### Backlog restante (103 metodos mortos), por arquivo

- SystemTweaks.cs: 43
- WindowsUpdateManager.cs / WinbootManager.cs: 7 cada
- StartupManager.cs / Guardian.cs: 6 cada
- WinpeBuilder.cs / KitStore/StoreEngine.cs / DeepUninstaller.cs: 3 cada
- SmartVersionDetector / PartitionManager / LocalInstallManager / DiagnosticsManager /
  BootOptimizerManager / AdapterManager: 2 cada
- 1 em cada: SafeProcessHelper, SystemUtils, NetworkTrafficMonitor, KnownStartupArgs,
  KitStore/StoreModels, IntegrityCheckManager, ForceStopUnlockService,
  EnablementPackageManager, DriverUnlockService, DriverManager, BrowserExtensionManager,
  BrowserCacheManager, AdvancedTweaksManager

Os grandes (SystemTweaks, WinbootManager, Guardian, StartupManager) sao mais
delicados (metodos centrais, possivel uso por reflection/nameof) — remocao metodo a
metodo com build + auditoria a cada lote.
## Sessao 01/10 (cont.) - backlog de codigo morto ZERADO (103 -> 0)

Segundo lote: removidos os 103 metodos publicos mortos restantes + 3 que ficaram
orfaos em cascata. Auditoria `tests/core_deep_audit2.py`: **0 metodos publicos do
Core sem uso** (era 136 no inicio do dia).

Arquivos tocados: AdapterManager, AdvancedTweaksManager, BootOptimizerManager,
BrowserCacheManager, BrowserExtensionManager, DeepUninstaller, DiagnosticsManager,
DriverManager, DriverUnlockService, EnablementPackageManager, ForceStopUnlockService,
Guardian, IntegrityCheckManager, KitStore/StoreEngine (+ campos `_cachedUpgrades`/
`_cachedAppx` e classe `StuckInfo` orfa), KitStore/StoreModels, KnownStartupArgs,
LocalInstallManager, NetworkTrafficMonitor, PartitionManager, SmartVersionDetector,
StartupManager, SystemTweaks (43), SystemUtils, TaskManager/SafeProcessHelper,
WinbootManager, WindowsUpdateManager, WinpeBuilder.

**Licao da ferramenta:** `tests/remove_dead_methods.py` fecha o metodo no primeiro
`}` de 8 espacos. Em metodos expression-bodied multi-linha a assinatura nao tem
`{`/`}` proprio, entao a ferramenta remove ate o `}` do proximo metodo (aconteceu
com `SetDeliveryOptimizationMode` engolindo `RestrictDeliveryOptimization`, que e
usado pela TweaksPage). Antes de remover: se a assinatura termina em `;` ou tem
`=>` na linha seguinte, remover apenas a(s) linha(s) por patch Python. Sempre
buildar **Core + GUI** depois.

**Verificacao:** Core 0 erros; GUI compila (para so no MSB3021/3027 se o .exe
estiver aberto; o `/t:Compile` puro acusa CS0103 em massa por nao rodar o
MarkupCompilePass - artefato, nao erro real). Diff acumulado (2 sessoes, nao
commitado): 42 arquivos, +91/-2929.

Ficou de fora: `IntegrityCheckManager.cs` inteiro agora esta morto (so helper
privado `CheckDiskHealth` + TODO de WMI SMART) - remocao opcional futura.
## Sessao 01/10 (cont. 2) - 15 awaits sincronos do Core eliminados

Auditoria secao 4 (`GetAwaiter().GetResult()` / `.Result`): **0 no KitLugia.Core**
(eram 15). Nenhum ficou pendente no Core; os `.Result` restantes sao do GUI
(MainWindow, QuickInstallPage, StoreRemakePage) e sao backlog.

Detalhe por site:

| Site | Solucao |
|---|---|
| AdapterManager.RestartAdapter | metodo morto -> removido |
| SmartVersionDetector.GetVersionInfo | metodo morto -> removido |
| IsoEditorManager.RunProcessCapturedWithStdin (x2) | Task.Run sync -> Task.Run(async) + await |
| WinbootManager.RunProcessCaptured (~1329) | metodo ja async -> await |
| WinbootManager.ScanBcdEntriesAsync (2461) | Task.Run sync -> Task.Run(async) + await |
| WinpeBuilder.RunProcess (x2) | metodo ja async -> await |
| UpdateControlManager.RunProcess (130) | -> RunProcessAsync (WaitForExitAsync + timeout) |
| StoreEngine.NuGetLatestVersion (528) | -> NuGetLatestVersionAsync (await GetStringAsync) |
| SearchEngine (356/357, DISM/SFC) | novo ExecuteActionAsync + AddActionAsync; UI faz await |
| DriverManager.ExportDriverListToTxt (300) | -> ExportDriverListToTxtAsync |
| SystemUtils.RunExternalProcess (176) | shim reescrito como sincrono real (threads por stream) |
| SystemUtils.RunExternalProcessWithCode (228) | idem |

**Shims do SystemUtils**: 373 + 14 call sites impediam migrar tudo para async neste
lote. Em vez de sync-over-async, `RunExternalProcess`/`RunExternalProcessWithCode`
agora executam de forma sincrona de verdade via `ReadProcessOutputSync` (uma thread
por stream evita deadlock de pipe; `WaitForExit(timeout)` de 120s). Semantica de
saida byte-a-byte igual (ReadToEnd) e timeout igual. `RunExternalProcessAsync` e
`RunExternalProcessWithCodeAsync` continuam publicos como API async; o primeiro ja
tem chamadores reais (LocalInstallManager).

**Build**: Core 0 erros; GUI compila (o build so para no MSB3021/3027 com o
KitLugia.GUI.exe aberto). Auditoria: 0 metodos publicos mortos e 0 sync awaits no Core.
## Sessao 01/10 (cont. 3) - Limpeza final

- `.Result` do GUI migrados para `await` (MainWindow intro, QuickInstallPage.Run,
  StoreRemakePage): a auditoria secao 4 so aponta agora `tests/TaskManagerOpenBench`
  (benchmark proposital). Producao 100% sem sync-over-async.
- Kills com `catch { }` passaram a logar o padrao do projeto (EnablementPackageManager,
  StartupManager, FileTakeOwnership, UpdateControlManager).
- Ferramenta nova: `tests/quality_scan.py` (read-only) - catches vazios, GC.Collect e
  Kill por arquivo.

Nao abordado (registrado no AGENTS.md): 664 catches vazios (muitos em hot paths),
overlay morto KIT ISO STUDIO na IsoEditorPage, e refactors de WMI/ServiceController/
DllImport. Todos exigem decisao ou trabalho em lote dedicado.

## Sessao 02/10 - LatencyAnalyzer: medidor de latencia real

O motor do DashboardPage fabricava os numeros (Thread.SpinWait + fator 15x + `Random`
no "MeasureSingleLatency", DPC/ISR derivados da media das amostras, page faults =
`WorkingSet64/4096`, latencia por driver de dicionario hardcoded). Substituido por
medicao real no kernel/usuario - nenhum valor estimado:

| Metrica | Fonte real |
|---|---|
| DPC/ISR por CPU (100ns + contagem) | `NtQuerySystemInformation(SystemProcessorPerformanceInformation=8)`, struct 48 B |
| DPCs / context switches | `NtQuerySystemInformation(SystemInterruptInformation=23)`, struct 24 B |
| Page faults | `NtQuerySystemInformation(SystemPerformanceInformation=2)`, offset 60 |
| Resolucao do timer | `NtQueryTimerResolution` (Minimum = maior espera; Maximum = menor) |
| Latencia de despertar | sonda QPC + `Thread.Sleep(1)` em thread dedicada AboveNormal |

Estatisticas: min / media aparada 5% / p95 / max / desvio; DPC%/ISR% normalizados por
parede x numero de CPUs; DPCs/s, context switches/s, page faults/s. Score e estabilidade
(coeficiente de variacao) calculados sobre o que foi medido; cancelar no meio restaura o
estado original. A sonda usa `TaskCompletionSource` (nenhuma thread do pool bloqueada).

Validacao independente (probe PowerShell): structs 48/24 bytes exatas, timer
15,625 ms / 0,5 ms, DPC 0,16% e ISR 0,18% da maquina real, despertar medio ~15,5 ms
(granularidade do scheduler). A UI do DashboardPage passou a mostrar ms
(MEDIA/P95/MAX) + DPC/ISR/timer, e o DiagnosticPage usa `LatencyAnalyzer.ReleaseBuffers()`
em vez de reflection em campos que nao existem mais.

Build: Core 0 erros; GUI 0 erros; auditoria: 0 metodos publicos mortos / 0 sync awaits.

## Sessao 02/10 (cont.) - LatencyDriverAnalyzer: DPC/ISR por driver (ETW)

Novo `KitLugia.Core/LatencyDriverAnalyzer.cs` (estilo LatencyMon) + pacote
`Microsoft.Diagnostics.Tracing.TraceEvent 3.2.8` no Core.

- **Quirk do TraceEvent**: `TraceEventSession.EnableKernelProvider` falha no Windows 11 26100
  com `ERROR_WMI_INSTANCE_NOT_FOUND (4201)` nos dois caminhos (system logger com nome proprio e
  "NT Kernel Logger" via KernelTraceControl nativo). Workaround implementado: `StartTraceW` cru
  com `EVENT_TRACE_PROPERTIES` (LogFileMode=`0x08000100`, `Wnode.Flags=0x20000`, ClientContext=1,
  LoggerNameOffset=sizeof, LogFileNameOffset=+2048) e consumo com
  `ETWTraceEventSource("NT Kernel Logger", TraceEventSourceType.Session)`.
- Eventos: `PerfInfoDPC` / `PerfInfoTimerDPC` / `PerfInfoThreadedDPC` / `PerfInfoISR`
  (`ElapsedTimeMSec` = tempo real da rotina).
- Rotina -> driver: `NtQuerySystemInformation(SystemModuleInformation=11)`; entrada
  RTL_PROCESS_MODULE_INFORMATION de 296 B (x64): ImageBase@16, ImageSize@24, FullPathName@40.
- Requer admin; a sessao e unica no sistema (conflito com LatencyMon/WPR vira mensagem tratada).
- Metricas novas no `LatencyAnalyzer`: pior janela de DPC/ISR (250 ms), hard page faults
  (`PerformanceCounter Memory\\Pages Input/sec`), P50/P99 e MHz por nucleo
  (`NtPowerInformation(SystemProcessorInformation=11)`).

Teste real (harness `tests/LatencyMeterTest`, elevado): nvlddmkm.sys DPC max 1236,9 us,
dxgkrnl.sys ISR max 694,7 us, Wdf01000/tcpip/NDIS/storport/portcls/ntoskrnl - 0 eventos perdidos.
