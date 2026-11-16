# KitTaskManager — Mapa de Estilos e Cores

> Documento de referência do visual do **Gerenciador de Tarefas próprio do Kit**.
> Tudo dele vive em `KitLugia.GUI/Windows/TaskManager/KitTaskManagerWindow.xaml`
> (`Window.Resources`) + os partials `KitTaskManager.<Aba>.cs` que pintam Canvas/barras.
>
> **Skin canônica (16/11/2026):** o TM agora veste o visual do **Task Manager TMOG**
> (skin "Windows Task Manager" do Dave Plummer): cinza 3D clássico, barra de título
> navy, caixas com borda cinza, seleção navy e texto preto. A paleta abaixo é a fonte
> de verdade — cor fora dela é divergência.
>
> Regra: **cor fora desta paleta é divergência**. Se precisar de uma cor nova, adicione
> o token aqui e justifique — não invente hex solto no meio de uma aba.

---

## 1. O problema que este mapa resolve

O TM é uma janela **clássica** dentro de um app cujo tema global é **dourado**
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

Definidos como `SolidColorBrush` no topo do `Window.Resources`. Valores extraídos do
skin `Windows Task Manager.tmogskin` do TMOG (`skin.json`).

### Superfícies (do mais fundo ao mais claro)

| Token | Hex | Onde usar |
|---|---|---|
| `TmBgDeep` | `#000000` | poços/gráficos, console do diário, fundo de barra de progresso |
| `TmBgWindow` | `#D4D0C8` | fundo da janela, sidebar e das páginas |
| `TmBgCard` | `#ECE9D8` | cartões, popups, menus, zebra das tabelas |
| `TmBgHeader` | `#D4D0C8` | cabeçalho de `DataGrid` |
| `TmBgControl` | `#FFFFFF` | combo, input, botão de barra |
| `TmBgHover` | `#E3E3E3` | hover de linha de tabela / item de menu |
| `TmBgNavHover` | `#ECE9D8` | hover do botão da sidebar |
| `TmBgNavPress` | `#D4D0C8` | pressionado da sidebar |

### Barra de título (drag bar)

| Elemento | Cor |
|---|---|
| Fundo | `#000080` (navy do TMOG) |
| Título / ícones de janela | `White` |
| Caixa de busca | `#FFFFFF` + borda `#808080`, texto `Black` |
| Hover/press dos botões | `#33FFFFFF` / `#55FFFFFF` (branco translúcido) |
| Fechar (hover/press) | `#800000` / `#C00000` |

### Bordas

| Token | Hex |
|---|---|
| `TmBorder` | `#808080` |
| `TmBorderStrong` | `#404040` |

### Acento e estado

| Token | Hex | Papel |
|---|---|---|
| `TmAccent` | `#000080` | navy clássico: títulos de seção, ícones, foco, seleção de texto |
| `TmAccentDeep` | `#000040` | variante escura do navy (hover de selecionado) |
| `TmSelect` | `#000080` | navy de acento (reservado; a seleção de LINHA é clara — ver §3) |
| `#FFFFFF` / `#000080` | (sem token) | **linha selecionada**: fundo branco + borda navy (o texto das células é preto e precisa continuar legível) |
| `#D4D0C8` / `#808080` | (sem token) | grupo expandido (pai) na aba Processos |
| `#FFFFFF` / `#808080` | (sem token) | linha filha (indentada) na aba Processos |
| `#E3E3E3` | (sem token) | hover de filho / hover de dispositivo |

### Texto

| Token | Hex | Papel |
|---|---|---|
| `TmText` | `#000000` | corpo |
| `TmTextDim` | `#404040` | secundário |
| `TmTextMuted` | `#606060` | terciário / cabeçalho de coluna |
| `TmTextFaint` | `#808080` | legendas |

### Semânticas

| Token | Hex | Papel |
|---|---|---|
| `TmGood` | `#008000` | saudável / CPU |
| `TmWarn` | `#B8860B` | atenção / disco / temperatura |
| `TmBad` | `#C00000` | crítico / kernel / energia |
| — | `#E6A800` | **âmbar de atenção** (escala de calor) |
| `TmRam` | `#800080` | memória |
| `TmNet` | `#800080` | rede |
| — | `#800080` | relógio / GPU (canais compartilhados do clássico) |
| — | `#00FF00` | texto monoespaçado do console (diário) |
| — | `#C00000` / `#800000` | perigo (hover e press de ações destrutivas) |
| — | `#F8D7DA` / `#FFF3CD` / `#D1ECF1` | cartões de crítico/atenção/info (Diagnóstico) |
| — | `#004000` / `#008000` | botão "GERAR DIAGNÓSTICO" |

