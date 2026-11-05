# Barras de pesquisa do Kit + nova Página de Pesquisa Global

**Data:** 05/11/2026
**Escopo:** `KitLugia.GUI` (XAML/tema + code-behind). Nenhuma mudança de motor, de ação ou de regra do Kit.
**Arquivos-chave:** `KitLugia.GUI/Themes/BaseStyles.xaml`, `KitLugia.GUI/Pages/GlobalSearchPage.xaml(.cs)`, `KitLugia.GUI/Pages/NetworkPage.xaml(.cs)`.

---

## 1. Resumo do que mudou

| Frente | Antes | Depois |
|---|---|---|
| Barras de pesquisa | 22 campos, cada um com markup próprio (Border + lupa em emoji + placeholder em `TextBlock` sobreposto, ou `TextBox` pelado com `FocusStyle`/`DarkTextBoxStyle`) | Um padrão único no tema: `SearchTextBoxStyle` / `SearchInputStyle` + placeholder por `Tag` |
| Animações nas buscas | Herdavam o estilo implícito de `TextBox` (`DarkTextBoxStyle`), que anima borda e fundo por 0,2 s no hover/foco | Sem `Storyboard`: hover e foco trocam cor por `Setter` (instantâneo) |
| Placeholders | Texto falso dentro do campo (Privacidade), `ToolTip` (Integridade), itálico sobreposto (Serviços, Drivers, Reparos, Contexto) ou nenhum (Bloatware, Programas, Apps) | Todos com placeholder real via `Tag`, alinhado com o caret |
| Página de Pesquisa Global | Só lista de resultados, cartões com `CardStyle` (scale + sombra animados por item) | Página completa: barra própria, filtros por tipo com contagem, cartões leves, estado vazio contextual |
| MAC Spoofing (Rede) | Card "Fabricantes (OUI real)" com filtro, combo e botão "Vendor aleatório" | Bloco removido (UI **e** código morto); o card MAC termina em "Auto-detect MAC" |

---

## 2. O padrão de barra de pesquisa

### 2.1 Onde mora

`KitLugia.GUI/Themes/BaseStyles.xaml` (carregado por `Themes/Generic.xaml` → `App.xaml`). Quatro chaves:

| Chave | Tipo | Para que serve |
|---|---|---|
| `SearchBarBorderStyle` | `Border` | Contêiner para casos com botão de limpar (o campo dentro usa `SearchInputStyle`) |
| `SearchTextBoxBaseStyle` | `TextBox` | Template base: lupa + `PART_ContentHost` + placeholder |
| `SearchTextBoxStyle` | `TextBox` | Barra completa (desenha a própria borda). Usar na maioria dos casos |
| `SearchInputStyle` | `TextBox` | Mesma barra **sem** borda/fundo, para viver dentro de um `Border` da página |

### 2.2 Valores exatos

```
SearchBarBorderStyle ......... Background #1A1A1A · BorderBrush #333 · BorderThickness 1
                              CornerRadius 6 · Height 34
SearchTextBoxBaseStyle ....... Background #1A1A1A · BorderBrush #333 · BorderThickness 1
                              Foreground White · CaretBrush AccentColor (#FFD700)
                              FontSize 13 · Padding 0 · MinHeight 32
                              VerticalContentAlignment Center
SearchTextBoxStyle ........... BasedOn base · MinHeight 34
SearchInputStyle ............. BasedOn base · Background Transparent · BorderThickness 0
```

### 2.3 Anatomia do template

```
Border x:Name="bd"  (CornerRadius 6)
└── Grid
    ├── Coluna Auto : TextBlock "🔍" 12px, Foreground #7A7A7A, Margin 10,0,7,0
    └── Coluna *    : Grid (Margin 0,0,6,0)
                       ├── ScrollViewer x:Name="PART_ContentHost" (Focusable=false)
                       └── TextBlock x:Name="ph"
                             Text       = {TemplateBinding Tag}
                             Foreground = #666
                             FontSize   = {TemplateBinding FontSize}
                             Visibilidade controlada por trigger (Text == "")
                             TextTrimming = CharacterEllipsis
```

Triggers (nenhum é animação):

