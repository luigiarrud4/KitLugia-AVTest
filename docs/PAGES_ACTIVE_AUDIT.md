# Auditoria das Páginas Ativas — KitLugia GUI

Data: 28/09/2026 · Build: 0 erros · Escopo: **todas as páginas que o usuário consegue abrir**.

## 1. Objetivo

Passar uma peneira geral nas páginas navegáveis do app e verificar, uma a uma, se estão
em ordem: contrato de `Cleanup()`, `Unloaded`, trabalho pesado fora do construtor, timers
parados, eventos desinscritos, caminhos hardcoded e retenção de memória.

## 2. Universo real de páginas (como o usuário chega nelas)

Os pontos de entrada são:

| Origem | O que abre |
|---|---|
| Sidebar (hambúrguer) + toolbar | Dashboard, Tweaks, Apps, Storage(`CleanupPage`), Partitions, Network, Windows, Drivers, Repairs, Services, TraySettings, GameBoost, Diagnostic, Integrity |
| Busca global (`SearchEngine.AddNav` → `NavigateToPage(tag)`) | 36 tags (ver §5) |
| Cards do Dashboard | Tweaks, Storage, Apps, GameBoost, Windows, WinTune, Shrink, WinpeTools, ReinstallPreserve, WindowsUpdate, Tools(abas), Network, Privacy, Security, Integrity, IsoEditor, Partitions, Winboot, QuickInstall |
| Cards da `WindowsPage` | ForceStopUnlock, ContextMenuManagerPage, ContextMenuAddPage, StoreRemake |
| Cards da `AdvancedToolsPage` | IsoEditor, Winboot, Partitions |
| Engrenagem (topo) | `SettingsPage` |
| `KitStoreWindow` | `StoreRemakePage` (hospedada no Frame da janela da loja) |
| TM aba Diagnóstico | páginas por tag |
| Menu de contexto do Explorer (IPC) | ForceStopUnlock |

**Ativas: 42 páginas.**
Dashboard, Tweaks, Screen, Apps, Cleanup(Storage), Network, Windows, Tools, GameBoost,
Services, Repairs, Drivers, Partitions, Winboot, AdvancedTools, IsoEditor, Integrity,
Security, Privacy, Activation, TraySettings, Update, Diagnostic, Server, Rufus,
AdvancedRamCleanSettings, AllTweaks, ExmTweaks, Stutter, WinTune, QuickInstall, Shrink,
WindowsUpdate, WinpeTools, ReinstallPreserve, Settings, GlobalSearch, ContextMenu,
ContextMenuManagerPage, ContextMenuAddPage, ForceStopUnlock, StoreRemake.

**Código morto (6 páginas, nenhuma referência fora dos mapas de navegação):**

| Página | Situação |
|---|---|
| `AboutPage` | Substituída pelo GameBoost Pro no rodapé da sidebar ("ANTIGO SOBRE") |
| `BloatwarePage` | Nunca chamada — o fluxo de bloatware vive em `AppsPage` |
| `OptimizationPage` | Nunca chamada — absorvida por Tweaks/WinTune |
| `ProcessMonitorPage` | Nunca chamada — o monitor virou a KitTaskManager (janela separada) |
| `ProgramsPage` | Nunca chamada — `AppsPage` cobre |
| `ContextMenuPreviewPage` | O preview foi embutido na `ContextMenuPage`; a página isolada sobrônou |

Elas continuam compilando e **aparecem nos benchmarks** (por isso os benchmarks listam 48).
Não foram removidas nesta rodada (decisão do usuário pendente).

Observação: a `ContextMenuPage` é a única página ativa alcançável **apenas** pela busca
global (não há botão/card na UI). Fica como está — mas é um caminho obscuro.

## 3. Como foi auditado

Dois scripts read-only (ficam no repo para reuso):

```bash
bash tests/page_audit.sh          # Checklist por página (Cleanup/Unloaded/ctor pesado/timers/hardcoded)
bash tests/page_cleanup_audit.sh  # Corpo do Cleanup: para timers? desinscreve eventos estáticos? DataContext?
dotnet run --project tests/PagesBench -- leak   # Construção, layout e retenção por página
```

## 4. Achados e correções (nesta rodada)

