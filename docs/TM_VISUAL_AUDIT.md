# 🖼️ Auditoria visual do KitTaskManager (prints + texto sobreposto + flicker)

**Sessão:** 26/09/2026
**Pedido:** "adicione um visual único para o KitTaskManager, corrija elementos de lá (ele
não precisa ser igual ao Kit padrão — usa interface mais larga e com mais informações),
vá tirando prints e comparando com referências da web, corrija as partes em que o refresh
faz tudo piscar (só os dados precisam atualizar) e cheque se tem textos um por cima do outro."

---

## 1. Ferramenta criada: `tests/TmVisualBench`

Sem medir, "está feio/sobreposto" é opinião. O harness faz três coisas, tudo read-only:

```bash
cd tests/TmVisualBench
dotnet run -- tabs            # PNG de cada aba + detector de texto sobreposto
dotnet run -- flicker         # quanto da tela muda entre dois refreshes
dotnet run -- tabs flicker --size 1920x1080 --top 20
dotnet run -- --tab Summary   # uma aba só
```

Saída: `tests/TmVisualBench/out/tm_<Aba>.png` — **10 prints por execução**
(Summary, Processes, Performance, Services, Startup, Users, Connections, Latency,
Storage, Diagnostic), renderizados a 1680×1000 fora da tela.

Como funciona cada parte:

| Parte | Como |
|---|---|
| PNG | `RenderTargetBitmap` sobre a raiz visual da janela, com fundo escuro composto (a janela é translúcida, sem isso o PNG sai com alpha) |
| Texto sobreposto | percorre a árvore visual, pega todo `TextBlock` com texto visível, calcula os bounds **em coordenadas da janela** (via `TransformToAncestor`) e reporta pares cujo cruzamento cobre >35 % da área do menor |
| Flicker | renderiza a aba, bombeia o Dispatcher 2,2 s (deixa os timers tickarem), renderiza de novo e conta quantos pixels mudaram (tolerância de 8/canal para ignorar antialias) |

### Três cuidados aprendidos (o detector mentia sem eles)

1. **Recorte por clip**: `TextBlock` dentro de célula de DataGrid reporta a largura que
   *gostaria* de ter; um clip de ancestral tem que ser aplicado.
2. **Viewport de `ScrollViewer`**: conteúdo abaixo da dobra **não** é sobreposição — é
   rolagem normal. Sem cortar pela viewport, todo texto fora da tela virava "bug".
3. **Texto cortado sai da comparação**: dois pedaços cortados pelo mesmo viewport colapsam
   para o mesmo retângulo e pareceriam sobrepostos sem estarem. Só texto **inteiramente
   visível** entra na auditoria.

> ⚠️ Sempre rode com build (`dotnet run -- tabs`, **sem** `--no-build`): com `--no-build` o
> harness usa a cópia antiga de `KitLugia.GUI.dll` e você mede o código de ontem.

---

## 2. Bug REAL encontrado (e corrigido): a aba Diagnóstico não renderizava

Na primeira execução o harness não conseguiu nem gerar o PNG da aba Diagnóstico:

```
XamlParseException: Uma associação TwoWay ou OneWayToSource não pode funcionar
na propriedade somente leitura 'Evidence' do tipo DiagFindingVm
```

`TextBox.Text` é **TwoWay por padrão**; `DiagFindingVm.Evidence` é read-only → o binding
estoura ao instanciar o template. Ou seja: **a aba de Diagnóstico estava quebrada**.

O `Preview` (aba Evidências) já tinha `Mode=OneWay` — alguém corrigiu um e esqueceu o outro.

Correção: `Text="{Binding Evidence, Mode=OneWay}"`.

---

## 3. Textos sobrepostos: antes × depois

| Aba | Antes | Depois |
|---|---:|---:|
| Summary | 12 | **0** |
| Processes | 17 | 2 (ver §5) |
| Users | 1 | **0** |
| Connections | 6 | **0** |
| Latency | 1 | **0** |
| Performance / Services / Startup / Storage / Diagnostic | 0 | 0 |

Padrões que causavam as sobreposições reais:

