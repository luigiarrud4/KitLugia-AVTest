# Startup do KitLugia e comportamento sob pressão

**Sessão**: 26/09/2026
**Relato do usuário**: *"enquanto o sistema estava sobrecarregado o kit simplesmente não conseguia responder — clicava e nada acontecia até aparecer aquele negócio que o app fica claro e diz o app não está respondendo"*.

---

## 1. Resumo executivo

O Windows marca a janela como **(Não Respondendo)** quando a **thread de UI** (a que bombeia as
mensagens do Win32/WPF) fica ~5 s sem processar a fila. Não é "falta de CPU" — é alguém **bloqueando
a thread de UI**.

Encontramos um bloqueio de verdade, e ele **piora exatamente quando o sistema está carregado**:

> O `MonitorTick` do `TrayIconService` — um `DispatcherTimer` que roda **na thread de UI** — fazia,
> a cada ciclo: enumeração de **todos** os processos, `MainWindowHandle` de cada um, criação/leitura
> de **PerformanceCounter de IO** (perflib), *trims* de working set por processo e **append síncrono
> em CSV**. Em máquina ociosa isso custa ~200–900 ms; com o sistema saturado infla para **segundos**
> → "(Não Respondendo)". Ironia: o Kit travava justamente quando deveria ajudar.

**Correções aplicadas** (build 0 erros):

1. `MonitorTick` agora **só agenda** o ciclo e o corpo roda **no thread pool** (com guarda de
   overlap); a thread de UI só pinta o ícone do tray (`PostTrayUi`).
2. `UpdateTrayIcon` **não recria bitmap/GDI nem chama `Shell_NotifyIcon`** quando o percentual não mudou.
3. O `Dispatcher.Invoke` **síncrono** que a limpeza de RAM fazia de dentro do thread pool virou
   `BeginInvoke` (não prende mais um worker do pool esperando a UI).
4. `ThreadPool.SetMinThreads(max(núcleos, 8))` no `Program.Main` — o startup dispara dezenas de
   `Task.Run` e o pool padrão injeta 1–2 threads/s (fila crescente = respostas atrasadas).
5. **Novo watchdog** `UiFreezeWatchdog`: mede a responsividade da UI e **loga**
   `[UI-FREEZE] Thread de UI travada por ~N ms …`. Da próxima vez o log diz o culpado em vez de
   virar adivinhação.

---

## 2. Caminho do startup (o que é UI e o que é background)

| Etapa | Onde | Trabalho | Veredito |
|---|---|---|---|
| `Program.Main` | UI (antes do WPF) | mutex de instância única, `PriorityClass.High`, (novo) `SetMinThreads` | ok (~ms) |
| `Program.Main` | ThreadPool | `EnsureAutoStartMethods`, `ReapplyContextMenuPrefs`… (via `App`) | ok |
| `App.OnStartup` | UI | `RenderOptions`, recursos de tema, `UnlockIpcServer.Start`, `new MainWindow()` | ok |
| `MainWindow` ctor | UI | XAML gigante (`InitializeComponent`) | inevitável |
| `MainWindow` ctor | UI, `BeginInvoke` **Background** | `LoadGoodbyeDPIConfig`, `_trayService.Initialize()`, `SearchEngine.Initialize`, health-check do tray | correto (deferido) |
| `MainWindow` ctor | ThreadPool | auto-start, updater (10 s), prewarm do KitTaskManager (4 s + `ApplicationIdle`) | correto |
| `Window_Loaded` → `EnsureUIInitialized` | UI | `MainFrame.Navigate(new DashboardPage())`, botões/estados | ok (custo da página) |
| `OnIntroFinished` | UI | fecha splash, liga timer do GoodbyeDPI | ok |

Conclusão: o **startup em si já está bem escalonado** (quase tudo deferido ou em `Task.Run`). O
problema de responsividade **não é o startup** — é o que passa a rodar na thread de UI **depois**.

## 3. O que roda na thread de UI depois do startup

Todos abaixo são `DispatcherTimer` → **executam na thread de UI**:

| Timer | Intervalo | Trabalho |
|---|---|---|
| `TrayIconService._monitorTimer` → `MonitorTick` | 30 s | **o grande culpado** (detalhe abaixo) |
| `GoodbyeDPI` status (`MainWindow`) | 2 s | cacheado (scan externo só a cada 10 s) — ok |
| `AggressiveMemoryCleaner` | 30 s | `GC.GetTotalMemory` + eventual limpeza (visual tree walk síncrono) |
| `UiFreezeWatchdog` (**novo**) | 0,5 s | 1 `BeginInvoke` de prioridade `Send` |
| `DiagnosticPage` / `TraySettingsPage` / `NetworkPage` / `PrivacyPage` (por página aberta) | 2–10 s | específico da página |
| `ExplorerTurboWatchdog` | 2 s | **background** (`System.Threading.Timer`) — ok |
| `BackgroundTaskTracker` | 1 s | barato |
| Hook de foreground (`SetWinEventHook`) | por evento | roda na thread de UI; fallback: timer de **250 ms** (`CheckForegroundWindow`, ~1 `GetProcessById` por troca) |

### 3.1 O `MonitorTick` (agora fora da UI)

Trabalho que ele fazia na thread de UI, com custo medido no host
(i5-14600KF, 349 processos, máquina **ociosa**):

