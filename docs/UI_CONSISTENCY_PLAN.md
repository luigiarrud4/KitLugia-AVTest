# Plano de padronização visual do KitLugia

> Documento de trabalho. Cada fase termina com build limpo e uma lista de páginas
> migradas; o checklist no fim é a fonte de verdade do andamento.

## Estado base

| Item | Valor |
|---|---|
| Branch | `impeccable-polish` |
| Commit de referência | `f5c3961 feat(ui): standardize search fields and rebuild global search page` |
| Tag do kit estável | `estavel-pre-ui-padronizacao` |
| Escopo | `KitLugia.GUI` (XAML + code-behind de UI). Nenhuma mudança de comportamento dos motores. |

Reverter uma fase = `git diff estavel-pre-ui-padronizacao -- KitLugia.GUI` / `git checkout <tag> -- <arquivo>`.

---

## 1. Diagnóstico medido

Levantamento por leitura de markup (46 páginas em `Pages/`, mais `Windows/`, `Controls/`
e `MainWindow.xaml`; 72 XAMLs no total), com contagem por `grep`.

| Sintoma | Número | Evidência |
|---|---|---|
| Cores hardcoded em vez de tema | **3.211** hex em XAML vs **178** `DynamicResource` | `NetworkPage` usa `#F48120` local em vez de `AccentColor` |
| Escala tipográfica inexistente | 12 tamanhos (9 a 32) | títulos em **32** (29×), **28** (21×) e **24** (24×) |
| Tons de subtítulo | 6 cinzas | `#888` 150×, `#AAA` 93×, `#A0A0A0` 73×, `#666` 68×, `#999` 15×, `#777` 14× |
| Raios de canto | 11 valores | 2, 3, 4, 5, 6, 8, 10, 12, 15, 18, 20 |
| Templates de botão duplicados | ~400 cópias inline | `ExmTweaksPage` 126, `TweaksPage` 96, TM 50, `NetworkPage` 32, `MainWindow` 28 |
| Tema de página morto | `Themes/PageStyles.xaml` com `PageTitleStyle`, `SectionHeaderStyle`, `PageStackStyle`… e **0 usos** | `grep StaticResource Page*Style` = vazio |
| Efeitos caros | **27** XAMLs com `DropShadowEffect`, **24** com `Storyboard` | `CardStyle` anima scale 1.015 + blur **por item** |
| Loading/estado vazio | 7 páginas com overlay, 2 implementações de spinner | `Win11SpinnerTemplate` vs `Ellipse` + storyboard inline |
| Margem de página | `30` em 10 páginas, `20`/`24` em outras | `PageStackStyle` (40,30,40,50) nunca usado |

Armadilha encontrada: `CardContainerStyle` (PageStyles) declara
`<Setter Property="Style" Value="{DynamicResource CardStyle}"/>` — setter de `Style`
dentro de `Style` não é aplicável. Precisa sair **antes** de espalhar o uso do arquivo.

---

## 2. Padrão proposto (contrato visual)

### Tipografia — 6 degraus, nada fora da escala

| Token | Tamanho | Uso |
|---|---|---|
| `FontSizeDisplay` | 28 | título de página |
| `FontSizeTitle` | 18 | título de bloco destacado |
| `FontSizeSection` | 16 | cabeçalho de seção |
| `FontSizeBody` | 13 | texto corrido, valores, subtítulo de página |
| `FontSizeSmall` | 12 | texto auxiliar |
| `FontSizeCaption` | 11 | badges, legendas, rodapés |

Sem 9/10 para conteúdo informativo; sem 14/15/16 misturados no mesmo nível.

### Cores — todos `DynamicResource`