1. `Text == ""` → mostra o `ph` (placeholder).
2. `IsMouseOver` → `bd.BorderBrush = #555`.
3. `IsKeyboardFocusWithin` → `bd.BorderBrush = AccentColor` (declarado por último: com o mouse em cima **e** o campo focado, o dourado vence).

### 2.4 Por que `Padding = 0` e o placeholder na mesma célula

O recuo do texto vem da coluna da lupa, não de `Padding`. Isso resolve o desalinhamento clássico de placeholder (o `DnsPlaceholderTextBoxStyle` de `NetworkPage`/`MacSpoofPage` precisa de margem `Padding.Left + 2` justamente porque usa `Padding`). Aqui texto, caret e placeholder começam todos na mesma coordenada X.

> **Regra:** não sobrescreva `Padding` em `SearchTextBoxStyle`/`SearchInputStyle`. Para ajustar o respiro use `Margin` na página.

### 2.5 Uso

Barra completa:

```xml
<TextBox x:Name="TxtFilter"
         Style="{StaticResource SearchTextBoxStyle}"
         Tag="Filtrar drivers..."
         TextChanged="TxtFilter_TextChanged"/>
```

Dentro de um `Border` da página (com botão de limpar, pílula etc.):

```xml
<Border Style="{StaticResource SearchBarBorderStyle}" Padding="0">
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="Auto"/>
        </Grid.ColumnDefinitions>
        <TextBox x:Name="TxtSearch" Grid.Column="0"
                 Style="{StaticResource SearchInputStyle}"
                 Tag="Buscar tweak por nome ou categoria..."
                 TextChanged="TxtSearch_TextChanged"/>
        <Button x:Name="BtnClearSearch" Grid.Column="1" Content="✕"
                Background="Transparent" BorderThickness="0" Foreground="#666"
                Cursor="Hand" Visibility="Collapsed" Width="24" Height="24" Margin="0,0,4,0"
                Click="BtnClearSearch_Click"/>
    </Grid>
</Border>
```

Toolbars compactas (26–32 px) precisam de `Height`/`MinHeight` locais, porque a base tem `MinHeight 32`:

```xml
<TextBox Style="{StaticResource SearchTextBoxStyle}" Height="26" MinHeight="0" FontSize="11" .../>
```

Páginas com paleta própria (ex.: Gerenciador de Tarefas, azul `#1F2A3A`/`#2C3A4E`) mantêm a identidade sobrescrevendo as cores **depois** do `Style` (valor local vence `Setter`):

```xml
<TextBox Style="{StaticResource SearchTextBoxStyle}"
         Background="#1F2A3A" BorderBrush="#2C3A4E" Height="26" MinHeight="0" .../>
```

---

## 3. Onde o padrão foi aplicado (23 campos, 17 XAMLs)

| Arquivo | Controle | Detalhe |
|---|---|---|
| `MainWindow.xaml` | `TxtGlobalSearch` | Busca global da sidebar; saiu o `Border #20FFFFFF` + placeholder itálico |
| `Pages/AllTweaksPage.xaml` | `TxtSearch` + `BtnClearSearch` | `SearchBarBorderStyle` + `SearchInputStyle` |
| `Pages/IntegrityPage.xaml` | `SearchBox` + `BtnClearSearch` | manteve a pílula (`CornerRadius 18`) e o botão de limpar |
| `Pages/RepairsPage.xaml` | `TxtFilter` | `Height 45`, título grande da página |
| `Pages/BloatwarePage.xaml` | `TxtSearch` | saiu do `FocusStyle` (que animava borda) |
| `Pages/ProgramsPage.xaml` | `TxtSearch` | saiu o `TextBox` com cores fixas |
| `Pages/AppsPage.xaml` | `TxtSearchBloatware`, `TxtSearchPrograms`, `TxtSearchPortable` | as 3 abas |
| `Pages/ServicesPage.xaml` | `TxtSearchStartup`, `TxtSearchService`, `TxtDriverFilter` | inicialização, serviços e drivers |
| `Pages/DriversPage.xaml` | `TxtFilter`, `TxtKernelFilter` | drivers e drivers de kernel |
| `Pages/PrivacyPage.xaml` | `TxtSearch` | **sem** texto falso de placeholder (ver §3.2) |
| `Pages/WindowsSettings/ContextMenuPage.xaml` | `TxtSearch` | `Width 260` |
| `Pages/WindowsSettings/ContextMenuManagerPage.xaml` | `TxtSearch` | era `DarkTextBoxStyle` + `Tag` que nunca aparecia |
| `Pages/WindowsSettings/StoreRemakePage.xaml` | `TxtSearch` | mega query box (ver §3.3) |
| `Controls/ProcessPickerOverlay.xaml` | `TxtSearch` | seletor de processos |
| `Controls/GlobalConsole.xaml` | `TxtSearch` | `SearchInputStyle`, Consolas 11, `Foreground #AAA` (identidade do terminal) |
| `Windows/TaskManager/KitTaskManagerWindow.xaml` | `TxtSearch`, `TxtConnFilter`, `TxtStartupSearch` | azul do TM preservado |
| `Pages/GlobalSearchPage.xaml` | `TxtPageSearch` | barra nova da própria página de busca (§5) |