| Operação | Medido (ocioso) | Sob carga |
|---|---|---|
| `Process.GetProcesses()` | **11 ms** | dezenas–centenas de ms |
| `MainWindowHandle` × 349 (`UpdateProcessProfiles`) | **22 ms** | idem |
| `PerformanceCounter("Process","IO Read Bytes/sec", inst)` — **1ª criação** | **874 ms** (carga do perflib do serviço "Process") | pior (enumerar instâncias num sistema saturado é caro) |
| `PerformanceCounter` por processo com TCP ativo (`SampleTraffic`) | **14–17 ms** cada | pior |
| `UpdateTrayIcon` (bitmap GDI + `GetHicon` + `Shell_NotifyIcon`) | ms, mas **IPC com o explorer** | sensível |
| `LogStats` (abrir/anexar/fechar CSV) | ms, mas **I/O de disco** | grave (disco saturado) |
| `DetectAndTrimLeaks` (itera **todos** os processos + `EmptyWorkingSet` por processo) | só liga com RAM ≥ 65 % | **era o pior caso: RAM cheia → UI-thread ocupada em trims** |
| `ApplyFireminOptimizations` (`GetProcessesByName` + `OpenProcess` por perfil VIP) | ms | pior |

Ou seja: quando a máquina ficava sob pressão (RAM ≥ 65 % e/ou CPU saturada), o Kit **aumentava**
a própria carga **na thread de UI** — comportamento auto-infligido. Agora todo esse cálculo é
background; a UI só faz o `BeginInvoke` do ícone.

## 4. Correções (arquivo:linha)

| Arquivo | Linha | O que mudou |
|---|---|---|
| `KitLugia.GUI/Services/TrayIconService.cs` | 2236–2272 | campos `_monitorTickBusy`/`_lastTrayPercent` + helper `PostTrayUi` (BeginInvoke, nunca Invoke) + `MonitorTick` só agenda `Task.Run(RunMonitorCycle)` com guarda de overlap |
| idem | 2273 | `RunMonitorCycle()` = corpo antigo do tick, agora no thread pool (comentário avisando "não tocar em UI daqui") |
| idem | 2328 | atualização de ícone/tooltip via `PostTrayUi` |
| idem | 4000–4037 | `UpdateTrayIcon` sai cedo se `percent == _lastTrayPercent` (sem GDI/`Shell_NotifyIcon` à toa) |
| idem | 3971 (`CleanRamNow`) | `Dispatcher.Invoke` (síncrono) → `PostTrayUi` (assíncrono) |
| `KitLugia.GUI/Services/UiFreezeWatchdog.cs` | novo | watchdog de responsividade (thread dedicada `BelowNormal`, probe em `DispatcherPriority.Send`, log `[UI-FREEZE]` com anti-flood de 5 s) |
| `KitLugia.GUI/App.xaml.cs` | 72 | `UiFreezeWatchdog.Start()` |
| `KitLugia.GUI/Program.cs` | 99–108 | `ThreadPool.SetMinThreads(max(núcleos, 8))` |

Nada de UI foi redesenhado e nenhuma regra de negócio mudou — é só **onde** o trabalho roda.

## 5. Como validar

1. Recompilar (fechar o Kit antes — MSB3021 trava a DLL).
2. Rodar o Kit e abrir `LocalAppData\KitLugia\logs` (ou o console de log do próprio Kit):
   - `[UI] Watchdog de responsividade ativo (limiar 1000 ms).` deve aparecer no startup.
   - Da próxima vez que o app "engasgar", aparece
     `[UI-FREEZE] Thread de UI travada por ~N ms (…)` — **com o horário**, para cruzar com o que
     estava rodando.
3. Reproduzir a carga (jogo + compactação/scan + 300+ processos) e clicar nas abas: a janela deve
   responder mesmo com o sistema saturado.
4. Conferir que o ícone do tray continua mudando de cor/percentual (o skip por igualdade não pode
   esconder mudanças reais).

## 6. Pendências (próximos ganhos)

- [ ] `SampleTraffic` (perflib) ainda é o item mais caro do ciclo: usar contadores nativos
      (`GetProcessIoCounters` via `NativeMetricsHelper`, que o KitTaskManager já usa) em vez de
      `PerformanceCounter`.
- [ ] `UpdateProcessProfiles`/`DetectAndTrimLeaks`: trocar `Process.GetProcesses()` +
      `MainWindowHandle` por um *snapshot* `Toolhelp32`/`EnumWindows` único (1 passe) — hoje paga
      N+1 no kernel.
- [ ] Fallback de 250 ms do hook de foreground (`CheckForegroundWindow`) — só liga se
      `SetWinEventHook` falhar; considerar 500–1000 ms.
- [ ] Páginas com timer próprio (`DiagnosticPage` 2 s, `PrivacyPage` 5 s, `TraySettingsPage` 2 s):
      mesmas regras — timer só agenda, trabalho vai para background se custar > ~5 ms.
- [ ] Levar `UiFreezeWatchdog.WorstStallMs/StallCount` para a Central de Diagnóstico (mostrar o
      histórico de travadas na UI).
- [ ] Timers pausados quando a janela está oculta (`Hide()`/tray): a maioria do trabalho de página
      não precisa rodar invisível.
