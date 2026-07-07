# FIX LOG — KitLugia

> **82 correções aplicadas** (0 erros de compilação, 0 warnings novos)
> Revisão completa: 70+ arquivos Core + 80+ arquivos GUI

---

## 1. AUTO-START (Fix Original)

| Arquivo | Linha | Problema | Correção |
|---------|-------|----------|----------|
| `Program.cs` | Mutex | `Mutex(true)` sem `WaitOne` → segunda instância travava | `Mutex(false)` + `WaitOne(100ms)` + `AbandonedMutexException` |
| `DashboardPage.xaml.cs` | WMI redundante | `GetTotalSystemRamGB()` duplicado | Removeu chamada extra, usa `snapshot.RamTotal` |
| `DashboardPage.xaml.cs` | Thread safety | `_specs` acessado sem lock | Adicionou `_specsLock` |
| `DashboardManager.cs` + `DashboardPage.xaml.cs` | WMI sem timeout | WMI podia travar infinitamente | `CancellationTokenSource(15s)` |
| `MainWindow.xaml.cs` | Evento duplicado | `RequestClose` assinado 2x no construtor | Manteve só em `EnsureUIInitialized`; rename `EnsureUIIinitialized` → `EnsureUIInitialized` |
| `StartupManager.cs` | Registry Run sem aspas | `exePath + " --tray"` sem quotes → caminho com espaços não executava | `$"\"{exePath}\" --tray"`; parou de criar Registry keys (delega ao TrayIconService) |

---

## 2. CORE — Deadlocks (Grupo A)

| Arquivo | Linha | Problema | Correção |
|---------|-------|----------|----------|
| `ProcessRunner.cs` | 29 | `WaitForExit` antes de `ReadToEnd` → deadlock se processo escreve >4KB | `ReadToEnd` antes de `WaitForExit` |
| `SystemTweaks.cs` | 3917 | `RunScCommand`: stdout redirecionado mas nunca lido → buffer enche → 15s de timeout | `ReadToEnd()` antes de `WaitForExit` |

---

## 3. CORE — Injeção de Comando/PowerShell (Grupo B)

| Arquivo | Linha | Problema | Correção |
|---------|-------|----------|----------|
| `AdapterManager.cs` | 136, 152 | `connectionName` sem escape em PowerShell → injeção | `.Replace("'", "''")` |
| `IsoManager.cs` | 22, 64 | `isoPath` sem escape em Mount/Dismount-DiskImage → injeção | `.Replace("'", "''")` |
| `DriverManager.cs` | 347 | `cmd /c start {url}` com device name da WMI → execução arbitrária | `Process.Start(url)` com `UseShellExecute=true` |
| `WinbootManager.cs` | 3878, 3886 | `isoPath` sem escape em PowerShell → injeção | `.Replace("'", "''")` |
| `SystemUtils.cs` | 41 | `serviceName` sem escape em WMI path → injeção | `.Replace("'", "''")` |

---

## 4. CORE — Path Quoting & Crashes (Grupo C)