### 3.1 O que deixou de existir

- `TextBlock` de placeholder com `DataTrigger` sobre `Text` do campo (MainWindow, Serviços, Drivers, Reparos, Contexto, TM).
- Placeholder por `ToolTip` (Integridade) e placeholder em itálico sobreposto.
- Botão de limpar sobre o texto (agora fica em coluna própria, sem cobrir a digitação).

### 3.2 Privacidade: placeholder que era texto de verdade

`TxtSearch` nascia com `Text="🔍 Pesquisar configurações..."` e o code-behind trocava esse texto em `GotFocus`/`LostFocus`, além de filtrar por comparação de string mágica:

```csharp
// removido
if (TxtSearch.Text == "🔍 Pesquisar configurações...") { ... }
```

Agora o campo começa vazio (`Tag` faz o placeholder) e `TxtSearch_TextChanged` só faz `_searchText = TxtSearch.Text; ApplyFilter();`. Sem estado escondido, sem risco de o filtro "pescar" o placeholder.

### 3.3 Store Remake: caixa de comando

A caixa larga mantém os botões (`✕` e `Buscar`), mas perdeu o ícone MDL2 próprio (a lupa do estilo já aparece), o `TextBlock` de placeholder e a animação herdada. O `TxtSearchPlaceholder` saiu do XAML e do code-behind:

```csharp
private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
{
    if (BtnClearSearch == null) return;
    var hasText = !string.IsNullOrEmpty(TxtSearch.Text);
    BtnClearSearch.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
}
```

---

## 4. Animações: o que saiu e o que ficou

### Saiu das barras de pesquisa

O Kit tem um estilo implícito `TextBox` que herda de `DarkTextBoxStyle` (hover/foco = `ColorAnimation` de 0,2 s em `BorderBrush` e `Background`). Todo `TextBox` sem `Style` explícito — inclusive as barras de pesquisa — "piscava" a cada passada de mouse. Os dois estilos novos não têm `Storyboard`; a troca de cor é imediata. O `FocusStyle` também anima (0,2 s) e deixou de ser usado em busca (`BloatwarePage` migrou).

### Continua valendo para os demais controles (de propósito)

- `FocusStyle` / `DarkTextBoxStyle` / `DarkPasswordBoxStyle`: animação de borda/fundo nos **campos que não são de busca**.
- `CardStyle` / `SubtleCardStyle`: `ScaleTransform 1.015` + `DropShadowEffect` no hover dos cartões.
- Spinners de carregamento (`Win11SpinnerTemplate`, `Ellipse` rotativas) e barras de progresso.
- `ListBoxItem`/`ListViewItem` com `ColorAnimation` de 0,15 s na seleção/hover.

Motivo para não unificar agora: são centenas de pontos e o usuário só pediu as barras de pesquisa. O padrão novo está pronto para ser estendido quando fizer sentido.

---

## 5. Página de Pesquisa Global (refeita)

Arquivos: `KitLugia.GUI/Pages/GlobalSearchPage.xaml` e `.xaml.cs`.

### 5.1 Layout

```
[ Cabeçalho ]      "Resultados da Pesquisa"
                   "Encontrados: N itens  para  “termo”"
[ Busca ]          TxtPageSearch  (SearchTextBoxStyle, Height 38)
[ Filtros ]        chips: Tudo (N) · Páginas (n) · Tweaks (n) · Ações (n) · Serviços (n)
[ Resultados ]     ListBox virtualizado  +  painel de estado vazio
```