### Escala de calor (`GetHeatColor`)

`KitTaskManagerWindow.xaml.cs` — 4 degraus, conforme o valor vs. os limiares:

```
>= critical  -> _brushRed     #C00000
>= warn      -> _brushOrange  #FF8000
>= warn*0,6  -> _brushYellow  #E6A800
senão        -> _brushGreen   #008000
```

---

## 3. Estilos nomeados (`x:Key`)

| Chave | Tipo | Para que serve |
|---|---|---|
| `TmToolButton` | Button | botão de barra de ferramentas (Serviços, Inicialização, Latência, Armazenamento, Diagnóstico) |
| `TmDarkMenu` | ContextMenu | menu de contexto clássico (fundo `#ECE9D8`, borda `#808080`) |
| `TmMenuItem` | MenuItem | item de menu, com **Popup** de submenu (a seta ▸ só aparece com filhos) |
| `TmColumnHeaderStyle` | DataGridColumnHeader | cabeçalho padrão (`#D4D0C8` / `#606060` / `Semibold` / `ClipToBounds`) |
| `TmRowStyle` | DataGridRow | linha padrão (hover `#E3E3E3`, seleção `#FFFFFF` + borda `#000080`) |
| `TmCellStyle` | DataGridCell | célula padrão (`ClipToBounds` — sem isso o texto vazava para a coluna vizinha) |
| `TmGridRow` | DataGridRow | linha das tabelas que usam `TmGrid` (seleção **clara** `#FFFFFF`, não a lavagem dourada do tema) |
| `TmGrid` | DataGrid | tabela densa de diagnóstico (Latência / Armazenamento / Diagnóstico) |
| `TmComboBox` / `TmComboBoxItem` | ComboBox | combo clássico (`#ECE9D8`; item selecionado navy/branco) |
| `TmScrollBar` / `TmScrollBarThumb` | ScrollBar/Thumb | barra de rolagem clássica (thumb `#D4D0C8`, hover navy) |
| `TmCard` | Border | cartão padrão (fundo `#ECE9D8`, borda `#808080`, raio 8, padding 12) |
| `TmSectionHeader` | TextBlock | título de seção (`11` / Bold / `#000080`) |
| `TmBadge` | Border | selo de estado (`#ECE9D8` / raio 4 / `7,2`) |
| `UserRowStyle` | DataGridRow | linha da aba Usuários (carrega `DetailsVisibility` do item) |
| `InfoTipWrap` | ToolTip | tooltip que quebra linha (textos longos da Latência) |

Estilos **implícitos do TM** (sem `x:Key`, valem para a janela inteira):

```xml
<Style TargetType="{x:Type Button}"/>                  <!-- hover/press claros, SEM dourado e SEM zoom -->
<Style TargetType="{x:Type CheckBox}"/>                <!-- 15x15 clássico -->
<Style TargetType="{x:Type Separator}"/>               <!-- #808080 -->
<Style TargetType="{x:Type ComboBox}" BasedOn="{StaticResource TmComboBox}"/>
<Style TargetType="{x:Type ScrollBar}" BasedOn="{StaticResource TmScrollBar}"/>
<Style TargetType="{x:Type DataGridColumnHeader}" BasedOn="{StaticResource TmColumnHeaderStyle}"/>
<Style TargetType="{x:Type DataGridCell}" BasedOn="{StaticResource TmCellStyle}"/>
<Style TargetType="{x:Type DataGridRow}" BasedOn="{StaticResource TmRowStyle}"/>
```

> **Por que os DataGrid implícitos são obrigatórios:** o `TmGrid` declara cabeçalho/célula
> por **Setter de estilo**. Nas abas Latência/Armazenamento/Diagnóstico isso não bastava e o
> estilo **global** de `DataGridColumnHeader` (`#1A1A1A` + texto dourado + 40 px) vencia.
> Com os implícitos no `Window.Resources`, o `TmGrid` passou a render `#D4D0C8`/`#606060`.
> Provado pelo `TmVisualBench gold`: **10/10 abas com 0 elementos dourados**.

