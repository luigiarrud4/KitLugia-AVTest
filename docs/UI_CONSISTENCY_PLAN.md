# Plano de padronização visual do KitLugia

> Documento de trabalho. Cada fase termina com build limpo e uma lista de páginas
> migradas; o checklist no fim é a fonte de verdade do andamento.

## Estado base

| Item | Valor |
|---|---|
| Branch | `impeccable-polish` |
| Commit de referência | `f5c3961` (tag **`estavel-pre-ui-padronizacao`**) — busca padronizada, antes da UI |
| F0 | commit `cbc159e` — tokens de tipografia e superfície |
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
| Efeitos caros | **27** XAMLs com `DropShadowEffect`, **24** com `Storyboard` | `CardStyle` animava scale 1.015 + blur **por item** |
| Loading/estado vazio | 7 páginas com overlay, 2 implementações de spinner | `Win11SpinnerTemplate` vs `Ellipse` + storyboard inline |
| Margem de página | `30` em 10 páginas, `20`/`24` em outras | `PageStackStyle` (40,30,40,50) nunca usado |

Armadilha encontrada: `CardContainerStyle` (PageStyles) declarava
`<Setter Property="Style" Value="{DynamicResource CardStyle}"/>` — setter de `Style`
dentro de `Style` não é aplicável. Removido na F0.

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
Números grandes de painel (20/22/24/32) continuam permitidos como **métrica**, não como texto.

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
- Margem de página: **32** (`PageStackStyle`).

### Movimento

- Só micro-interação de **120–180 ms** em card clicável isolado (`InteractiveCardStyle`).
- **Zero** animação em campo de entrada.
- **Zero** animação em item de lista virtualizada (hover = cor).

### Exceções intencionais (documentar, não "corrigir")

- Gerenciador de Tarefas: paleta azul (`#1F2A3A`, `#2C3A4E`).
- Console/terminal: monoespaçada com `#AAA`.
- Store Remake: barra de comando própria.

---

## 3. Fases

### F0 — Tokens e guarda-corpo ✅
`Themes/Typography.xaml` (escala como `sys:Double` + todos os estilos de texto),
tokens de superfície/borda em `Brushes.xaml`, `PageStyles.xaml` reduzido a layout.

**Quirk registrado:** `{StaticResource FontSize*}` dentro do `Setter` de um estilo
declarado em **outro** dicionário resolve para `DependencyProperty.UnsetValue` — o estilo
aplica sem `FontSize` e o texto cai para 14. Por isso os estilos de texto moram no mesmo
arquivo dos tokens (`Typography.xaml`). Confirmado com `tests/UiShotHarness --selfcheck`.

### F1 — Cabeçalho de página ✅ (por estilo, não por controle)
Decisão de execução: em vez de substituir 46 cabeçalhos por um `UserControl` (muito markup,
sem verificação visual confiável na época), os títulos foram migrados **para
`PageTitleStyle`** por script (`tests/ui_style_audit.py --apply`), com o resto do layout intacto.
30 títulos em 30 arquivos. O `UserControl` fica para quando um cabeçalho novo for criado do zero.

### F2 — Botões e controles 🟡 parcial
Barras de ação da `AppsPage` (13 botões) migradas para `SecondaryButtonStyle`/`DeleteButtonStyle`
com altura 32 — antes eram 4 paletas (azul, cinza, vermelho, verde) e alturas 35.
Os ~400 templates inline (`ExmTweaksPage` 126, `TweaksPage` 96, `NetworkPage` 32,
`MainWindow` 28, `GameBoostPage` 18) continuam: cada um é um `ControlTemplate` próprio,
a troca é página a página com revisão de captura.

### F3 — Superfícies e listas ✅
`CardStyle`/`SubtleCardStyle`/`CompactCardStyle` sem `DropShadowEffect` e sem storyboard —
hover só por cor, raio 8. A animação de escala ficou isolada em `InteractiveCardStyle`.
Novo `SurfacePanelStyle` (painel estático com `Surface1`/`BorderSubtle`).
Efeitos inline dentro das páginas (sombras pontuais) seguem como estão.

### F4 — Textos, badges, estados e ícones 🟡 parcial
Novo `Controls/EmptyStateView.xaml` (ícone, título, dica + `Update(contagem)`), adotado na
`AllTweaksPage`. Durante a adoção apareceu um **bug real**: `_isLoaded` era marcado **depois** de
`ApplyFilter()`, então a lista só aparecia depois que o usuário mexia num filtro (a captura mostrava
0 de 108 tweaks; agora mostra os 108 na primeira entrada).
`Controls/LoadingOverlay.xaml` já existe (4 páginas usam) e 8 páginas ainda têm spinner inline.
Badges e tiles seguem por página.

### F5 — Contraste e legibilidade 🟡 parcial
`TextMuted` `#858585` → `#8A8A8A`. **419** `FontSize` normalizados em 42 páginas
(`9`/`10` → 11, `14` → 13). Restam **260** usos de `15` (sub-cabeçalhos, decisão manual
por página: 16 seção ou 13 corpo) e o foco visível global.

