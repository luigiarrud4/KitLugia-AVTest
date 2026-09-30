# KitTaskManager — Mapa de Estilos e Cores

> Documento de referência do visual do **Gerenciador de Tarefas próprio do Kit**.
> Tudo dele vive em `KitLugia.GUI/Windows/TaskManager/KitTaskManagerWindow.xaml`
> (`Window.Resources`) + os partials `KitTaskManager.<Aba>.cs` que pintam Canvas/barras.
>
> Regra: **cor fora desta paleta é divergência**. Se precisar de uma cor nova, adicione
> o token aqui e justifique — não invente hex solto no meio de uma aba.

---

## 1. O problema que este mapa resolve

O TM é uma **janela azul** (`#4FC3F7`) dentro de um app cujo tema global é **dourado**
(`#FFD700`). O tema global (`Themes/*.xaml`) declara vários estilos **implícitos**
(Button, CheckBox, ComboBox, DataGrid*, Separator) que vazam para qualquer controle
que não tenha estilo próprio. Resultado antes desta sessão:

| Sintoma visível | Origem no tema global |
|---|---|
| Botão ficava **dourado** e dava "zoom" ao clicar | `Themes/Buttons.xaml` — estilo implícito de `Button` (`IsPressed` → `#30FFD700` + `ScaleTransform 0.95`) |
| Popup do combo com **borda/listra dourada** | `Themes/ComboBox.xaml` — implicit `ComboBox`/`ComboBoxItem` (`#FFD700`) |
| Cabeçalho de tabela **#1A1A1A com texto dourado e 40 px de altura** | `Themes/DataGrid.xaml` — implicit `DataGridColumnHeader` (`Foreground = AccentColor`) |
| Linha selecionada com **lavagem dourada** | `Themes/DataGrid.xaml` — implicit `DataGridRow` (`IsSelected` → `#25FFD700`) |
| CheckBox **22×22 com marca dourada** | `Themes/CheckBox.xaml` — implicit `CheckBox` |
| Separador cinza `#333` sem contraste | `Themes/Menus.xaml` — implicit `Separator` |
| Valores de CPU/RAM em **dourado puro** | `GetHeatColor()` devolvia `#FFD700` como "amarelo" (`KitTaskManagerWindow.xaml.cs`) |

Todas essas fontes foram neutralizadas **dentro do TM** (ver §4 e §5).

---

## 2. Paleta canônica (tokens)

Definidos como `SolidColorBrush` no topo do `Window.Resources`.

### Superfícies (do mais fundo ao mais claro)

| Token | Hex | Onde usar |
|---|---|---|
| `TmBgDeep` | `#0D131A` | poços/gráficos, console do diário, tooltips, fundo de barra de progresso |
| `TmBgWindow` | `#111823` | fundo da janela e das páginas |
| `TmBgCard` | `#141C28` | cartões, popups, menus, zebra das tabelas |
| `TmBgHeader` | `#1A2432` | cabeçalho de `DataGrid` |
| `TmBgControl` | `#1F2A3A` | combo, input, botão de barra, caixa do checkbox |
| `TmBgHover` | `#243040` | hover de linha de tabela / item de menu |
| `TmBgNavHover` | `#1C2634` | hover do botão da sidebar |
| `TmBgNavPress` | `#26334A` | pressionado da sidebar |

### Bordas

| Token | Hex |
|---|---|
| `TmBorder` | `#1F2A3A` |
| `TmBorderStrong` | `#2C3A4E` |

### Acento e estado

| Token | Hex | Papel |
|---|---|---|
| `TmAccent` | `#4FC3F7` | azul do TM: títulos de seção, ícones, foco, seleção de texto |
| `TmAccentDeep` | `#2196F3` | ponta do gradiente da pílula da aba ativa |
| `TmSelect` | `#17324F` | fundo da linha/aba selecionada |
| `#28507E` | (sem token) | selecionado + hover na mesma linha |
| `#2A2A30` / `#31465F` | (sem token) | grupo expandido (pai) na aba Processos |
| `#1B2937` / `#3A4C66` | (sem token) | linha filha (indentada) na aba Processos |