---

## 4. Padrões por aba (o que já é consistente)

| Elemento | Valor |
|---|---|
| Barra de título | 40 px, `#000080`, borda inferior `#000000` |
| Sidebar | 58 px, botão 60×52, ícone **17 px**, rótulo 8 px; ativo = fundo `#000080` + texto branco |
| Título de aba | `18` / SemiBold / `#000000` |
| Contagem ao lado do título | `12` / `#606060` |
| Cartão | fundo `#ECE9D8`, borda `#808080`, **raio 8**, padding `12` (ou `12,10` / `10,8` / `14,12`) |
| Poço de gráfico/console | `#000000` |
| Zebra de tabela | `#D4D0C8` (fundo) alternando `#ECE9D8` |
| Cabeçalho de coluna | `#D4D0C8` / `#606060` / `11` SemiBold / padding `8,5` |
| Selo de estado | `#ECE9D8` / raio 4 / `7,2` |
| Tooltip | `#D4D0C8` com borda `#000080` |

---

## 5. Divergências normalizadas nesta sessão

| Antes | Depois | Por quê |
|---|---|---|
| `#0E141B`, `#0B1018`, `#0B1119`, `#121212` (4 tons de "fundo de gráfico") | `#0D131A` → `#000000` | o gráfico de RAM usava um tom fora da rampa do TM |
| `#161F2C` (cartões da Latência/Armazenamento/Diagnóstico) | `#141C28` → `#ECE9D8` | dois tons de cartão diferentes entre abas |
| `#101822`, `#2A3A4E` | `#141C28`, `#2C3A4E` → `#ECE9D8`, `#404040` | borda de painel fora do par canônico |
| `#26262B`, `#26262C` (hover cinza) | `#243040` → `#E3E3E3` | hover sem o cinza do clássico |
| `#33445A` (seleção de célula) | `#17324F` → `#000080` | tom de seleção fora do navy |
| `BorderBrush="#1F2A3A" ... CornerRadius="6"` (13 cartões) | `CornerRadius="8"` | Latência/Armazenamento/Diagnóstico com raio diferente do Resumo |
| Cabeçalho do `TmGrid`: `#141C28`/`#999`/`8,6`/`#333` | `#1A2432`/`#888`/`8,5`/`#1F2A3A` + `ClipToBounds` | alinhar ao `TmColumnHeaderStyle` |
| `_brushYellow = #FFD700` | `#FFD54F` → `#E6A800` | dourado do Kit dentro da escala de calor |
| Ícones da sidebar 14/15/16/17/18 px | 17 px em todos | 5 tamanhos para 10 botões |
| Títulos de seção `10` e `12` no mesmo conjunto | 11 px | Resumo usava 10 nas fichas inferiores |
| `TmToolButton` forçava `TextBlock.Foreground="White"` no `ContentPresenter` | removido; `Foreground` entrou no estilo | o `Foreground` local era **ignorado** (ex.: `services.msc`, `resmon`) |

---

## 6. Overlay "Click for History" (Resumo)

O painel `PnlCpuHistory` cobre a área do gráfico de CPU quando você clica nele.
Detalhes que **não** podem regredir:

| Item | Valor | Por quê |
|---|---|---|
| Linhas do grid interno | `Auto` / **`*`** / `Auto` | o `*` precisa ser do **canvas**: antes ele ficava na linha de baixo (informação), o que criava um vão morto no meio e empurrava a lista para o rodapé |
| Canvas do histórico | `MinHeight="56"` | as barras crescem junto com o painel |
| `ClipToBounds="True"` no painel | obrigatório | em janela pequena o conteúdo é maior que a área do gráfico e vazaria sobre o cartão MEMÓRIA |
| Número grande ao vivo (`TxtSumCpuBig`/`TxtSumCpuSub`) | `Collapsed` enquanto o painel está aberto | o painel para 40 px antes da borda direita e era por essa fresta que aparecia um **pedaço cortado** do "5%" |
| Lista de quem usava a CPU | moldura própria + cabeçalho `PROCESSO`/`CPU`, `FontSize 11`, coluna de % fixa em 40 px | era o menor texto da tela (10 px), solto no canto sem cabeçalho |

