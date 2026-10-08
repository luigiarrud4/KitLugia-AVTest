# Plano de reescrita do KitTaskManager

> Estado: **fase 0 (arrumar o que está quebrado) concluída**. Este documento descreve a
> reescrita estrutural propriamente dita — o que fazer depois, em fases verificáveis.

## 1. Diagnóstico: por que ele "se parece demais com o kit" e trava

O Gerenciador de Tarefas tem hoje **14.540 linhas em 12 arquivos `partial`** da mesma
classe `KitTaskManagerWindow`:

| Arquivo | Linhas | Conteúdo |
|---|---:|---|
| `KitTaskManagerWindow.xaml` | 3.473 | 10 abas, 348 `x:Name`, 124 eventos |
| `KitTaskManagerWindow.xaml.cs` | 3.508 | ciclo de vida, timers, filtros, agrupamento, ações |
| `KitTaskManager.Storage.cs` | 1.377 | aba Disco/Armazenamento |
| `KitTaskManager.Summary.cs` | 1.179 | aba Resumo |
| `KitTaskManager.Performance.cs` | 1.080 | aba Performance |
| `KitTaskManager.Latency.cs` | 795 | aba Latência |
| `KitTaskManager.Diagnostic.cs` | 561 | Central de Diagnóstico |
| `KitTaskManager.Audio.cs` | 451 | cartão de áudio |
| `KitTaskManager.Services.cs` | 331 | abas Serviços + Inicialização |
| `KitTaskManager.Render.cs` | 271 | desenho dos gráficos |
| `KitTaskManager.StorageJournal.cs` | 220 | diário do armazenamento |
| `KitTaskManager.Loading.cs` | 206 | lazy-loading |

Problemas estruturais que a fase 0 **não** resolve (precisam da reescrita):

1. **Tudo é uma classe só.** Não existe separação entre *modelo de dados*,
   *coleta* e *apresentação*: qualquer aba nova toca nos timers, nos filtros e no
   `ApplyFilter` de todas as outras. É a origem de quase todo bug de estado compartilhado.
2. **Sem testes.** Nenhum teste cobre o agrupamento de processos, o merge de linhas nem
   os parsers de rede/disco — cada correção é medida "a olho" no app rodando.
3. **Acoplamento ao global.** A janela herda `Application.Current.Resources`; a
   identidade azul do TM é mantida por estilos implícitos *locais* que precisam
   listar cada controle (hoje cobrem Button/CheckBox/Separator/DataGrid*/ScrollBar/
   ComboBox). Qualquer controle novo volta ao dourado do Kit por omissão.
4. **Duplicação com `ServicesPage`.** Colas de tabela, cabeçalhos e a barra de
   filtros de `KitLugia.GUI/Pages/ServicesPage.xaml` foram copiados para dentro do TM.

## 2. Arquitetura proposta

```
KitLugia.GUI/Windows/TaskManager/
├─ Model/                     ← POCOs puros, sem WPF (testáveis)
│   ├─ ProcessRow.cs  ServiceRow.cs  StartupRow.cs  UserRow.cs
│   └─ PerfSample.cs  LatencyEvent.cs  DiskSample.cs
├─ Sampling/                  ← UM coletor por métrica, todos com CancellationToken
│   ├─ ISampler.cs  ProcessSampler.cs  PerfSampler.cs  DiskSampler.cs
│   └─ NetSampler.cs  UserSampler.cs  LatencySampler.cs
├─ Filtering/                 ← predicados puros (o que a busca "às vezes não funciona" virou)
│   └─ ProcessQuery.cs   (parser de "cpu:>10 ram:>500 chrome" + testes)
├─ Tabs/                      ← UM UserControl por aba, com a sua própria VM
│   ├─ SummaryTab.xaml(.cs)  ProcessesTab.xaml(.cs)  UsersTab.xaml(.cs)
│   ├─ ServicesTab.xaml(.cs) StartupTab.xaml(.cs)    LatencyTab.xaml(.cs)
│   ├─ StorageTab.xaml(.cs)  DiagnosticTab.xaml(.cs) ConnectionsTab.xaml(.cs)
│   └─ PerformanceTab.xaml(.cs)
├─ TmTheme.xaml               ← ResourceDictionary ÚNICO do TM (Dictionary + merge)
└─ KitTaskManagerWindow.*     ← só a casca: barra lateral, busca, ciclo de vida
```

### 2.1 Regra de ouro
**Um `DispatcherTimer` só**, no `KitTaskManagerWindow`, que a cada `N` segundos pede uma
amostra a cada `ISampler` registrado e publica um `TmSnapshot` (imutável). As abas
assinam o snapshot. Isso elimina a família de timers por aba (hoy: `_refreshTimer`,
`_graphTimer`, `_latTimer`, `_loadTimer`, debounce de busca) e a classe de bugs
"a aba X continua trabalhando com ela escondida".

### 2.2 Identidade visual
Mover toda a paleta `Tm*` para `TmTheme.xaml` e aplicá-la com
`Resources.MergedDictionaries` **no Window inteiro**, com estilos implícitos para
`Button`, `CheckBox`, `ToggleButton`, `RadioButton`, `ComboBox`, `ComboBoxItem`,
`TextBox`, `ScrollBar`, `ProgressBar`, `ToolTip`, `ListBox`, `MenuItem`, `Separator`,
`DataGrid*`. Um controle novo nasce no azul do TM sem precisar lembrar de nada.

### 2.3 Fim da duplicação com `ServicesPage`
Extrair para `KitLugia.GUI/Controls/Tm/`:
`TmToolbar`, `TmFilterBar`, `TmDataGrid` (com `IsDefaultStyle` único),
`TmSectionHeader`, `TmBadge`. As duas páginas passam a usar os mesmos controles.

## 3. Fases (cada uma com verificação própria)

| Fase | Entrega | Verificação |
|---|---|---|
| **F0** ✅ | Bugs de travamento, filtro, cartão do Resumo, card do Usuário, botões de Serviços/Inicialização/Latência, lixo removido | build 0 erros + `CheckUiThreading` exit 0 + teste manual do TM |
| **F1** | `Model/` + `Filtering/ProcessQuery.cs` com testes unitários | `dotnet test` verde; o filtro deixa de depender do `Dispatcher` |
| **F2** | `Sampling/` com um `ISampler` por métrica + `TmSnapshot` | 1 timer só; CPU do idle do TM cai (medir antes/depois com `Get-Process` do próprio TM) |
| **F3** | `TmTheme.xaml` + estilos implícitos; visual idêntico ao atual | screenshot antes/depois |
| **F4** | Extrair `Controls/Tm/`; `ServicesPage` passa a usar | duas telas visualmente iguais |
| **F5** | Aba por `UserControl`, começando por Resumo e Processos (as mais pesadas) | cada aba extraída continua funcionando isolada |
| **F6** | Abas restantes + remover os `partial` | `KitTaskManagerWindow.xaml.cs` abaixo de 400 linhas |

## 4. Riscos

- **Regressão silenciosa**: o agrupamento de processos e o merge de linhas do refresh
  têm heurística fina (anti-pisca, fantasma, pin). Por isso F5 começa pelo Resumo e
  pelos Processos, com o comportamento atual como referência.
- **Escopo**: F1–F6 é um trabalho de várias sessões. A fase 0 deixa o app usável
  enquanto isso.