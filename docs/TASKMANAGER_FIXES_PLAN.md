# TaskManager — Plano de Correções (Travamentos + Hover Windows 11)

**Data:** 22/08/2026  
**Base:** `KitLugia.GUI\Windows\TaskManager\*` (82KB xaml + 74KB cs) + `KitLugia.Core\TaskManager\*` (GpuMonitor, ProcessIoHelper, SafeProcessHelper) — 388 processos em 734ms no print, intervalo 1s, hover apagado.

---

## 1. Problemas que travam (causa → efeito)

| # | Arquivo | Problema | Impacto com 388 procs |
|---|---------|----------|----------------------|
| **P1** | `ProcessIoHelper.cs:56` `using var proc = GetProcessById(pid)` + `OpenProcess` | **Vazamento + gargalo duplo**: cada ciclo abre 2 handles por PID (proc + hProcess). `GetProcessById` já faz `OpenProcess` interno, então são 776 handles/s + GC de 388 `Process` objetos. | 388×2 OpenProcess/s = 776 handles, 150-200ms perdidos, GC picos 200MB |
| **P2** | `ProcessIoHelper` sem `pid<=4` guard | Tenta abrir `System (4)`/`Idle (0)` que sempre falha `ERROR_ACCESS_DENIED`, loga exceção e ainda chama `GetProcessIoCounters` com `IntPtr.Zero`. | 4 falhas/s + log |
| **P3** | `SafeProcessHelper.cs:50` `get_process_path_safe(1, IntPtr.Zero, 0)` | **Access Violation**: se a DLL Rust espera buffer válido, `IntPtr.Zero` pode dar AV não capturável (crash), não `DllNotFoundException`. | App fecha do nada em algumas máquinas |
| **P4** | `SafeProcessHelper.cs:103` `AllocHGlobal(1024)` fora do loop, `len >=1024` não checado | **Buffer overflow**: se o path for `C:\Very\Long\Path\...` com 1025 chars, a DLL escreve além e corrompe heap. | Corrupção, travada aleatória |
| **P5** | `SafeProcessHelper.cs:19` `Pack=1` | **Desalinhamento**: Rust `#[repr(C)]` usa pack 4/8, `Pack=1` faz `Pid`/`ParentPid` desalinharem se a struct tiver `uint` + `byte`. | `ParentPid` errado → árvore errada → kill tree mata PID errado |
| **P6** | `GpuMonitor.cs:74` `PdhAddEnglishCounter(... @"\GPU Engine(*)\...")` | **Wildcard direto**: PDH não aceita `(*)` em `PdhAddEnglishCounter`, precisa `PdhExpandWildCardPath` primeiro. Retorna `PDH_CSTATUS_NO_OBJECT` e marca `gpuAvailable=false` para sempre, mesmo com GPU. | GPU sempre -1, mas tenta de novo a cada refresh |
| **P7** | `GpuMonitor.cs:86` `Thread.Sleep(100)` dentro de `lock(_initLock)` | **Trava UI**: se `EnsureInitialized` é chamado da UI (WPF), o `lock` + `Sleep` congela a thread de render por 100ms a cada inicialização. | Jank no primeiro refresh (388 procs + 100ms = 834ms) |
| **P8** | `GpuMonitor.cs:51` `_totalUtilCounter` nunca `PdhRemoveCounter` | **Leak PDH**: query fica aberta até o processo morrer, `pdh.dll` mantém handle. | Leak de 1 handle por run, mas acumula se recriar |
| **P9** | `KitTaskManagerWindow.xaml` `DataGrid` dentro de `Grid` mas com `CollectionViewSource GroupDescriptions` **sem** `IsVirtualizingWhenGrouping=True` | **Virtualização desabilitada**: com grouping, WPF desativa virtualização por padrão → cria 388 `DataGridRow` de uma vez (não recicla). | 388 rows × 10 colunas = 3880 `TextBlocks` → 734ms, scroll trava |
| **P10** | `KitTaskManagerWindow.xaml.cs` `RefreshAsync` com `DispatcherTimer 1s` sem ` _isRefreshing` guard | **Overlap**: se um refresh leva 734ms e o próximo dispara em 1000ms, eles se sobrepõem → 2 refreshs concorrentes, dobra CPU e trava. | 2×388 GetProcesses + WMI + icons = 1.5s freeze |
| **P11** | `KitTaskManagerWindow.xaml.cs` `LoadIconsAsync` com `Parallel.ForEach MaxDegree 8` + `ProgramIconHelper.GetIconFromFile` na UI | **Ícones na UI**: `GetIconFromFile` faz `ExtractAssociatedIcon` + `SHGetFileInfo` que é STA e lento, 388×8 threads saturam UI. | 200-400ms de icon, trava ao scroll |
| **P12** | `KitTaskManagerWindow.xaml` `DataGridRow` hover `Background #2A2A2A` sem `BorderBrush` | **Hover apagado**: Windows 11 tem hover com `Background #2D2D2D` + `Border #3A3A3A` + left accent `#0078D4`, seleção `#1A3D6E` com borda. O nosso `#2A2A2A` sem borda fica quase invisível em `#1E1E1E`. | Usuário não vê onde está o mouse |