> Ferramenta: `TmVisualBench sort` abre o painel, salva `out/tm_SummaryHistory.png` e imprime
> `painel : LxA px · canvas N px` e `lista : FontSize N · N linhas` — dá para provar a mudança sem olhar.

---

## 7. Regras para manter o visual

1. Cor nova só via token (§2). Se realmente precisar de um hex solto, **documente aqui**.
2. Controle novo dentro do TM **não precisa de estilo**: os implícitos já cobrem Button,
   CheckBox, ComboBox, Separator, ScrollBar e DataGrid*. Só escreva estilo se quiser algo diferente.
3. **Não** remover os estilos implícitos do `Window.Resources` — sem eles o tema dourado volta.
4. Todo cartão: `#ECE9D8` + borda `#808080` + raio 8. Poço de dados: `#000000`.
5. Título de seção: `Style="{StaticResource TmSectionHeader}"`.
6. Selo: `Style="{StaticResource TmBadge}"`.
7. Tabela: `TmColumnHeaderStyle` + `TmRowStyle` + `TmCellStyle` (ou `Style="{StaticResource TmGrid}"`).
8. `Foreground` de botão de barra funciona — use `TmToolButton` e passe o `Foreground` que quiser.
9. Texto longo em célula: a célula precisa de `ClipToBounds` (já vem no `TmCellStyle`).
10. Seleção de linha é **clara** (`#FFFFFF` + borda `#000080`): o texto das células é preto
    e precisa continuar legível. Navy `#000080` só onde o texto é explicitamente branco
    (sidebar ativa, item de combo selecionado, drag bar).

---

## 8. Como validar

`tests/TmVisualBench` (rodar **sem** `--no-build`):

```
dotnet run --project tests/TmVisualBench -- overlap   # 10 abas, nenhum texto sobreposto
dotnet run --project tests/TmVisualBench -- flicker   # % da tela repintada
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
| Fundo translúcido | `#80000000` (véu escuro sobre `TmBgWindow`) |
| Cartão | `#ECE9D8` (`TmCard`) + borda `#808080` (`TmBorder`), raio 10 |
| Spinner (elipse tracejada) | `#000080` (`TmAccent`), `StrokeDashArray="3 5"` |
| Título / etapa / tempo | `#000000` / `#606060` / `#808080` |
| Barrinhas de etapa | acesa `#000080` (`TmAccent`), apagada `#808080` (`TmBorder`) |

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
  saíram de 58 px fixos para 257 px medidos), vão morto eliminado, `ClipToBounds` para não vazar
  em janela pequena, número grande ao vivo escondido enquanto o painel está aberto e a lista de
  processos virou tabela com moldura, cabeçalho e `FontSize 11`.

- **28/09/2026** — **véu de carregamento da 1ª carga** (`LoadOverlay` + `KitTaskManager.Loading.cs`)
  e **coletores auxiliares fora do caminho crítico** (GPU/TCP/usuários deixaram de segurar a
  primeira pintura; ver `docs/TM_FIRST_LOAD.md`).

- **28/09/2026 (cont.)** — o modo `sort` passou a contar **empates até 0,1** como empate (e não
  como "ordem errada"): a ordenação do TM arredonda em 1 casa por anti-pisca.

- **16/11/2026** — **skin clássico TMOG** (referência: `Task Manager TMOG`, skin "Windows Task
  Manager"). Paleta azul/ciano `#111823/#4FC3F7` substituída pela rampa clássica:
  janela `#D4D0C8`, cartões `#ECE9D8`, controles `#FFFFFF`, hover `#E3E3E3`, bordas `#808080`,
  acento/seleção `#000080`, texto `#000000/#404040/#606060/#808080`, semânticas
  `#008000/#B8860B/#C00000/#800080`, poços `#000000`. Barra de título virou navy `#000080`
  com título/botões brancos e busca branca. Código (`GetHeatColor`, brushes de sidebar,
  séries dos gráficos, badges de Latência/Armazenamento/Diagnóstico) alinhado à mesma rampa.
  Build 0 erros; `TmVisualBench overlap` e `gold`: **10/10 abas OK, 0 dourado**.