### Texto

| Token | Hex | Papel |
|---|---|---|
| `TmText` | `#EEEEEE` | corpo |
| `TmTextDim` | `#AAAAAA` | secundário |
| `TmTextMuted` | `#888888` | terciário / cabeçalho de coluna |
| `TmTextFaint` | `#666666` | legendas |
| `#555555` | (sem token) | texto "apagado" (rodapés de dica) |

### Semânticas

| Token | Hex | Papel |
|---|---|---|
| `TmGood` | `#4CAF50` | saudável / CPU |
| `TmWarn` | `#FF9800` | atenção / disco / temperatura |
| `TmBad` | `#F44336` | crítico / kernel / energia |
| — | `#FFD54F` | **âmbar de atenção** (escala de calor; era `#FFD700`) |
| `TmRam` | `#B36AE2` | memória |
| `TmNet` | `#9C27B0` | rede |
| — | `#7E57C2` | relógio (vital do Resumo) |
| — | `#4A6FA5` | memória em cache (barra composta) |
| — | `#9CC7E8` | texto monoespaçado do console (diário) |
| — | `#C42B1C` / `#E81123` | fechar janela / perigo (hover e press) |
| — | `#3A2A2A` | fundo de hover perigoso (Finalizar) |
| — | `#1E5C2E` / `#35C759` | botão "GERAR DIAGNÓSTICO" |

### Escala de calor (`GetHeatColor`)

`KitTaskManagerWindow.xaml.cs` — 4 degraus, conforme o valor vs. os limiares:

```
>= critical  -> _brushRed     #E81123
>= warn      -> _brushOrange  #FF9800
>= warn*0,6  -> _brushYellow  #FFD54F   (era #FFD700 — DOURADO do tema global)
senão        -> _brushGreen   #4CAF50
```

---

## 3. Estilos nomeados (`x:Key`)

| Chave | Tipo | Para que serve |
|---|---|---|
| `TmToolButton` | Button | botão de barra de ferramentas (Serviços, Inicialização, Latência, Armazenamento, Diagnóstico) |
| `TmDarkMenu` | ContextMenu | menu de contexto escuro (borda `#1F2A3A`, raio 8) |
| `TmMenuItem` | MenuItem | item de menu, com **Popup** de submenu (a seta ▸ só aparece com filhos) |
| `TmColumnHeaderStyle` | DataGridColumnHeader | cabeçalho padrão (`#1A2432` / `#888` / `Semibold` / `ClipToBounds`) |
| `TmRowStyle` | DataGridRow | linha padrão (hover `#243040`, seleção `#17324F`) |
| `TmCellStyle` | DataGridCell | célula padrão (`ClipToBounds` — sem isso o texto vazava para a coluna vizinha) |
| `TmGridRow` | DataGridRow | linha das tabelas que usam `TmGrid` (seleção **azul**, não a lavagem dourada do tema) |
| `TmGrid` | DataGrid | tabela densa de diagnóstico (Latência / Armazenamento / Diagnóstico) |
| `TmComboBox` / `TmComboBoxItem` | ComboBox | combo azul (o global era dourado) |
| `TmScrollBar` / `TmScrollBarThumb` | ScrollBar/Thumb | barra de rolagem azul (o global animava para dourado) |
| `TmCard` | Border | cartão padrão (fundo `#141C28`, borda `#1F2A3A`, raio 8, padding 12) |
| `TmSectionHeader` | TextBlock | título de seção (`11` / Bold / `#4FC3F7`) — unifica os antigos 10/11/12 |
| `TmBadge` | Border | selo de estado (`#333` / raio 4 / `7,2`) |
| `UserRowStyle` | DataGridRow | linha da aba Usuários (carrega `DetailsVisibility` do item) |
| `InfoTipWrap` | ToolTip | tooltip que quebra linha (textos longos da Latência) |