### 5.2 Comportamento

- **Barra da própria página**: `TextChanged` inicia um `DispatcherTimer` de 180 ms; o tick roda `UpdateSearch`. `Enter` dispara na hora. O índice em memória responde em <2 ms — o debounce existe para não rebindar a lista a cada tecla (perderia scroll e atrapalharia a digitação).
- **Sincronização com a sidebar**: `MainWindow.PerformLiveSearch` continua chamando `UpdateSearch(query)`. O método espelha o termo em `TxtPageSearch` com um guard (`_updatingBox`), então não há laço de `TextChanged`. A página **não** escreve de volta na sidebar.
- **Filtros por tipo**: chips são `RadioButton` com o mesmo `GroupName` (`SearchType`) e `Tag` (`all`/`nav`/`tweak`/`action`/`service`). `ChipFilter_Checked` chama `ApplyTypeFilter()`. Se o tipo escolhido não tiver itens para o novo termo e existirem resultados em outros tipos, a página **volta sozinha para "Tudo"** (com `_suppressChipEvent` para não reentrar no handler).
- **Contagens**: `UpdateChipCounts()` escreve `"Páginas (12)"` etc.; `TxtResultCount` mostra `"609 itens"` ou `"12 de 609 itens"` quando há filtro ativo.
- **Estado vazio contextual**: sem termo → "Digite algo para pesquisar."; com termo → "Nada encontrado para “x”." + sugestões (desempenho, rede, drivers, reparos...).
- **Clique no cartão**: abre **somente** itens `SearchResultType.Navigation`. Tweaks continuam exigindo o switch e ações o botão — evita disparar ação destrutiva por clique acidental. Duplo clique segue a mesma regra.
- **Estados dos toggles**: mantido o batch em background com cache TTL (`SearchEngine.GetStates()`), 1 passada por busca, nunca 1 scan `Guardian` por item.
- **API pública preservada**: `UpdateSearch(string)` e `Cleanup()` — `MainWindow` não precisou mudar.

### 5.3 Estilos locais da página

```xml
<!-- Chip: seleção só por cor, sem animação -->
SearchChipStyle ......... RadioButton; Border #10FFFFFF / #333, CornerRadius 12, Padding 12,5
                           Foreground #B0B0B0 → IsChecked: fundo #22FFD700, borda AccentColor, texto dourado
                           (trigger de hover vem ANTES do IsChecked para o dourado vencer)

<!-- Cartão de resultado: hover só troca cor -->
SearchResultCardStyle ... Background #10FFFFFF (hover #1AFFFFFF), Border #2E2E2E (hover AccentColor),
                           CornerRadius 8, Padding 12,10, Cursor Hand
```

**Por que não `CardStyle`**: ele aplica `ScaleTransform` + `DropShadowEffect` e anima ambos no hover. Numa lista de centenas de resultados (virtualizada), isso custa caro por item e é justamente o tipo de "animação desnecessária" que se quer evitar em lista.

### 5.4 Detalhes do template do cartão

- Ícone em tile 38×38 (`#15FFD700`), `Segoe UI Emoji`.
- Título `SemiBold 13` com `MaxWidth 420` + badge de tipo (`TypeLabel`: PÁGINA/TWEAK/AÇÃO/SERVIÇO).
- Descrição `11px #8A8A8A`, `MaxHeight 30`, `TextTrimming`, `ToolTip` com o texto completo.
- Direita: `Button` (`ButtonText`, `SecondaryButtonStyle`) **ou** `CheckBox` (`ToggleSwitchStyle`), alternados por `IsToggle` via `DataTrigger`. Inalterado em relação à versão anterior.

---

## 6. MAC Spoofing: remoção do bloco "Fabricantes (OUI real)"

Pedido: remover **apenas** esse bloco da página de Rede.

### XAML (`NetworkPage.xaml`)
Removido o `Border` `#151520` que continha título, texto explicativo, `TxtOuiFilter`, `BtnOuiRandom`, `CmbOuiVendors` e `TxtOuiInfo`. O card de MAC Spoofing agora termina no botão "Auto-detect MAC". O arquivo voltou ao estado do HEAD (diff zero).

