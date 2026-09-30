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