Estilos **implícitos do TM** (sem `x:Key`, valem para a janela inteira):

```xml
<Style TargetType="{x:Type Button}"/>                  <!-- hover/press escuros, SEM dourado e SEM zoom -->
<Style TargetType="{x:Type CheckBox}"/>                <!-- 15x15 azul -->
<Style TargetType="{x:Type Separator}"/>               <!-- #1F2A3A -->
<Style TargetType="{x:Type ComboBox}" BasedOn="{StaticResource TmComboBox}"/>
<Style TargetType="{x:Type ScrollBar}" BasedOn="{StaticResource TmScrollBar}"/>
<Style TargetType="{x:Type DataGridColumnHeader}" BasedOn="{StaticResource TmColumnHeaderStyle}"/>
<Style TargetType="{x:Type DataGridCell}" BasedOn="{StaticResource TmCellStyle}"/>
<Style TargetType="{x:Type DataGridRow}" BasedOn="{StaticResource TmRowStyle}"/>  <!-- FontSize 11 -->
```

> **Por que os DataGrid implícitos são obrigatórios:** o `TmGrid` declara cabeçalho/célula
> por **Setter de estilo**. Nas abas Latência/Armazenamento/Diagnóstico isso não bastava e o
> estilo **global** de `DataGridColumnHeader` (`#1A1A1A` + texto dourado + 40 px) vencia.
> Com os implícitos no `Window.Resources`, o `TmGrid` passou a render `#1A2432`/`#888`.
> Provado pelo `TmVisualBench gold`: **10/10 abas com 0 elementos dourados**.

---

## 4. Padrões por aba (o que já é consistente)

| Elemento | Valor |
|---|---|
| Barra de título | 40 px, `#111823`, borda inferior `#1F2A3A` |
| Sidebar | 58 px, botão 60×52, ícone **17 px**, rótulo 8 px |
| Título de aba | `18` / SemiBold / White |
| Contagem ao lado do título | `12` / `#777` |
| Cartão | fundo `#141C28`, borda `#1F2A3A`, **raio 8**, padding `12` (ou `12,10` / `10,8` / `14,12`) |
| Poço de gráfico/console | `#0D131A` |
| Zebra de tabela | `#111823` (fundo) alternando `#141C28` |
| Cabeçalho de coluna | `#1A2432` / `#888` / `11` SemiBold / padding `8,5` |
| Selo de estado | `#333` / raio 4 / `7,2` |
| Tooltip | `#111823` com borda `#4FC3F7` |

---

## 5. Divergências normalizadas nesta sessão

| Antes | Depois | Por quê |
|---|---|---|
| `#0E141B`, `#0B1018`, `#0B1119`, `#121212` (4 tons de "fundo de gráfico") | `#0D131A` | o gráfico de RAM usava `#121212`, um **cinza puro** fora do azul do TM |
| `#161F2C` (cartões da Latência/Armazenamento/Diagnóstico) | `#141C28` | dois tons de cartão diferentes entre abas |
| `#101822`, `#2A3A4E` | `#141C28`, `#2C3A4E` | borda de painel fora do par canônico |
| `#26262B`, `#26262C` (hover cinza) | `#243040` | hover sem o azul do TM |
| `#33445A` (seleção de célula) | `#17324F` | 4º tom de azul de seleção |
| `BorderBrush="#1F2A3A" ... CornerRadius="6"` (13 cartões) | `CornerRadius="8"` | Latência/Armazenamento/Diagnóstico com raio diferente do Resumo |
| Cabeçalho do `TmGrid`: `#141C28`/`#999`/`8,6`/`#333` | `#1A2432`/`#888`/`8,5`/`#1F2A3A` + `ClipToBounds` | alinhar ao `TmColumnHeaderStyle` |
| `_brushYellow = #FFD700` | `#FFD54F` | dourado do Kit dentro da escala de calor azul |
| Ícones da sidebar 14/15/16/17/18 px | 17 px em todos | 5 tamanhos para 10 botões |
| Títulos de seção `10` e `12` no mesmo conjunto | 11 px | Resumo usava 10 nas fichas inferiores |
| `TmToolButton` forçava `TextBlock.Foreground="White"` no `ContentPresenter` | removido; `Foreground` entrou no estilo | o `Foreground="#4FC3F7"` local era **ignorado** (ex.: `services.msc`, `resmon`) |