### F6 — Verificação por imagem ✅
`tests/UiShotHarness` (novo): renderiza **52 páginas** fora da tela, uma PNG por página em
`tests/UiShotHarness/out*/`, índice em `_index.csv`. Modos: `--only`, `--list`, `--selfcheck`,
`--size`, `--pump`, `--out`. Construtores com parâmetros opcionais são aceitos.

### F7 — Antirregressão ✅
`tests/ui_style_audit.py`: `--check` relata título/subtítulo fora do estilo, `FontSize` fora
da escala e hex hardcoded, e **sai com código 2** quando há violação; `--apply` normaliza títulos.

---

## 4. Achados do harness (bugs pré-existentes, fora do escopo desta fase)

- `BloatwarePage`: `InvalidOperationException` — storyboard anima a propriedade `Angle`, que não
  resolve no caminho de propriedade.
- `DiskConverterPage`: `NullReferenceException` na carga.
- `AllTweaksPage`: **corrigido** — a lista não aparecia na primeira carga (`_isLoaded` marcado depois de `ApplyFilter`); agora lista 108 tweaks e mostra o estado vazio quando o filtro zera.
- `AppsPage`: barra de ações tinha 4 paletas; agora 1 neutra + 1 destrutiva.

---

## 5. Critérios de aceite

- Título de página em 28/SemiBold via `PageTitleStyle`: **30/30** páginas com título textual.
- Hex hardcoded fora da allowlist em `Pages/`: 2.133 (base de trabalho para a allowlist).
- `Button.Template` inline: ainda nos 5 arquivos grandes (ver F2).
- `dotnet build` 0 erros; `UiShotHarness` 52/52 páginas.
- Revisão visual por lote contra a captura anterior.

---

## 6. Checklist

### F0 — Tokens ✅
- [x] `Surface0`/`Surface1`/`Surface2`, `BorderSubtle`/`BorderStrong`, `WarningBrush` em `Brushes.xaml`
- [x] `Themes/Typography.xaml` com a escala (`sys:Double`) e todos os estilos de texto
- [x] `Typography.xaml` mesclado no `Generic.xaml` (antes de `PageStyles.xaml`)
- [x] `PageStyles.xaml`: setter inválido de `CardContainerStyle` removido
- [x] `PageTitleStyle` em 28
- [x] Build 0 erros e encoding BOM+CRLF preservado

### F1 — Cabeçalhos ✅
- [x] Script de normalização de título (`tests/ui_style_audit.py --apply`)
- [x] 30 títulos de página migrados para `PageTitleStyle`
- [x] Auditoria confirma 0 título textual fora do estilo (`--check` sai 2 só por subtítulo)
- [ ] Subtítulos: normalização automática descartada (o 1º TextBlock cinza é rótulo de estado em várias páginas) — ajuste manual pendente
- [ ] `Controls/PageHeader` — só quando houver página nova; não vale reescrever 46 layouts

### F2 — Botões 🟡
- [x] `AppsPage`: 3 barras (13 botões) → `SecondaryButtonStyle`/`DeleteButtonStyle`, altura 32
- [ ] `ExmTweaksPage` (126 templates inline)
- [ ] `TweaksPage` (96)
- [ ] `NetworkPage` (32)
- [ ] `MainWindow` (28) e `GameBoostPage` (18)

### F3 — Superfícies ✅
- [x] `CardStyle` sem sombra e sem animação; hover por cor
- [x] `SubtleCardStyle`/`CompactCardStyle` sem `DropShadowEffect`
- [x] `InteractiveCardStyle` para card clicável isolado
- [x] `SurfacePanelStyle` novo
- [x] Raio unificado em 8 nos cards
- [ ] Sombras inline pontuais dentro das páginas

### F4 — Estados e ícones 🟡
- [x] `Controls/EmptyStateView` (ícone/título/dica + `Update`) e adoção na `AllTweaksPage`
- [x] Bug da lista vazia na primeira carga (`_isLoaded` marcado depois do `ApplyFilter`)
- [ ] Adotar o estado vazio em Apps, Drivers, Serviços, Programas
- [ ] Overlay de carregamento único (8 páginas com spinner inline → `LoadingOverlay`)
- [ ] Badges em 2 variantes / `Caption 11`
- [ ] Tiles de ícone 32/38/44

### F5 — Contraste 🟡
- [x] `TextMuted` → `#8A8A8A`
- [x] `9`/`10` → 11 e `14` → 13 (419 atributos, 42 páginas)
- [ ] `15` restante (260) decidido por página
- [ ] Foco visível em todo controle focável

### F6 — Capturas ✅
- [x] `tests/UiShotHarness` com 52 páginas e `_index.csv`
- [x] `--selfcheck` prova que o estilo resolve `FontSize` 28
- [x] Baselines: `out/` (antes), `out3/` (títulos), `out4/` (fontes)

### F7 — Antirregressão ✅
- [x] `ui_style_audit.py --check` com código de saída
- [x] Relatório de hex por arquivo (base da allowlist)