| Token | Valor | Uso |
|---|---|---|
| `TextPrimary` | `#FFFFFF` | títulos e valores |
| `TextSecondary` | `#E0E0E0` | texto de apoio forte |
| `TextMuted` | `#8A8A8A` | subtítulo, rótulo, placeholder |
| `TextDescription` | `#A0A0A0` | descrição longa |
| `Surface0` | `#0F0F0F` | fundo de página |
| `Surface1` | `#1A1A1A` | card |
| `Surface2` | `#252525` | card em destaque, barra |
| `BorderSubtle` | `#333333` | divisor e borda de card neutro |
| `BorderStrong` | `#555555` | borda de campo e de foco |
| `AccentColor` | `#FFD700` | destaque, foco, CTA |
| `SuccessBrush` / `ErrorBrush` / `WarningBrush` | — | semânticos |

### Forma e espaço

- Raio: **8** card/superfície, **6** campo, **4** badge, **12** chip.
- Espaçamento em múltiplos de 4: 4/8/12/16/20/24/32.
- Altura de controle: **28** compacta, **32** padrão, **40** CTA.
- Margem de página: **32** (com `MaxWidth` opcional por tipo de tela).

### Movimento

- Só micro-interação de **120–180 ms** em card clicável isolado e botão.
- **Zero** animação em campo de entrada.
- **Zero** animação em item de lista virtualizada (hover = cor).

### Exceções intencionais (documentar, não "corrigir")

- Gerenciador de Tarefas: paleta azul (`#1F2A3A`, `#2C3A4E`).
- Console/terminal: monoespaçada com `#AAA`.
- Store Remake: barra de comando própria.

---

## 3. Fases

### F0 — Tokens e guarda-corpo
`Themes/Typography.xaml` (escala como `sys:Double` + estilos de texto base), tokens de
superfície/borda em `Brushes.xaml`, correção do `PageStyles.xaml`
(setter inválido fora, `PageTitleStyle` em 28). Nenhuma página tocada.

Risco: baixo. Verificação: build 0 erros.

### F1 — Cabeçalho de página padrão
`Controls/PageHeader.xaml` (ícone opcional, título, subtítulo, contagem, slot de ações)
e migração das 46 páginas em 3 lotes, começando pelas 10 mais usadas.
Risco: baixo/médio. Verificação: build + captura antes/depois.

### F2 — Botões e controles
`PrimaryBtn`, `SecondaryBtn`, `DangerBtn`, `GhostBtn`, `IconBtn`, `ChipBtn`, `BadgeStyle`
com hover por cor. Substituir os templates inline nos 5 arquivos que concentram 90% das cópias.
Risco: médio (muito markup). Verificação: build por arquivo + revisão de captura.

### F3 — Superfícies e listas
Três níveis (`Card`, `SubtleCard`, `SurfacePanel`), raio 8 único; `CardStyle` animado só
em card clicável isolado; remover `DropShadowEffect` de listas longas.
Risco: médio. Verificação: contagem de `DropShadowEffect`/`Storyboard` caindo.

### F4 — Textos, badges, estados e ícones
Subtítulo sempre `TextMuted`; badges em 2 variantes com `Caption 11`; componente único de
estado vazio; overlay de carregamento único no `Win11SpinnerTemplate`; tiles 32/38/44.
Risco: baixo.

### F5 — Contraste e legibilidade
`#666`/`#555` de texto para `#8A8A8A`; fim do `FontSize 9/10` informativo; foco visível;
`Wrap`/`TextTrimming` onde já houve corte (RAM, BCD, Conversor).
Risco: baixo.

### F6 — Verificação por imagem
`tests/UiShotHarness`: abre cada página em janela fora da tela, `UpdateLayout()`, salva
PNG em `%TEMP%\kitlugia_ui\<pagina>.png`. Mesmo padrão de `GpuHarness`/`ClockHarness`.
É o único jeito confiável de revisar visual aqui, porque WPF não renderiza no agente.

### F7 — Antirregressão
`tests/uistyleaudit`: falha se aparecer hex fora da allowlist, `FontSize` fora da escala,
`Storyboard` em lista/campo ou `Button.Template` inline novo. Roda junto do build, como o
`CheckUiThreading`.

---

## 4. Ordem de execução