---

## 6. Overlay "Click for History" (Resumo)

O painel `PnlCpuHistory` cobre a área do gráfico de CPU quando você clica nele.
Detalhes que **não** podem regredir:

| Item | Valor | Por quê |
|---|---|---|
| Linhas do grid interno | `Auto` / **`*`** / `Auto` | o `*` precisa ser do **canvas**: antes ele ficava na linha de baixo (informação), o que criava um vão preto morto no meio e empurrava a lista para o rodapé |
| Canvas do histórico | `MinHeight="56"` (era `Height="58"` fixo) | as barras crescem junto com o painel (medido: 257 px numa janela de 1000 px, contra 58 px fixos) |
| `ClipToBounds="True"` no painel | obrigatório | em janela pequena o conteúdo (cabeçalho + barras + lista) é maior que a área do gráfico e vazaria sobre o cartão MEMÓRIA |
| Número grande ao vivo (`TxtSumCpuBig`/`TxtSumCpuSub`) | `Collapsed` enquanto o painel está aberto | o painel para 40 px antes da borda direita (para não cobrir o eixo de temperatura), e era por essa fresta que aparecia um **pedaço cortado** do "5%" e do "kernel 7%" |
| Lista de quem usava a CPU | moldura própria + cabeçalho `PROCESSO`/`CPU`, `FontSize 11`, coluna de % fixa em 40 px | era o menor texto da tela (10 px), solto no canto sem cabeçalho, com o % alinhado por acaso |

> Ferramenta: `TmVisualBench sort` abre o painel, salva `out/tm_SummaryHistory.png` e imprime
> `painel : LxA px · canvas N px` e `lista : FontSize N · N linhas` — dá para provar a mudança sem olhar.

## 7. Regras para manter o visual

1. Cor nova só via token (§2). Se realmente precisar de um hex solto, **documente aqui**.
2. Controle novo dentro do TM **não precisa de estilo**: os implícitos já cobrem Button,
   CheckBox, ComboBox, Separator, ScrollBar e DataGrid*. Só escreva estilo se quiser algo diferente.
3. **Não** remover os estilos implícitos do `Window.Resources` — sem eles o tema dourado volta.
4. Todo cartão: `#141C28` + borda `#1F2A3A` + raio 8. Poço de dados: `#0D131A`.
5. Título de seção: `Style="{StaticResource TmSectionHeader}"`.
6. Selo: `Style="{StaticResource TmBadge}"`.
7. Tabela: `TmColumnHeaderStyle` + `TmRowStyle` + `TmCellStyle` (ou `Style="{StaticResource TmGrid}"`).
8. `Foreground` de botão de barra funciona — use `TmToolButton` e passe o `Foreground` que quiser.
9. Texto longo em célula: a célula precisa de `ClipToBounds` (já vem no `TmCellStyle`).

---

## 8. Como validar

`tests/TmVisualBench` (rodar **sem** `--no-build`):