| # | Página / arquivo | Problema | Correção |
|---|---|---|---|
| 1 | `ServerPage` | `Cleanup()` fazia `Unloaded -= Page_Unloaded`, mas a inscrição **nunca existia** → em navegação comum o Cleanup não rodava e o túnel/Playit não era encerrado | `this.Unloaded += Page_Unloaded;` no construtor |
| 2 | `QuickInstallPage` | Sem `Unloaded` → a pasta temporária de extração da ISO (`%TEMP%\KL_WIN_*`) ficava para trás | Handler nomeado + `Unloaded` + `DataContext = null` no Cleanup |
| 3 | `ShrinkPage` | Sem `Unloaded` → o `CancellationTokenSource` não era cancelado ao sair da página | Handler nomeado + `Unloaded` |
| 4 | `UpdatePage` | O construtor disparava `Task.Run(Confidence.Benchmark())`: **10.000 iterações × 25 pares × 2 implementações = 500.000 chamadas** (com P/Invoke) a cada abertura, só para escrever uma linha no log — o resultado nunca era lido | Chamada removida do construtor (o código do benchmark segue no Core) |
| 5 | `PartitionsPage` | Enumeração de discos no construtor (anti-pattern 8), competindo com o layout da navegação; e um bloco `_usageMonitorCts` duplicado no Cleanup | `LoadDisks()` movido para o `Loaded` (uma vez, em background); bloco duplicado removido |
| 6 | `ForceStopUnlockPage`, `ContextMenuManagerPage`, `ContextMenuAddPage` | `Unloaded += (_, _) => Cleanup();` — lambda não removível (anti-pattern 4) | Handlers nomeados + `Unloaded -=` no Cleanup (+ `DataContext = null` nas duas últimas) |
| 7 | `StoreRemakePage` | Cleanup embutido no handler do `Unloaded`, sem `public void Cleanup()` — fora do contrato que o MainWindow usa (reflection) | Extraído para `public void Cleanup()` |
| 8 | `SearchEngine` (busca global) | Tag de navegação `"Games"` **não existe** em `PageType` nem no `NavTagMap` → buscar "Jogos" respondia "EM BREVE" | Entrada `Jogos` virou `Windows` (a `WindowsPage` não tinha entrada alguma na busca; o GameBoost já tinha a sua) |
| 9 | `ServicesPage` | `path.StartsWith(@"C:\Program Files\WindowsApps")` — path absoluto (regra 1) que quebra com o Windows em outro volume | Resolvido via `Environment.SpecialFolder.ProgramFiles` |
| 10 | `WinpeToolsPage.xaml` (dicas) | Texto dizia "WinPE + config em `C:\Program Files\KitLugia\WinPE\` / Logs em `C:\KitLugia_WinPE_Log.txt`" — o kit é portátil e o log vai para a raiz do volume alvo | Texto corrigido (staging `C:\KL_WINPE`, log no volume alvo) |

## 5. Verificação da busca global (tags)

Extração de todas as tags de `AddNav` e confronto com `PageType` + `NavTagMap`:
**todas resolvem** depois do item 8 (antes: `Games` → "EM BREVE").

## 6. Evidências (PagesBench `leak`, 48 páginas, 3 corridas)

- construção frio total (todas de uma vez): **656–801 ms** na UI thread
- construção quente média: **3,5–3,9 ms/página**
- layout médio: **19,4–21,1 ms/página**
- soma do retido por página: **15,3 MB** · working set: 44 MB → 186/194 MB
- **nenhuma página reteve >512 KB depois do Cleanup() em 2 corridas seguidas** — sem vazamento
  nas páginas ativas (as duas que aparecem com sobra isolada são mortas: `ProcessMonitorPage`)

Hotspots de layout (backlog conhecido, não é vazamento): `PrivacyPage` ~190 ms,
`RepairsPage` ~167 ms; de construção: `GameBoostPage` ~88 ms, `GlobalSearchPage` ~54 ms.

## 7. Falsos positivos (para não reabrir)

Os itens abaixo foram sinalizados pelos scripts e **verificados manualmente como OK**:

- `DiagnosticPage`: `_monitorTimer`, `_cleanupTimer`, `_cachedTrayTimer` etc. são **nomes de
  campos de OUTROS componentes**, lidos por reflection (a página é o visualizador de timers).
- `NetworkPage`: o timer real é `_refreshTimer`/`_dnsTimer` (parados no Cleanup); o match veio
  do campo `_timersStarted`.
- `SettingsPage` / `StutterPage`: usam `AudioGlitchMonitor.Instance` (singleton) e o próprio
  `StutterDetector` — `StutterPage` desinscreve e descarta o detector no Cleanup.
- `WinpeToolsPage` "path hardcoded": é um **regex de log** (`C:\KL_WINPE\ deletado`), não um
  caminho de recurso.
- `ScreenPage`: o trabalho do construtor é `GetSystemMetrics` (barato). `ContextMenuAddPage`
  e `ContextMenuPreviewPage`: o `Task.Run` está no `Loaded`, não no construtor.
- Lambdas em `Loaded`/`Unloaded` próprios: como o alvo é o próprio `this`, não criam raiz
  externa — não vazam (segue valendo como item de estilo, não de bug).

## 8. Backlog sugerido

1. Decidir o destino do código morto (6 páginas) — remover ou reancorar na UI.
2. Investir nos dois layouts caros (`PrivacyPage`, `RepairsPage`) — mesmo item da "Fase 1" do
   plano de páginas.
3. `ContextMenuPage`: dar um ponto de entrada visível (hoje só a busca alcança).
4. Padronizar `Loaded` lambdas → handlers nomeados nas páginas restantes.