1. F0 + F6 (base + medir).
2. F1 lote 1 (10 páginas mais usadas).
3. F2 nos 3 arquivos com mais duplicação (`ExmTweaksPage`, `TweaksPage`, `NetworkPage`).
4. F3 + F4 nas páginas já migradas, depois nas restantes.
5. F5 + F7 no fim, com a allowlist já refletindo o estado limpo.

---

## 5. Critérios de aceite

- Hex hardcoded fora da allowlist em `Pages/`: **0**.
- Título de página em 28; subtítulo `TextMuted` 13; seção 16/Bold.
- `Button.Template` inline: só nos componentes de tema (≤ 5 no app).
- Nenhum `Storyboard` em `TextBox`/`ListBoxItem` de lista longa.
- `dotnet build` 0 erros; `UiShotHarness` gera PNG das 46 páginas sem exceção.
- Revisão visual por lote, página a página, contra a captura anterior.

---

## 6. Checklist

### F0 — Tokens ✅ concluída
- [x] Adicionar `Surface0`/`Surface1`/`Surface2` e `BorderSubtle`/`BorderStrong` em `Brushes.xaml` (+ `WarningBrush`)
- [x] Criar `Themes/Typography.xaml` com a escala (`sys:Double`) e estilos `TextBodyStyle`, `TextBodyMutedStyle`, `TextSmallStyle`, `TextCaptionStyle`
- [x] Mesclar `Typography.xaml` no `Generic.xaml` (antes de `PageStyles.xaml`)
- [x] `PageStyles.xaml`: remover o setter inválido de `CardContainerStyle`
- [x] `PageStyles.xaml`: `PageTitleStyle` em 28 (e demais estilos tokenizados)
- [x] Build 0 erros e encoding BOM+CRLF preservado

### F1 — Cabeçalho de página
- [ ] Criar `Controls/PageHeader.xaml` + `.xaml.cs` (ícone, título, subtítulo, ações)
- [ ] Migrar lote 1: Dashboard, Desempenho, RAM, Tweaks, Todos os Tweaks, Integridade, Apps, Serviços, Drivers, Rede
- [ ] Migrar lote 2: Reparos, Privacidade, Bloatware, Programas, Atualizações, Contexto, Inicialização, Processos
- [ ] Migrar lote 3: restantes de `Pages/` + `Pages/WindowsSettings/`
- [ ] Remover margens/títulos duplicados que sobrarem

### F2 — Botões
- [ ] Estilos `PrimaryBtn`/`SecondaryBtn`/`DangerBtn`/`GhostBtn`/`IconBtn`/`ChipBtn`/`BadgeStyle`
- [ ] `ExmTweaksPage`: trocar templates inline (126)
- [ ] `TweaksPage`: trocar templates inline (96)
- [ ] `NetworkPage`: trocar templates inline (32)
- [ ] `MainWindow` e `GameBoostPage`

### F3 — Superfícies
- [ ] `CardStyle` animado restrito a card clicável isolado
- [ ] `SubtleCardStyle` sem `DropShadowEffect`
- [ ] Remover sombra de listas (Apps, Drivers, Serviços, Store Remake)
- [ ] Raio unificado em 8 nos cards

### F4 — Estados e ícones
- [ ] Componente de estado vazio único
- [ ] Overlay de carregamento único (`Win11SpinnerTemplate`)
- [ ] Badges em 2 variantes / `Caption 11`
- [ ] Tiles de ícone 32/38/44

### F5 — Contraste
- [ ] `TextMuted` para `#8A8A8A` e remoção de `#666`/`#555` em texto
- [ ] Fim do `FontSize 9/10` informativo
- [ ] Foco visível em todo controle focável

### F6 — Capturas
- [ ] `tests/UiShotHarness` gerando PNG das 46 páginas
- [ ] Baseline de imagens salvo para comparação

### F7 — Antirregressão
- [ ] `tests/uistyleaudit` com allowlist de hex e escala
- [ ] Rodar no build e documentar o comando
