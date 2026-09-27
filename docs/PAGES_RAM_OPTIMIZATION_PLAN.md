# 🧭 Plano — Otimização das Páginas do KitLugia (tempo de clique + RAM)

**Sessão:** 26/09/2026
**Escopo:** medir o custo real de **todas** as páginas do `KitLugia.GUI`
(construção, layout, tempo de "assentar" no clique, RAM retida, vazamento),
corrigir o que for comprovado e deixar um caminho de fases para o que sobra.

---

## 1. Como medimos (harness `tests/PagesBench`)

Não existe forma honesta de otimizar isso "no olho": o clique na sidebar
constrói uma página WPF nova (não há cache de páginas no `MainWindow`), e o
custo se divide em partes muito diferentes.

| Dimensão | O que é | Como o harness mede |
|---|---|---|
| **frio** | custo de `new XPagina()` na 1ª vez (caches do WPF vazios) | `Stopwatch` em volta do ctor na UI thread |
| **quente** | mesmo ctor com o WPF já aquecido | média de execuções seguintes |
| **layout** | `Measure`/`Arrange`/`UpdateLayout` da 1ª renderização | `Stopwatch` em volta dos 3 |
| **assenta** | tempo até o trabalho do `Loaded` terminar (é o travamento do clique) | `RaiseEvent(Loaded)` + bombeio até `ApplicationIdle`, com teto |
| **retido** | RAM gerenciada enquanto a página está viva | `GC.GetTotalMemory(true)` antes/depois |
| **sobra2/sobra3** | o que fica após `Cleanup()` em execuções seguidas | delta de memória pós-Cleanup+bombeio+GC |

CLI:

```bash
cd tests/PagesBench && dotnet build -v q -nologo
dotnet run --no-build -- leak                 # construção + retenção (todas as páginas)
dotnet run --no-build -- loaded --cap 2000    # mede o "assenta" (settle)
dotnet run --no-build -- --root DriversPage --once --wait 4   # veredito de vazamento de 1 página
dotnet run --no-build -- selftest / ctx / ctx2                # valida o próprio harness
```

### A regra de ouro do harness (aprendida na marra)

O modo `--root` é o **árbitro** de vazamento: cria a página, chama `Cleanup()`,
bombeia o Dispatcher 1,5 s, roda GC completo e pergunta a uma `WeakReference`
se a página ainda vive. O modo `leak` em lote é um **triador** barato (delta de
memória) e erra para mais quando há trabalho em voo.

> **Bug histórico do harness que gerou falsos "6 vazamentos":** a página era
> criada e anulada no *mesmo frame* que media, então a local de pilha a segurava
> e nem `new object()` era coletado. Corrigido separando criação/Cleanup em
> frames que saem antes do GC. O autoteste (`selftest`) prova isso.

---

## 2. Resultado do triador (48 páginas, modo `leak`)

Depois dos ajustes desta sessão (bombeio de 400 → **1200 ms** antes de medir a
sobra, porque páginas com carga no `Loaded` tinham trabalho em voo e eram
acusadas injustamente):

```
construção frio total (todas de uma vez): 679 ms na UI thread
construção quente média:                 3,8 ms/página
layout médio:                            20,6 ms/página
soma do retido por página:               15,2 MB
working set: 44 MB -> 188 MB

!! 1 página(s) vazando (sobra2 e sobra3 >512 KB): NetworkPage
```

E o veredito pelo árbitro:

```
NetworkPage:        t+1s ainda viva → t+2s LIBEROU — era trabalho em background, NÃO é vazamento
ProcessMonitorPage: NÃO vazou
OptimizationPage:   NÃO vazou
OBJECT / Page (controles): NÃO vazou
```

**Conclusão: nenhuma página do KitLugia vaza.** O que restou de `sobra3` alto é
trabalho assíncrono em trânsito (monitor de rede, scan de drivers, monitor de
processos) que termina entre 1 e 2 segundos depois do `Cleanup()` — comportamento
normal de app que bombeia o Dispatcher, não retenção.

### Ranking por custo de construção (frio, ms)

| Página | frio | layout | retido KB |
|---|---:|---:|---:|
| AboutPage | 194,0 | 41,3 | 754 |
| GameBoostPage | 95,9 | 20,8 | 1.192 |
| PrivacyPage | 71,9 | **213,6** | 182 |
| GlobalSearchPage | 51,5 | 1,4 | 928 |
| AllTweaksPage | 33,3 | 7,4 | 315 |
| WinTunePage | 17,4 | 9,6 | 1.169 |
| AdvancedRamCleanSettingsPage | 16,3 | 20,2 | 244 |
| AppsPage | 15,7 | 8,6 | 732 |
| TweaksPage | 14,7 | 50,7 | **1.499** |
| WinbootPage | 12,2 | 12,1 | 498 |
| NetworkPage | 11,9 | 25,5 | 514 |
| DriversPage | 11,1 | 14,0 | 504 |
| ExmTweaksPage | 10,3 | **53,2** | 1.143 |
| ServicesPage | 9,9 | 8,3 | 521 |

### Ranking por layout (o "trava e aparece" da 1ª renderização)

| Página | layout (ms) |
|---|---:|
| PrivacyPage | 213,6 |
| RepairsPage | 163,6 |
| ExmTweaksPage | 53,2 |
| TweaksPage | 50,7 |
| AboutPage | 41,3 |
| DashboardPage | 36,7 |
| WindowsPage | 29,6 |

---

## 3. Entregue nesta sessão

### 3.1 Bug real — diálogo "Erro ao carregar programas" (o que o usuário via "toda hora")