### Code-behind (`NetworkPage.xaml.cs`)
Removidos, por serem exclusivos do bloco:

- campo `_ouiListLoading`;
- `TxtOuiFilter_TextChanged`, `LoadOuiVendors`, `CmbOuiVendors_SelectionChanged`, `ApplySelectedVendor`, `BtnOuiRandom_Click`;
- a chamada `LoadOuiVendors(null)` em `NetworkPage_Loaded`;
- a escrita em `TxtOuiInfo` dentro de `BtnGenerateMac_Click`.

### O que continua
- "Gerar Novo MAC" usa MAC genérico (`02` + 5 bytes) via `AdapterManager.GenerateRandomMacPlain()` — comportamento mais compatível.
- `KitLugia.Core/OuiCatalog.cs` e `AdapterManager.GenerateMacFromOui(...)` **permanecem** (usados pelo Core e pelo harness `tests/ouicatalog`); apenas deixaram de ter entrada na UI.

---

## 7. Armadilhas (leia antes de mexer)

1. **`Tag` é o placeholder.** Um `TextBox` com `SearchTextBoxStyle` e sem `Tag` fica sem dica visual (só a lupa).
2. **Não use `Padding`** para dar respiro: a base usa `Padding 0` e o recuo vem da coluna da lupa; mexer desalinha o placeholder do caret.
3. **`MinHeight`**: a base é 32 e `SearchTextBoxStyle` sobe para 34. Em barras de 20–30 px (console, conexões, inicialização) passe `Height` **e** `MinHeight="0"` locais.
4. **Cores de página**: `Style` define fundo/borda; para uma paleta diferente sobrescreva `Background`/`BorderBrush` no próprio elemento (local vence `Setter`).
5. **Ambiguidade WinForms no code-behind**: o projeto tem usings globais que trazem `System.Windows.Forms`. Handlers novos precisam qualificar tipos: `System.Windows.Controls.RadioButton`, `System.Windows.Input.KeyEventArgs` (erro CS0104).
6. **`PART_ContentHost` continua obrigatório** e precisa de `Focusable="false"`; ele está aninhado (coluna do texto), o que é válido para `TextBox`.
7. **Placeholder de `Text` (texto real no campo)** nunca mais: quebra filtro por igualdade de string e o `Tab`/`Ctrl+A`.

---

## 8. Verificação feita

- `dotnet build KitLugia.sln` → **0 erros** (142 avisos nullable pré-existentes, nenhum nos arquivos tocados).
- `git diff --check` → limpo (sem whitespace/CR no fim de linha).
- Encoding/BOM: XAMLs continuam em UTF-8 com CRLF (BOM preservado onde já existia).
- `grep` de fechamento: 24 referências a `SearchTextBoxStyle`/`SearchInputStyle` em 17 XAMLs (23 campos de busca + a barra da própria página); nenhum `TextBlock` de placeholder antigo restante; `TxtSearchPlaceholder` não existe mais no projeto.

**Limitação:** não houve inspeção visual (o ambiente não renderiza WPF). O que foi provado é compilação, estrutura e consistência de referências. Vale abrir Rede (card sem o bloco OUI), Busca Global (chips/cartões), Integridade (pílula), Serviços/Drivers (toolbars) e o Gerenciador de Tarefas (caixas azuis) para conferir espaçamentos.

---

## 9. Checklist para uma tela nova

- [ ] Importar? Não — os estilos vêm de `Themes/Generic.xaml` (app).
- [ ] Barra simples → `SearchTextBoxStyle` + `Tag="O que se busca..."` + `TextChanged`.
- [ ] Com botão de limpar → `SearchBarBorderStyle` (`Padding="0"`) + `SearchInputStyle` em coluna própria + botão na coluna `Auto`.
- [ ] Toolbar compacta → `Height`/`MinHeight="0"` locais e `FontSize` menor.
- [ ] Paleta específica → sobrescrever `Background`/`BorderBrush` após o `Style`.
- [ ] Não adicionar `Storyboard`/`ColorAnimation` na barra; foco é indicado por cor.
- [ ] Debounce ao filtrar listas grandes (150–250 ms) em vez de rebindar por tecla.