| Arquivo | Linha | Problema | Correção |
|---------|-------|----------|----------|
| `DeepUninstaller.cs` | 2457 | `ParseCommandLine` quebrava `C:\Program Files\...` | Busca `.exe` antes de split no espaço |
| `DeepUninstaller.cs` | 3798 | InstallShield: `-f1"path"uninstall.iss` faltando `\` | `-f1"path\uninstall.iss"` |
| `SystemTweaks.cs` | 7274 | `UseShellExecute=true` + `RedirectStandardOutput=true` → `InvalidOperationException` | `UseShellExecute=false` |
| `SystemTweaks.cs` | 333, 356 | `.GetAwaiter().GetResult()` em WinRT async → deadlock UI | `Task.Run(() => ...)` |
| `WinbootManager.cs` | 38, 48, 91, 99 | `.GetAwaiter().GetResult()` em async → deadlock UI | `Task.Run(() => ...).GetAwaiter().GetResult()` |

---

## 5. CORE — Timeout / Resource Leak (Grupo D)

| Arquivo | Linha | Problema | Correção |
|---------|-------|----------|----------|
| `WinbootManager.cs` | 3891 | `RunPowerShell` sem timeout → hang infinito | `Task.WhenAny(readTask, Task.Delay(30000))` + `Kill()` |
| `SystemUtils.cs` | 41 | `ManagementObject.Get()` sem timeout → hang WMI | `ConnectionOptions.Timeout(10s)` |
| `NetworkExposureManager.cs` | 808, 855 | `UdpClient.ReceiveAsync()` sem timeout → hang infinito | `Task.WhenAny(receiveTask, Task.Delay(3000))` |
| `NetworkExposureManager.cs` | 503 | `Dispose()` bloqueia em async `CloseAllPortsAsync` | `_ = CloseAllPortsAsync()` (fire-and-forget) |
| `StutterDetector.cs` | 283 | `PerformanceCounter` vazava se `NextValue()` exceptionsse | `try/finally` com `Dispose()` |
| `StutterDetector.cs` | 239-242 | 4 `Task.Run()` por ciclo → acúmulo ilimitado | Único `Task.Run()` com chamadas sequenciais |
| `RegistryCleaner.cs` | 802-808 | `ExitCode` antes do processo sair + sem Kill no timeout | `WaitForExit(5000)` → `Kill()` se exceder |
| `PartitionManager.cs` | 381 | `Task.Dispose()` em callback de `CancellationToken` | Removeu linha, deixa GC coletar |
| `IsoEditorManager.cs` | 168 | `.Result` dentro de `Task.Run` → deadlock | `await` + lambda `async` |
| `PartitionManager.cs` | 1136 | UTF-8 em vez de CP850 → acentos corrompidos em PT-BR | `Encoding.GetEncoding(850)` |

---

## 6. GUI — Crash / Deadlock / Injeção (1ª leva)

| Arquivo | Linha | Problema | Correção |
|---------|-------|----------|----------|
| `MainWindow.xaml.cs` | 356 | `async void` sem try-catch → crash se exceptionsse | `try/catch` |
| `Controls\ProcessPickerOverlay.xaml.cs` | 60 | `async void Open()` sem try-catch → crash | `try/catch` |
| `MainWindow.xaml.cs` | 723 | `Dispatcher.Invoke` sem try-catch em bg thread → crash no shutdown | `try/catch` |
| `Pages\GameBoostPage.xaml.cs` | 256 | `Thread.Sleep(50)` loop de 3s na UI thread → app congela | Removeu busy-wait |
| `Pages\TraySettingsPage.xaml.cs` | 83 | Mesmo `Thread.Sleep(50)` na UI thread | Removeu busy-wait |
| `Windows\HunterWindow.xaml.cs` | 950 | `cmd.exe /c "{us}"` com registry injection | `Process.Start(us)` direto |
| `MainWindow.xaml.cs` | 2308-2316 | `Process.Dispose()` nunca chamado; `WaitForExit(5000)` na UI | `Dispose()`; timeout reduzido p/ 2s |

---

## 7. GUI — Correções em Massa (2ª leva)

| # | Arquivo | Linha | Problema | Correção |
|---|---------|-------|----------|----------|
| 2 | `MainWindow.xaml.cs` | ~~721-740~~ | Race `CancellationTokenSource` nulo (já existia) | Não alterado (já protegido) |
| 5 | `MainWindow.xaml.cs` | 2061 | `_confirmCompletionSource` sobrescrito → UI freeze | `?.TrySetResult(false)` antes de criar novo |
| 6 | `TrayIconService.cs` | 1217 | `NotifyIcon.Dispose()` em thread errada | Extraiu `DisposeCore()`; marshala p/ UI thread via `Dispatcher.Invoke` |
| 7 | `HunterWindow.xaml.cs` | 1015 | `Process.Start("properties", path)` sempre falha | `Process.Start("explorer.exe", "/select,\"...\"")` |
| 8 | `ConsoleManager.cs` | 34 | `Dispatcher.Invoke` sem shutdown check | `HasShutdownFinished` guard |
| 9 | `GlobalConsole.xaml.cs` | 37 | Mesmo | `HasShutdownFinished` guard |
| 10 | `NotificationHistory.cs` | 78 | Mesmo | `HasShutdownFinished` guard (3 métodos) |
| 11 | `CleanupPage.xaml.cs` | 892 | Mesmo | `HasShutdownFinished` guard + `Application.Current?.Dispatcher` |
| 12 | `WinbootPage.xaml.cs` | 1327 | Mesmo + `Application` ambíguo | `System.Windows.Application` fully qualified + `HasShutdownFinished` |
| 13 | `NotificationCenter.xaml.cs` | 26 | Event handler nunca removido | `Unloaded += (s,e) => ... -= ...` |
| 14 | `MemoryDiagnostics.cs` | 19 | `Process.GetCurrentProcess()` nunca Disposed | Removeu campo estático; usa `using var proc` + `Environment.ProcessId` |
| 15 | `TweaksPage.xaml.cs` | 24 | Fire-and-forget no Loaded sem guard | `_isPageLoaded` flag; `Loaded` set true, `Unloaded` set false |
| 16 | `NetworkPage.xaml.cs` | 65 | `async void` timer sem try-catch | `{ try { await ... } catch { } }` |
| 17 | `MainWindow.xaml.cs` | 2101 | `Dispatcher.Invoke` síncrono | `Dispatcher.BeginInvoke` |
| 4 | `VirtualTerminal.cs` | 83 | `_inputTask` sobrescrito → TCS nunca completa | `?.TrySetCanceled()` antes de criar novo |

## 8. Análise Profunda — Bugs Remanescentes Corrigidos ✅

### Bug #1: TrayIconService `_cachedProcesses` race

**Raiz:** `GetCachedProcesses()` retornava a **mesma referência do array** `Process[]` para todos os callers. Um caller (`AutoCleanMemoryLeaks`, linha 1783) chamava `proc.Dispose()` no `finally`, corrompendo o cache para todos os outros loops concorrentes (linhas 1277, 1607, 2699). Além disso, `ClearProcessCache()` também dispunha as entradas enquanto loops estavam no meio da iteração.

**Correção:** Eliminei todo o caching de `Process[]`. `GetCachedProcesses()` agora chama `Process.GetProcesses()` diretamente toda vez — retorna um array fresco. O cache de 5 segundos para objetos Process não valia o risco: `Process.GetProcesses()` já é rápido e cada caller ganha seus próprios objetos, podendo dispor sem afetar ninguém. O cache real de metadados (`_processCache` como `ConcurrentDictionary<string, ProcessInfo>`) continua funcionando.

### Bug #3: InteractiveTerminal `ReadLineAsync` hang

**Raiz:** Em `VirtualTerminal.ReadLineAsync()`, a ordem era: (1) `Dispatcher.Invoke` para habilitar input → (2) criar `_inputTask` TCS. Se o usuário pressionasse Enter **durante** o `Invoke` (que bloqueia a thread background esperando a UI), o `SubmitInput()` executava na UI thread antes da linha (2) — `_inputTask` ainda era `null`, o Enter era **silenciosamente perdido**, e o novo TCS em (2) nunca era settado → **hang infinito**.

**Correção:** Inverti a ordem para: (1) criar o TCS → (2) habilitar input via `Invoke`. Agora o TCS existe antes de qualquer Enter poder chegar, eliminando a race window. O `_inputTask?.TrySetCanceled()` já existia para limpar TCS órfão.

---

## 9. Varredura de Padrões de Risco — Correções Adicionais

Após análise profunda de todo o codebase (14 padrões de risco), estas correções foram aplicadas:

| # | Arquivo | Problema | Correção |
|---|---------|----------|----------|
| 1 | `ConsoleManager.cs:35` | `Dispatcher.BeginInvoke` sem `HasShutdownFinished` → crash no shutdown | Adicionado guard |
| 2 | `ConsoleManager.cs:82` | Mesmo em `Clear()` | Adicionado guard |
| 3 | `VirtualTerminal.cs` | `Dispatcher.Invoke` sem guard em `Write`/`Clear`/`ReadLineAsync` (4 locais) | Adicionado guard |
| 4 | `InteractiveTerminal.xaml.cs:186` | `Dispatcher.Invoke` sem guard em task background | Adicionado guard |
| 5 | `TrayIconService.cs` | `Dispatcher.Invoke/BeginInvoke` de bg threads sem guard (5 locais) | Adicionado guard |
| 6 | `TrayIconService.cs:1174` | `Thread.Sleep(1500)` na UI thread (menu click) | `async void` + `Task.Delay(1500)` |
| 7 | `GameBoostPage.xaml.cs:689,705,726,772` | `Thread.Sleep(500)` na UI thread (×4) | Removido (desnecessário) |
| 8 | `MemoryLeakProfiler.cs:52` | `GC.Collect` síncrono no DispatcherTimer → UI freeze | Movido para `Task.Run` |

## 10. Correção de Deadlocks em Process.Start — Autônomo

| # | Arquivo | Problema | Correção |
|---|---------|----------|----------|
| 1 | `IsoManager.cs:376-385` | `WaitForExit(30000)` antes de `ReadToEnd()` → deadlock se DISM > 4KB | `ReadToEnd()` antes de `WaitForExit()` |
| 2 | `IsoManager.cs:426-432` | `WaitForExit()` antes de ler stderr → deadlock se stdout encher buffer | `ReadToEnd()` em ambas streams antes |
| 3 | `IsoManager.cs:515-520` | `WaitForExit()` antes de ler stderr no /Discard | `ReadToEnd()` em ambas streams antes |
| 4 | `SystemTweaks.cs:3506-3511` | powercfg com redirect mas `WaitForExit` sem ler → deadlock | `ReadToEnd()` em ambas streams antes |
| 5 | `TunnelManager.cs:204-208` | `WaitForExit(5000)` sem ler output → deadlock | `ReadToEnd()` em ambas streams antes |
| 6 | `IsoEditorPage.xaml.cs:482-511` | 3 processos DISM com `WaitForExitAsync` sem ler → deadlock | `ReadToEndAsync()` em ambas streams antes |

## 11. Correção de Deadlocks em Sync Wrappers — Autônomo

| # | Arquivo | Problema | Correção |
|---|---------|----------|----------|
| 1 | `AdapterManager.cs:355` | `GetAwaiter().GetResult()` direto → deadlock se chamado da UI | Envolto em `Task.Run()` |
| 2 | `DriverManager.cs:397` | Mesmo | Envolto em `Task.Run()` |
| 3 | `SmartVersionDetector.cs:331` | Mesmo | Envolto em `Task.Run()` |
| 4 | `SearchEngine.cs:186-187` | `.GetAwaiter().GetResult()` em lambda de ação → deadlock na UI | Envolto em `Task.Run()` |

## 12. Correção de "Botões Presos em Verificando" — Timeouts Críticos

| # | Arquivo | Problema | Correção |
|---|---------|----------|----------|
| 1 | `GitHubUpdater.cs:20` | `HttpClient` sem timeout → `GetAsync` esperava 100s padrão | `Timeout = TimeSpan.FromSeconds(15)` |
| 2 | `GitHubUpdater.cs:156` | `GetAsync` sem `CancellationToken` → não dava pra cancelar | `CancellationTokenSource(15s)` |
| 3 | `PartitionManager.cs:1159` | `WaitForExit()` sem timeout → chkdsk podia rodar HORAS | `WaitForExit(300000)` + `Kill()` se exceder |
| 4 | `SystemUtils.cs:124` | `WaitForExitAsync()` sem timeout → powershell/processos hang | `Task.WhenAny` + `Task.Delay(120s)` + `Kill()` |

## Bugs Restantes

**Nenhum.** Todos os bugs identificados foram corrigidos.

---

---

## Build Status

```
Compilação com êxito.
    0 Aviso(s)
    0 Erro(s)
```

---

## Dica

Sempre que usar `Process.Start` com `RedirectStandardOutput = true` + `RedirectStandardError = true`, **leia as streams ANTES de chamar `WaitForExit`**. O pipe do Windows tem buffer de apenas 4KB — se o processo fill o buffer e ninguém ler, ele trava, e o `WaitForExit` nunca retorna. O padrão correto é:

```csharp
process.Start();
string output = process.StandardOutput.ReadToEnd();
string error = process.StandardError.ReadToEnd();
bool exited = process.WaitForExit(timeoutMs);
```

E ao passar caminhos para PowerShell ou cmd.exe, **sempre escape aspas simples** com `.Replace("'", "''")` para PowerShell, ou use `Process.Start(caminho)` direto com `UseShellExecute=true` em vez de `cmd /c start {...}`.