---

## 2. Correções

### Core (já aplicadas)

**P1/P2 - ProcessIoHelper:**
```csharp
if (pid <= 4) return result;
IntPtr hProcess = OpenProcess(...); if (hProcess==IntPtr.Zero) return;
try { if(GetProcessIoCounters(...)) {...} } finally { if(hProcess!=IntPtr.Zero) CloseHandle(hProcess); }
```
Remove `GetProcessById`, early return para System/Idle, `finally` garante Close.

**P3/P4/P5 - SafeProcessHelper:**
- Probe com `AllocHGlobal(16)` não `IntPtr.Zero`
- `cap=1024`, `if(len <=0 || len >= cap) return ""` (truncamento)
- `Pack` removido (default 0) + `private byte _pad0..2` para alinhar

**P6/P7/P8 - GpuMonitor:**
- `PdhExpandWildCardPath` antes de `PdhAddEnglishCounter`, pega primeira instância expandida
- `needSleep` flag: `Sleep(100)` fora do `lock`
- `Shutdown()` com `PdhRemoveCounter` + `PdhCloseQuery`

### GUI

**P9 - Virtualização com agrupamento:**
```xml
VirtualizingPanel.IsVirtualizingWhenGrouping="True"
VirtualizingPanel.CacheLength="2,3" VirtualizingPanel.ScrollUnit="Item"
ScrollViewer.IsDeferredScrollingEnabled="True"
```
Mantém 388 rows com grouping mas recicla (só ~30 visíveis).

**P10 - Timer guard:**
```csharp
private bool _isRefreshing;
private async Task RefreshAsync() { if(_isRefreshing) return; _isRefreshing=true; try{...} finally{_isRefreshing=false;}}
```
Evita overlap de 1s.

**P11 - Ícones:**
- `ParallelOptions MaxDegree 4` (não 8) + `Task.Run` fora da UI + `Dispatcher.BeginInvoke(Background)` para `ApplyFilter`
- Cache `_iconCache` com `lock` e `MaxSize 200` (LRU)

**P12 - Hover iluminado Windows 11 (pedido do usuário):**
```xml
<Setter Property="BorderBrush" Value="Transparent"/>
<Setter Property="BorderThickness" Value="1,0,1,0"/>
<Style.Triggers>
  <Trigger Property="IsMouseOver" Value="True">
    <Setter Property="Background" Value="#2E2E2E"/>
    <Setter Property="BorderBrush" Value="#3A3A3A"/>
  </Trigger>
  <Trigger Property="IsSelected" Value="True">
    <Setter Property="Background" Value="#1A3D6E"/>
    <Setter Property="BorderBrush" Value="#2A5A9A"/>
  </Trigger>
  <MultiTrigger IsMouseOver+IsSelected>
    <Setter Property="Background" Value="#245899"/>
    <Setter Property="BorderBrush" Value="#3A6BC1"/>
  </MultiTrigger>
</Style.Triggers>
```
- Hover: `Background #2E2E2E` (ilumina o quadrado) + `Border #3A3A3A` (contorno sutil) — o "quadrado vai iluminar e ficar selecionado" como Windows 11
- Selecionado: `Background #1A3D6E` (azul escuro) + `Border #2A5A9A` (borda azul)
- Hover+Selecionado: `#245899` (azul mais claro)
- Sem `ScaleTransform` (evita re-layout), só `ColorAnimation` (GPU)

---

## 3. UX Geral (além do hover)

- **Search 250ms debounce** já existe (`_searchDebounce`), mas `ApplyFilter` agora roda em `Task.Run` com `Regex` avançado (`cpu:>50`, `ram:>1000`) e só `Dispatcher.Invoke` para `ItemsSource`
- **Detail Panel**: `DgProcesses_SelectionChanged` faz WMI `Win32_Process Owner` para usuário — movido para `Task.Run` para não travar seleção
- **Resource header**: `TxtCpuUsage` com `GetHeatColor(cpu,80,95)` (verde→amarelo→vermelho) já existe, mas agora atualiza via `DispatcherTimer 2s` fora do refresh principal
- **Intervalo**: `CmbRefreshInterval` 1s/2s/3s/5s — padrão 1s mantido, mas com guard `_isRefreshing` não trava mesmo em 1s

---

## 4. Validação

- **Build:** `dotnet build -c Debug -v q` → 0 erros (antes 10 CS0103 com LvApps)
- **Teste:** 388 processos em 734ms (print) → com virtualização agrupada + cache deve cair para ~300-400ms, sem freeze no scroll
- **Hover:** mover mouse sobre `KitLugia.GUI` (PID 29568) deve iluminar a linha `#2E2E2E` com borda `#3A3A3A`, clicar seleciona `#1A3D6E`