`AppsPage.LoadPrograms` e `ProgramsPage.LoadPrograms` assumiam que o
`SynchronizationContext` do WPF existia. Fora do loop de mensagens (ou em
qualquer chamada vinda de worker), todo `await` continua na ThreadPool e a
primeira escrita em controle WPF estoura
`InvalidOperationException: O thread de chamada não pode acessar este objeto`.

Corrigido nos dois arquivos:

- guarda de entrada `if (!Dispatcher.CheckAccess()) { await Dispatcher.InvokeAsync(LoadPrograms); return; }`;
- **toda** escrita de UI pós-`await` dentro de `await Dispatcher.InvokeAsync(...)`
  (não depende mais do contexto ambiental);
- `Logger.LogError("...LoadPrograms", ex.ToString())` antes do MessageBox — o
  stack passa a aparecer no log do Kit, não só a mensagem;
- `finally` que esconde o painel de loading também via dispatcher.

`ProgramsPage` é **órfã** (nada navega para ela) — em produção o diálogo era do
`AppsPage`. A correção dela fica como blindagem.

### 3.2 Padrões de construtor alinhados ao `_CONVENTIONS.md`

A convenção do projeto (e o item 8 dos Anti-Patterns) diz:
*"❌ Fazer trabalho pesado no construtor — usar `Loaded` + `async`"* e
*"❌ Usar lambda no `Unloaded += (s,e) => {...}`"*. Quatro páginas violavam isso:

| Página | Antes | Depois |
|---|---|---|
| `TweaksPage` | `Loaded += (s,e) => { _ = LoadCurrentStatus(); }` (lambda, não removível) | handler **nomeado** `TweaksPage_Loaded`, removido no `Cleanup` |
| `ExmTweaksPage` | `_ = LoadCurrentStatus()` **no ctor** + `Cleanup` não desinscrevia nada | `Loaded` nomeado + guard `_loadedOnce` + `Cleanup` desinscreve `Loaded`/`Unloaded` |
| `ProcessMonitorPage` | `DispatcherTimer` de 3 s **iniciado no ctor** | timer criado no ctor, `Start()` só no `Loaded` |
| `DriversPage` | 2× `Task.Run` de varredura de drivers + `CheckVerifierStatus()` **no ctor** | tudo movido para `DriversPage_Loaded` (nomeado, guard `_loadedOnce`), `Cleanup` desinscreve `Loaded` |

Ganho: construir a página deixou de disparar trabalho pesado antes de ela ser
exibida; o `Cleanup` volta a ser capaz de desligar tudo (sem lambda órfã).

### 3.3 Harness mais honesto

`FaseInterna` passou a bombear o Dispatcher por **1200 ms** (era 400 ms) antes de
medir a sobra — foi o que removeu os falsos positivos de
`ProcessMonitorPage`/`OptimizationPage` e deixou o triador alinhado ao árbitro.

---

## 4. Fases propostas (o que ainda vale atacar)

### Fase 1 — PrivacyPage e RepairsPage (layout de 213 ms / 164 ms) — **maior ganho visível**
São os dois piores "trava e aparece". Ação: medir com o `--root` qual subárvore
domina (`UpdateLayout` por seção), trocar `StackPanel` interno por `VirtualizingStackPanel`
onde houver listas longas e adiar o `Measure` de seções colapsadas. Meta: **< 80 ms**.

### Fase 2 — Abas pesadas de XAML: lazy real de subárvore
`WinTunePage` (2679 linhas), `TweaksPage` (2538), `ExmTweaksPage` (2373) constroem
**toda** a árvore no `InitializeComponent` (TweaksPage = 1081 nós, ExmTweaks = 864,
WinTune = 756). Ação: manter só a aba visível em árvore real e materializar as
outras no `SelectionChanged` (padrão `ContentControl` + `DataTemplate` diferido),
como o `AppsPage` já faz para Programas/Portáteis/Resíduos. Meta: cortar o
`retido` de 1,5 MB para < 500 KB nessas três.

### Fase 3 — `AboutPage` (194 ms frio)
Custo de construção desproporcional ao conteúdo (é uma página de créditos).
Ação: `--root AboutPage` + profiler para achar o que estoura; provavelmente
medidor/carregamento de ícone no ctor. Meta: **< 30 ms**.

### Fase 4 — `GameBoostPage` (96 ms frio)
Provável leitura de registry/estado no ctor. Mover para `Loaded` assíncrono.

### Fase 5 — Fechar o ciclo de "assenta" (`--loaded`)
O modo `--loaded` (settle) ainda não completou uma corrida inteira com o teto
alto. Rodar em porções com `--cap 2000` e registrar o pior caso de travamento
real de clique por página. É a métrica mais próxima da experiência do usuário.

### Fase 6 — Higiene contínua
- Rodar `leak` + `--root` no CI/nightly para pegar regressão de vazamento.
- Manter o padrão: nenhum `Task.Run`/`DispatcherTimer` no ctor; `Cleanup()`
  sempre desinscrevendo `Loaded`/`Unloaded`/eventos estáticos.

---

## 5. Guardrails (não repetir)

1. **`--root` decide vazamento; `leak` só tria.** Nunca declarar leak sem o árbitro.
2. **Bombeio antes de julgar.** Trabalho em voo ≠ vazamento.
3. **Nada de trabalho pesado no ctor** — é a origem tanto do jank de clique
   quanto da classe de bug cross-thread (contexto nulo fora do dispatcher).
4. **Toda escrita de UI pós-`await` de página deve assumir que o contexto pode
   ser nulo** — `Dispatcher.InvokeAsync` explícito, não confiança no ambiente.
