# TMOG Task Manager — Análise binária (IDA Pro 9.0)

**Alvo**: `C:\Program Files\Task Manager TMOG\TaskManager.exe` (6.0 MB, nativo x64, C++/MFC, sem .NET).
**Autor**: Dave Plummer — o Task Manager original do NT 4/2000/XP. Copia de análise:
`%TEMP%\opencode\tmog\` (`TaskManager.exe`, `TaskManager.exe.i64`, `decomp.txt` 4.260 linhas,
`report.txt`, `tmog_analyze.py`, `tmog_decomp.py`).

## Por que "funciona sem admin" — o achado central

O TMOG nunca abre um processo com direitos completos. Todo o acesso por processo usa
**dois níveis de direito, do mais alto para o mais baixo**:

```c
// decomp.txt (sub_14021ABA0, VA 0x14021ABA0):
HANDLE h = OpenProcess(0x1010, FALSE, pid);   // PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ
if (!h)
    h = OpenProcess(0x1000, FALSE, pid);      // fallback: só PROCESS_QUERY_LIMITED_INFORMATION
```

| Direito | Valor | Consegue sem admin? |
|---|---|---|
| `PROCESS_QUERY_INFORMATION` (0x0400) — o que `System.Diagnostics.Process` (.NET) usa | 0x0400 | **NÃO** para processos de sistema/protegidos |
| `PROCESS_QUERY_LIMITED_INFORMATION` (0x1000) | 0x1000 | **SIM** para todos os processos |
| `PROCESS_VM_READ` (0x0010) | 0x0010 | SIM (falha só para PPL) |

Com `PROCESS_QUERY_LIMITED_INFORMATION` dá para chamar sem admin:
`GetProcessTimes`, `GetProcessId`, `GetProcessIoCounters`*, `K32GetProcessMemoryInfo`*,
`QueryFullProcessImageNameW`, `GetPriorityClass`.

`*` = o TMOG trata falha com elegância: o processo continua na lista com o que dá para
obter, e um campo de "overlay" marca o que veio de fonte limitada (strings
`rights process overlay`, `GPU Engine counters and runtime-gated resident-mem`).

O .NET (`Process.GetProcesses()` no KitTaskManagerWindow.xaml.cs:509) abre cada processo
com `PROCESS_QUERY_INFORMATION | PROCESS_VM_READ` — por isso vários processos de sistema
aparecem sem CPU/memória/disco no kit e o usuário acha que "precisa de admin". O TMOG
mostra as métricas **de todos** porque pede o mínimo e aceita parcial.

## Inventário de APIs coletadas (imports confirmados + decompilação)

| Métrica | API do TMOG | O que o kit usa hoje |
|---|---|---|
| Lista de processos | `CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS)` + `Process32FirstW/NextW` (VA 0x140217870) — com cancelamento assíncrono (`TokenHandle` testado a cada iteração) e limite de 0x20000 entradas | `Process.GetProcesses()` |
| CPU por processo | `OpenProcess(0x1010→0x1000)` + `GetProcessTimes` → delta User+Kernel entre ticks | mesmo dado, mas com `Process.TotalProcessorTime` (handle completo → Access Denied) |
| Disco por processo | `GetProcessIoCounters` (ReadTransferCount/WriteTransferCount em 0x14021ABA0) | `PerformanceCounter("Process","IO Read Bytes/sec", inst)` — nenhuma falha de nome de instância |
| Memória por processo | `K32GetProcessMemoryInfo` (cb=0x50; WorkingSet@8, Pagefile/Committed@16, Quota@56, PeakWS@72) | `Process.WorkingSet64` (mesmo problema de handle) |
| CPU total do sistema | `GetSystemTimes` (resolvida dinamicamente; também nas strings `NtQuerySystemInformation`) | `PerformanceCounter("% Processor Time","_Total")` |
| Memória global | `K32GetPerformanceInfo` (cb=104: SystemCache×PageSize, CommitTotal/Limit, KernelPaged/NonPaged) — VA 0x1402414A0 / 0x140243120 | `PerformanceCounter("Memory", ...)` — 6 contadores separados |
| CPU por núcleo / topology | `GetLogicalProcessorInformationEx(RelationAll)` (VA 0x14023D570) | WMI ou contadores por instância |
| Frequência da CPU | `CallNtPowerInformation(ProcessorInformation)` — 24 bytes/núcleo `PROCESSOR_POWER_INFORMATION{Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdleState, CurrentIdleState}` (VA 0x14023E4F0) | não coleta |
| `% Processor Utility` (a métrica "real" do TM, normalizada por frequência) | `PdhAddEnglishCounterW(\Processor Information(_Total)\% Processor Utility)` (VA 0x140253EE0) | não coleta |
| Rede | `GetIfTable2` + `MIB_IF_ROW2.InOctets/OutOctets` + `FreeMibTable` (VA 0x14025B620) — não-admin, sem nomes "instanciados" | `PerformanceCounter("Network Interface","Bytes Sent/Received/sec", instância)` |
| GPU | `PdhAddEnglishCounterW(\GPU Engine(*)\Utilization Percentage)` + `\GPU Adapter Memory(*)\Dedicated Usage` (VA 0x140243120) | não coleta |
| Temperatura | `PdhAddEnglishCounterW(\Thermal Zone Information(*)\Temperature)` (VA 0x140270FC0) | não coleta |
| Sessões/usuário | WTSAPI32 (`WTSQuerySessionInformation` — strings) | — |
| Histórico de uso (App History) | SRUM via ESENT (`JetOpenDatabase...` — strings `windows.appHistory.srum`, `SRUM schema did not match the expected shape`) | não coleta |
| Bateria/energia | POWRPROF (`CallNtPowerInformation(SystemBatteryState)`) | não coleta |

## Técnicas de robustez (vale copiar além das APIs)

1. **Nomear contadores PDH só em inglês** (`PdhAddEnglishCounterW`) — imune a Windows pt-BR.
2. **Cancelamento cooperativo da enumeração**: o snapshot Toolhelp checa um token de
   cancelamento a cada `Process32NextW` ("Toolhelp process enumeration cancelled") — a
   janela fecha instantaneamente mesmo com 800 processos.
3. **Graceful degradation por processo**: cada métrica é opcional; falha do
   `GetProcessIoCounters` não tira o processo da lista, só marca o campo via overlay.
4. **PDH com query persistente**: o handle do `% Processor Utility` é aberto 1x e
   reutilizado (`static` + lazy init) — coleta é só `PdhCollectQueryData`.
5. **Fallback duplo em OpenProcess**: 0x1010 → 0x1000. Nunca 0x1FFFFF.
6. **ESENT para SRUM** com validação de schema ("SRUM schema did not match the expected
   shape") — lê o banco de App History do Windows sem desbloquear arquivos.

## Plano de adoção no KitLugia (proposto)

Novo arquivo `KitLugia.GUI/Windows/TaskManager/KitTaskManager.NativeMetrics.cs`
(P/Invoke puro, mesmo padrão do `NativeDiskIo.cs` do Core):

1. **Fase 1 — processos sem admin (maior impacto)**:
   - `NativeEnumProcesses()`: Toolhelp snapshot + `OpenProcess(0x1010→0x1000)` +
     `GetProcessTimes` + `K32GetProcessMemoryInfo` + `GetProcessIoCounters`.
   - Delta de CPU calculado no tick (mesmo modelo atual do kit, mas sem Access Denied).
   - Substituir `Process.GetProcesses()` (xaml.cs:509) mantendo a VM/colunas iguais.
2. **Fase 2 — gráficos baratos**: `GetSystemTimes` (CPU total), `K32GetPerformanceInfo`
   (memória/commit/cache), `GetIfTable2` (rede). Elimina ~10 `PerformanceCounter` e o
   bug de instâncias localizadas do PhysicalDisk/Network Interface.
3. **Fase 3 — extras do TMOG**: `CallNtPowerInformation` (frequência real por núcleo),
   PDH `% Processor Utility` e `\GPU Engine(*)\Utilization Percentage`.
4. **Não copiar**: ESENT/SRUM (bom para "App History", mas ESENT no processo do kit é
   risco de lock com o próprio Windows) e dxcore (GPU por processo já coberta pelo PDH).

Quirks do P/Invoke a lembrar: `IO_COUNTERS` (48B), `PROCESS_MEMORY_COUNTERS` cb=0x50
(80B, campo PeakWorkingSet no fim), `PROCESSOR_POWER_INFORMATION` 24B/núcleo,
`MIB_IF_TABLE2` tamanho variável (usar `GetIfTable2(null,&len)` 1ª chamada),
`K32GetPerformanceInfo` cb=104.

## Artefatos

- `report.txt` — imports + strings + seções PE (gerado por `tmog_analyze.py`).
- `decomp.txt` — 9 funções decompiladas (Hex-Rays) com os VAs da tabela acima
  (gerado por `tmog_decomp.py` via `idat.exe -S`).
- Banco IDA: `TaskManager.exe.i64` (reanalisar: apagar o .i64 antes, quirk já documentado).

## Validação dos pontos do Gemini (14/09) + implementação da Fase 1

Os 3 pontos eram válidos — e viraram código em `KitLugia.Core/TaskManager/NativeMetricsHelper.cs`:

1. **Delta de CPU com memória de estado** — implementado como `CpuDeltaTracker`
   (dicionário por PID com tick total + timestamp; delta dividido pelo tempo decorrido;
   `RemoveDead` descarta PIDs mortos; guard de `dMs > 50` contra amostras duplicadas).
   O GUI usa o tracker no caminho nativo e mantém o `_prevCpu` antigo no fallback .NET.

2. **Alocação dupla de tabela variável (GetIfTable2)** — válido e adotado como padrão
   do projeto (ficou para a Fase 2; o mesmo padrão already usado no `NativeDiskIo.cs`
   do Core). No NtQSI o equivalente é o loop `STATUS_INFO_LENGTH_MISMATCH → retry 2×`.

3. **NtQuerySystemInformation dinâmica** — implementado: `GetModuleHandleW("ntdll.dll")`
   + `GetProcAddress` + `Marshal.GetDelegateForFunctionPointer`. **Dois quirks reais
   encontrados na implementação**:
   - `GetProcAddress` é **ANSI-only** — declarado com `CharSet.Unicode` retorna 0
     silenciosamente (o TMOG importa `LoadLibraryA`/`GetProcAddress` pela mesma razão).
   - Resolvida a API, o TMOG usa `GetSystemTimes`/Toolhelp como fallback documentado
     na própria string do binário.

### Bugs encontrados e corrigidos na implementação (armadilhas para não repetir)

1. **`NextEntryOffset` é RELATIVO À ENTRADA ATUAL** — avançar com `baseAddr + next`
   funciona só na entrada 0 e lê lixo a partir da 2ª (foi a fonte de dois crashes
   `AccessViolationException`/CLR fatal 0x80131506 antes do fix). Avançar com
   `p += next` (acumulando), como o walk de referência fez.
2. **`UNICODE_STRING.Length` é USHORT** — lido como Int64 mistura o `MaximumLength`
   (12 | 16<<16 = 0x10000C) e todos os nomes saem vazios (ou o guard rejeita).
3. **`PtrToStringUni` com ponteiro ruim é FATAL** (Internal CLR error 0x80131506,
   não-capturável com try/catch). Defesa: validar `baseAddr <= buf && buf+len <= bufEnd`
   ANTES de chamar. Mesma disciplina vale para qualquer ponteiro que vem do kernel.
4. **Bounds do walk**: usar `needed` (bytes escritos pelo kernel, não o alloc) como fim
   e exigir header completo (0xF0) antes de ler cada entrada.

### Layout do SYSTEM_PROCESS_INFORMATION (x64, Windows 26100) — VALIDADO EMPIRICAMENTE

Scripts: `%TEMP%\opencode\tmog\{debug_entry0,validate_fields,walk_bounds}.ps1`.
É o layout **publicado no winternl.h** (a hipótese de deslocamento SRCORE +48 estava
ERRADA — corrigida): nome@0x38, pid@0x50, ppid@0x58, handles@0x60 (ULONG puro),
WorkingSet@0x90, PrivatePageCount@0xC8, IO_COUNTERS@0xD0 (ReadOps/WriteOps/ReadBytes/
WriteBytes). Stride NÃO é fixo (header 624-656 + threads×80) — usar o NextEntryOffset
do kernel. Cross-check perfeito: WS/Private == `Get-Process` e IO ==
`GetProcessIoCounters` nos mesmos PIDs; e **Memory Compression** (inacessível via
OpenProcess) aparece com dados completos — a vantagem decisiva do caminho kernel.

### Estado da Fase 1

- `NativeMetricsHelper.EnumerateProcesses()`: NtQSI primário → Toolhelp fallback;
  330 processos em ~6-8ms (vs ~40-90ms do `Process.GetProcesses()` com handles).
- `ProcessIoHelper.SampleProcessIoFromTotals()`: IO rate dos totais do snapshot
  (sem 2ª OpenProcess por PID).
- GUI: caminho nativo em `RefreshAsync` (auto-desativa em falha → fallback .NET),
  threads reais no lugar de "—", CPU via tracker, janelas via `EnumWindows`.
- Teste de runtime standalone: `%TEMP%\nttest\` (330 processos, Memory Compression OK,
  delta CPU OK). Build: 0 erros.

## Fase 2/3 implementada (14/09) — paridade completa de metricas + fix dos icones

### Correcoes de bugs

1. **Icones sumiram (regressao da integracao nativa)**: o caminho nativo setava
   `IconPath = path` incondicionalmente, mas o loader incremental so processa linhas
   com `IconPath` VAZIO — nenhuma linha nativa recebia icone. Fix: `IconPath` so
   recebe `path` quando `iconNow != null` (marca "resolvido"); sem path e resolvivel
   pelo nome (System32), a linha entra no lote do loader (antes ficava generica
   para sempre). `GetCachedIcon` + fallback por nome cobrem o 2o refresh.
2. **PERFORMANCE_INFORMATION com uint quebrava no x64** (erro 24 = ERROR_BAD_LENGTH):
   os campos sao SIZE_T (8 bytes no x64, sizeof=104). Corrigido para UIntPtr +
   fallback `GlobalMemoryStatusEx` (`dwMemoryLoad` e a porcentagem pronta).
3. Coluna GPU mostrava "—" para todo processo: nao havia GPU por PID.

### Paridade TMOG implementada

| Metrica | Origem | Status |
|---|---|---|
| CPU por processo | NtQSI + CpuDeltaTracker (Gemini #1) | OK (Fase 1) |
| Threads/handles/ppid reais | NtQSI | OK (Fase 1) |
| GPU por processo | `GpuMonitor.GetGpuUtilizationPerPid()` — pid_NNNN extraido das instancias `\GPU Engine(*)` (soma clamp 100, igual TM) | OK |
| CPU total (header) | `% Processor Utility` PDH `\Processor Information(_Total)` (metrica real do TM, normalizada por frequencia) + fallback GetSystemTimes | OK |
| Memoria (header) | K32GetPerformanceInfo (1 chamada) + fallback GlobalMemoryStatusEx | OK |
| Frequencia CPU | CallNtPowerInformation(ProcessorInformation) media CurrentMhz por nucleo | OK (header novo) |
| Temperatura | PDH `\Thermal Zone Information(*)\Temperature` (K->C; null = sem sensor, mostra "—") | OK (header novo) |
| Rede por PID | GetIfTable2/GetExtendedTcpTable (ja existia no kit) | OK |
| App History (SRUM/ESENT) | DECIDIDO NAO IMPLEMENTAR (lock de banco com o proprio Windows) | skip |

**Header novo no XAML**: colunas Frequencia + Temperatura (Auto) ao lado de GPU;
`UpdatePerformanceGraphs` alimenta via `NativeMetricsHelper` (escrita unica/tick,
SetMetricText anti-flicker). GPU header continua = max engine (padrao TM).

### Validacao em runtime (host real, 14/09)

- `utility=8-30%` (varia com carga real; 1a amostra e primer), `mem=42,8%`
  (== GlobalMemoryStatusEx 43%), `freq=3,50GHz`, GPU por PID 17,9% com navegador
  aberto (0 quando GPU ociosa), temperatura null com fallback "—".
- `procs=344 em ~7ms`. Build final: 0 erros (Core + GUI).