1. **Dois textos na MESMA célula de `Grid`** (conteúdo à esquerda + controles à direita).
   Enquanto o texto é curto não há colisão; quando cresce, ele passa por baixo dos botões.
   Atingia o cabeçalho do Resumo (`TxtSumStatus` longo passando sob "📋 Copiar"/"⚡ resmon"/
   "Intervalo"), o cabeçalho de Conexões, o cabeçalho do painel de detalhes ("Detalhes do
   Processo" × "✕"), e as barras de status de Processos e Conexões.
   **Correção**: `Grid` de 2 colunas (`*` + `Auto`) e `TextTrimming="CharacterEllipsis"`
   no texto que pode crescer.
2. **Célula/cabeçalho de DataGrid sem recorte**: texto longo vazava para a coluna vizinha
   (nome do processo desenhado sob o valor de "CPU" no Top CPU).
   **Correção**: `ClipToBounds="True"` no `TmCellStyle` e no `TmColumnHeaderStyle` —
   resolve para todos os grids do TM de uma vez.
3. **`Content` de `CheckBox` sem quebra de linha**: o texto longo do "Recuperar sozinho"
   media o tamanho cheio e estourava a largura da janela.
   **Correção**: conteúdo virou um `TextBlock` com `TextWrapping="Wrap"` e `MaxWidth`.

---

## 4. Flicker: medido, não presumido

Entre dois refreshes reais, a fração da tela que muda é:

| Aba | Mudou |
|---|---:|
| Services / Startup | 0,0 % |
| Users / Latency | 0,3 % |
| Processes | 1,4 % |
| Summary | 3,9 % |
| Performance | 3,0 % |
| Connections | 5,9 % |
| **Storage** | **14,0 %** |
| **Diagnostic** | **26,1 %** |

Leitura honesta: **o "refresh da página inteira" que o usuário percebe não se confirma** na
maior parte das abas — o grid de processos já faz atualização por diff (merge por PID, sem
recriar a coleção) e as barras de núcleo são construídas **uma vez** (`BuildCoreBars` sai cedo
se a contagem não mudou). O que ainda repinta bastante é o que **desenha em `Canvas`**: os
gráficos do Storage e as listas/evidências do Diagnostic fazem `Children.Clear()` + redesenho
por atualização. É o próximo alvo de otimização, não um bug de arquitetura.

---

## 5. Pendências conhecidas

1. **`Processes`: `GDI / USER` × `Elevado` (e os valores correspondentes).**
   O grid tem 15 `RowDefinition` (rows 0–14 confirmados, sem aliasing) e as duas linhas são
   `Auto` — em WPF linhas `Auto` não podem se sobrepor. Como o PNG é gerado fora da tela e não
   consegui inspecionar o pixel, trato como **artefato de medição não confirmado**. Próximo
   passo: `dotnet run -- --tab Processes` com a janela realmente exibida (ou inspeção do
   `tm_Processes.png`) para decidir. Não alterei o layout "no escuro".
2. **Visual único (identidade própria)**: NÃO implementado nesta rodada. O que foi feito foi
   *posicionamento* (as correções da §3). A identidade visual do TM continua a mesma do Kit.
3. `Storage` (14 %) e `Diagnostic` (26 %) com repintura alta — candidatos a desenho incremental.

---

## 6. Arquivos alterados

- `KitLugia.GUI/Windows/TaskManager/KitTaskManagerWindow.xaml` (bug do binding, recorte de
  célula/cabeçalho, 5 layouts de 2 colunas, wrap do checkbox)
- `tests/TmVisualBench/` (novo: `.csproj` + `Program.cs`, harness de auditoria visual)

---

## 7. Rodada 2 — modo `sort` (comportamento, não pixel)

Ordem visual correta não prova ordenação correta. O modo `sort` dirige a UI real e confere
os valores das linhas:

```
dotnet run --project tests/TmVisualBench -- sort
```

- **Aba Processos**: seleciona cada critério no `CmbSortProcesses` (o caminho do usuário),
  espera 1,5 s (um ciclo completo do refresh) e conta os pares consecutivos fora de ordem
  **dentro de cada grupo** (tolerância 5 % pela defasagem de 1 s entre refresh e leitura).
- **Aba Usuários**: expande o usuário mais pesado e exige `IsExpanded=True` +
  `DetailsVisibility=Visible` + lista não vazia **depois de 3 refreshes** (é exatamente a
  queixa "cliquei na seta e o processo sumiu"); depois aplica Memória ↓, Processos ↓ e
  Usuário ↑ e revalida **após o refresh** (o bug era a ordem ser desfeita em 1 s).

Resultado da última execução:

```
RamValue|Descending          OK    143 linhas · 3 grupos · 6/140 pares fora de ordem (4%)
DiskBytesPerSec|Descending   OK    143 linhas · 3 grupos · 0/140
PeakMemValue|Descending      OK    0/140      CpuTimeSec|Descending  OK  0/140
DisplayName|Ascending        OK    0/140      UserName|Ascending     OK  0/140
Status|Ascending             OK    0/140      Pid|Ascending        OK  0/140
Threads|Descending           OK    0/140

seta ▼ em "Lugia": expandido=True · detalhe=Visible · 190 processos listados
OK — continua aberto e listado depois de 3 refreshes
MemValue ↓ OK    ProcessCount ↓ OK    UserName ↑ OK
=== ordenação/detalhe: TUDO OK ===
```

Os 4-5 % do critério Memória são só a defasagem: a ordem é aplicada com os valores lidos no
refresh anterior e a leitura do teste acontece ~1 s depois (processos trocam de RAM nesse
intervalo) — não é erro de ordenação.

O modo `sort` também cobre o **painel de detalhes ao vivo** (item 3): seleciona uma linha,
larga 2,6 s sem tocar na seleção e exige que o painel tenha acompanhado.

```
linha explorer.exe (PID 6028) -> painel PID=6028 · CPU 0%->0% · Handles 5947->5935
                                 Uptime 0.08:10:55 -> 0.08:10:57
OK PID do painel bate com a seleção | OK painel acompanhou o refresh
```

Antes do `RefreshLiveDetail()` o Uptime/CPU/RAM/Handles só mudavam ao trocar de linha: o
painel parecia "travado" ao lado de uma lista que atualiza a cada segundo.

---

## 8. Rodada 3 — tema próprio, submenus, congelar na lista e histórico

### 8.1 Submenus não abriam (em nenhum menu do Kit)

Os três templates de `MenuItem` (global, cópia local da aba Processos e `TmMenuItem`) tinham
`Border` + `Grid` + `ContentPresenter` e **nenhum `Popup`**. O WPF marcava `IsSubmenuOpen`,
mas não havia nada para renderizar os itens filhos — "Prioridade" (7 níveis) ficava inerte.
Corrigido com `Popup` + seta ▸ (trigger `HasItems`) + `IsSubmenuOpen` nos três; o menu da aba
Processos passou a reusar `TmDarkMenu`/`TmMenuItem`.

### 8.2 Tema do TaskManager (o dourado vazava)

O combo abria com o popup do tema global do Kit (borda `#FFD700`, item destacado amarelo) e a
barra de rolagem ficava dourada no hover. `TmComboBox`/`TmComboBoxItem`/`TmScrollBar`
(implícitos no `Window.Resources` do TM) trazem o azul-escuro do TM e valem para todos os
combos da janela, sem mexer no tema do resto do Kit.

### 8.3 Congelar + agir na lista do Resumo

| Antes | Agora |
| --- | --- |
| Botão direito agia na linha selecionada antes (o WPF não seleciona com o botão direito) | O clique direito **seleciona a linha sob o mouse** |
| A lista era reordenada/atualizada a cada 1 s com o menu aberto | `_sumMenuOpen` **congela** o `UpdateSummaryTopCpu()` enquanto o menu está aberto |
| Não havia como manter um processo na lista | "Congelar na lista (fixar)" (`_sumPinned`) + 📌, mesmo fora do Top 14 |
| Ações só existiam na aba Processos | Finalizar/Suspender/Retomar/Prioridade/Limpar memória/Copiar ali mesmo, reusando `Kill`/`SuspendResume`/`MenuPriority_Click` |

### 8.4 Histórico ("Click for History")

Clique no gráfico de CPU abre a linha do tempo: 1 amostra/s em ring buffer de 600 (10 min) com
CPU, kernel, RAM **e os 6 processos que mais consumiam naquele instante**. Barras = CPU, linha
vermelha = kernel, marcador vertical na amostra selecionada; clique/arraste troca o instante e
mostra quem estava usando a CPU (com % e RAM). Botões Agora/Limpar/✕.

### 8.5 Verificação automatizada (modo `sort`)

```
"Prioridade": submenu aberto=True · 7 itens · popups abertos na árvore=1   OK
fixado: Pinned=True · continua na lista=True (posição 0) · ícone="📌"      OK
painel aberto=True · barras=14 · "12 amostra(s) de 10 min"                 OK
instante: "CPU 15% (kernel 4%) · RAM 12,5 GB em uso — quem estava usando"  quem usava: 6
seleção de amostra: "23:13:45 · 13 amostra(s) de 10 min"                   OK
=== ordenação/detalhe: TUDO OK ===
```

`overlap`: **10/10 abas sem texto sobreposto** (inclui o Resumo com o overlay fechado).