```
dotnet run --project tests/TmVisualBench -- overlap   # 10 abas, nenhum texto sobreposto
dotnet run --project tests/TmVisualBench -- flicker   # % da tela repintada (Resumo 3,1 %, Storage 12,1 %, Diagnostic 25,0 %)
dotnet run --project tests/TmVisualBench -- sort      # ordenação, detalhe, submenu, fixar, histórico
dotnet run --project tests/TmVisualBench -- gold      # procura #FFD700 renderizado (deve dar 0 em 10/10)
dotnet run --project tests/TmVisualBench -- load      # 1ª carga: véu, 1ª lista, 1º valor do Resumo + prints
```

`gold` é o detector de **vazamento do tema global**: percorre a árvore visual, acha todo
brush dourado visível e imprime tipo do elemento + texto + bounds. Use sempre que mexer em
estilos: qualquer linha "• TextBlock.Foreground #FFD700 ..." é um vazamento.

---

## 9. Véu de carregamento (1ª carga)

`LoadOverlay` (rodapé do `KitTaskManagerWindow.xaml`) usa **apenas tokens existentes** —
nenhum hex novo entrou no TM:

| Elemento | Token / cor |
|---|---|
| Fundo translúcido | `#8C070B11` (véu sobre `TmBgDeep`) |
| Cartão | `#141C28` (`TmCard`) + borda `#243040` (`TmBorder`), raio 10 |
| Spinner (elipse tracejada) | `#4FC3F7` (`TmAccent`), `StrokeDashArray="3 5"` |
| Título / etapa / tempo | `White` / `#8FA6BF` / `#5B6B7C` |
| Barrinhas de etapa | acesa `#4FC3F7` (`TmAccent`), apagada `#243040` (`TmBorder`) |

Regras de estilo do véu: **sem dourado**, `IsHitTestVisible=False` (não bloqueia clique) e
teto de 15 s. O modo `load` do bench mede a linha do tempo e checa sobreposição de texto
dentro do cartão em 1680x1000 e 1180x740. Detalhes e números: `docs/TM_FIRST_LOAD.md`.

---

## 10. Histórico

- **27/09/2026** — mapa criado; paleta/tokens + estilos base no `Window.Resources`;
  neutralização dos implícitos dourados (Button/CheckBox/Separator/DataGrid*);
  `TmGridRow`; normalização de 10 grupos de cores; `_brushYellow` → `#FFD54F`;
  modo `gold` no TmVisualBench. Build 0 erros, 10/10 abas sem sobreposição e **0 dourado**.

- **27/09/2026 (cont.)** — overlay do histórico do Resumo: linha do canvas passou a ser `*` (as barras
  saíram de 58 px fixos para 257 px medidos), vão preto morto eliminado, `ClipToBounds` para não vazar
  em janela pequena, número grande ao vivo escondido enquanto o painel está aberto (acabou o "5%"
  cortado na fresta do eixo de temperatura) e a lista de processos virou tabela com moldura,
  cabeçalho e `FontSize 11`.

- **28/09/2026** — **véu de carregamento da 1ª carga** (`LoadOverlay` + `KitTaskManager.Loading.cs`)
  e **coletores auxiliares fora do caminho crítico** (GPU/TCP/usuários deixaram de segurar a
  primeira pintura; ver `docs/TM_FIRST_LOAD.md`). Medido no bench novo: véu em `t+28 ms`, lista em
  `t+435 ms`, Resumo com valor em `t+1007 ms`, véu sai em `t+1235 ms` — sem texto sobreposto no
  cartão e sem dourado, em 1680x1000 e 1180x740.

- **28/09/2026 (cont.)** — o modo `sort` passou a contar **empates até 0,1** como empate (e não
  como "ordem errada"): a ordenação do TM arredonda em 1 casa por anti-pisca, então 10 dos 128
  pares vinham sendo reportados como falha por diferença de **0,1 MB**. Agora a checagem separa
  "empate proposital" de "linha realmente fora de lugar" (Δ mínimo reportado: 0,1; não há mais
  nenhuma violação real). Resultado: `sort` → **TUDO OK** (9 critérios de ordenação, detalhe ao
  vivo, submenu, fixar, histórico).
