# Plano de padronização visual do KitLugia

> Documento de trabalho. Cada fase termina com build limpo e uma lista de páginas
> migradas; o checklist no fim é a fonte de verdade do andamento.

## Estado base

| Item | Valor |
|---|---|
| Branch | `impeccable-polish` |
| Commit de referência | `f5c3961` (tag **`estavel-pre-ui-padronizacao`**) — busca padronizada, antes da UI |
| F0 | commit `cbc159e` — tokens de tipografia e superfície |
| Últimos commits | `1b6f468` (títulos/cards/fontes/harness), `d9ae0ac` (estado vazio + bug da lista), `0938ba8` (doc) |
| Revisão | 05/11 — carregamento decidido (2 variantes), nova ordem em E1–E7 (seção 7) |
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

### Carregamento e feedback — 2 variantes oficiais

As páginas divergem aqui porque estávamos **testando** qual encaixava melhor. Os testes
acabaram: ficam duas variantes oficiais e o resto é exceção aceita (não é dívida).

| Variante | Quando usar | Anatomia |
|---|---|---|
| **A. Overlay de área** — `Win11SpinnerTemplate` | conteúdo que ainda está chegando dentro de uma área (lista, aba, painel) | scrim `#CC000000` + raio 8; rodela de 5 pontos dourados 44 px girando em 1,4 s (`Win11MiniSpinnerTemplate` 24 px para faixas); texto branco **16** (`13` quando o overlay é de faixa); linha de progresso `#FFD700` 12 opcional |
| **B. Toast de progresso** — `Controls/LugiaToast` ("notificação cinza") | operação longa que **não** deve bloquear a tela (instalar, atualizar, baixar) | card 350, raio 6, fundo escuro em gradiente `#CC2A2010 → #F00A0A0A`, borda dourada translúcida, entra deslizando da direita; vira verde/vermelho na conclusão |

Regra de escolha: **se o usuário ainda pode clicar em outra coisa → toast (B); se a área
está vazia esperando dado → overlay (A).** Nunca os dois para a mesma operação.

Consequência técnica: `Win11SpinnerTemplate`/`Win11MiniSpinnerTemplate` hoje moram **dentro**
de `AppsPage.xaml`; passam para `Themes/Spinners.xaml` para as demais páginas usarem a
variante A sem copiar markup.

### Exceções intencionais (documentar, não "corrigir")

- Gerenciador de Tarefas: paleta azul (`#1F2A3A`, `#2C3A4E`).
- Console/terminal: monoespaçada com `#AAA`.
- Store Remake: barra de comando própria.
- Carregamento próprio, mantido por decisão de 05/11: `MainWindow.LoadingOverlay` (card
  300x120 com borda accent, no boot), `IntegrityPage` (badge circular 90x90), `DriversPage`
  (overlay + barra de progresso de análise), `ServicesPage` (`StartupLoadingOverlay`),
  `StoreRemakePage` e `KitTaskManagerWindow` (tela de load própria).

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

### F2 — Botões e controles ✅
Barras de ação da `AppsPage` (13 botões) migradas para `SecondaryButtonStyle`/`DeleteButtonStyle`
com altura 32 — antes eram 4 paletas (azul, cinza, vermelho, verde) e alturas 35.

O número real de templates inline não era ~400: a medição com `tests/ui_button_audit.py`
achou **175 templates em 14 arquivos, mas só 46 grupos distintos** — e **120 deles eram
o MESMO ícone-botão** (info/engrenagem/lixeira) copiado em `ExmTweaksPage` (63),
`TweaksPage` (47), `GameBoostPage` (8) e `IntegrityPage` (2), mais 7 no `NetworkPage`.
Esse grupo virou `IconButtonTemplate` em `Themes/Buttons.xaml` e **127 ocorrências**
passaram a referenciá-lo por `Template="{StaticResource IconButtonTemplate}"`
(`tests/ui_buttons_migrate.py`). Restam **48 templates em 11 arquivos**, todos cards
clicáveis com paleta própria (NetworkPage 9, MainWindow 14, AdvancedRamClean 5…) —
são casos únicos, não cópias; trocá-los seria reescrever desenho, não remover duplicata.

### F3 — Superfícies e listas ✅
`CardStyle`/`SubtleCardStyle`/`CompactCardStyle` sem `DropShadowEffect` e sem storyboard —
hover só por cor, raio 8. A animação de escala ficou isolada em `InteractiveCardStyle`.
Novo `SurfacePanelStyle` (painel estático com `Surface1`/`BorderSubtle`).
Efeitos inline dentro das páginas (sombras pontuais) seguem como estão.

### F4 — Estados e carregamento ✅
Novo `Controls/EmptyStateView.xaml` (ícone, título, dica + `Update(contagem)`), adotado na
`AllTweaksPage`. Durante a adoção apareceu um **bug real**: `_isLoaded` era marcado **depois** de
`ApplyFilter()`, então a lista só aparecia depois que o usuário mexia num filtro (a captura mostrava
0 de 108 tweaks; agora mostra os 108 na primeira entrada).

**Carregamento (revisado em 05/11):** não há mais busca — as duas variantes oficiais estão
no contrato acima (overlay estilo AppsPage + toast cinza). O spinner saiu da `AppsPage` para
`Themes/Spinners.xaml` (`Win11SpinnerTemplate` 44 px e `Win11MiniSpinnerTemplate` 24 px) e a
`AppsPage` (3 painéis), `ProgramsPage` e `BloatwarePage` já usam a definição única. Os
carregamentos próprios de MainWindow, Integrity, Drivers, Services, Store Remake e Task Manager
**ficam como estão** (exceção aceita).

**Estados vazios:** `EmptyStateView` adotado em `ProgramsPage` (busca), `AppsPage` (aba
Programas), `DriversPage` (drivers e drivers de inicialização) e `ServicesPage` (serviços),
sempre com a contagem real do filtro e uma dica que muda conforme há busca ou não.

### F5 — Contraste e legibilidade ✅
`TextMuted` `#858585` → `#8A8A8A`. Normalização completa: **419** (`9`/`10` → 11,
`14` → 13) + **260** de `15` (`tests/ui_fontsize15.py`: 258 títulos → 16, 2 corpo → 13)
+ 56 (`9`/`10`/`10.5`/`11.5`/`12.5` → 11/12) + 39 (`14`→13, `17`→16, `8`→11).
Fora da escala sobram **apenas métricas de painel** (20 a 60), permitidas pelo contrato.
Foco visível: `KitFocusVisual` (borda dourada, `DynamicResource` — `StaticResource` entre
dicionários mesclados cairia em `UnsetValue`, o mesmo quirk da F0) aplicado aos estilos
implícitos de `Button`, `TextBox`, `CheckBox` e `ComboBox`.

### F6 — Verificação por imagem ✅
`tests/UiShotHarness` (novo): renderiza **52 páginas** fora da tela, uma PNG por página em
`tests/UiShotHarness/out*/`, índice em `_index.csv`. Modos: `--only`, `--list`, `--selfcheck`,
`--size`, `--pump`, `--out`. Construtores com parâmetros opcionais são aceitos.

### F7 — Antirregressão ✅
`tests/ui_style_audit.py`: `--check` relata título/subtítulo fora do estilo, `FontSize` fora
da escala e hex hardcoded, e **sai com código 2** quando há violação; `--apply` normaliza títulos;
`--baseline` regrava a allowlist.
**Allowlist por arquivo** (`tests/ui_style_allowlist.txt`, 51 arquivos, 2.116 hex): o check só
reprova quando um arquivo **passa** do teto — hex novo é pego, a paleta existente de páginas
como Store Remake não é apagada. **Hook**: `.githooks/pre-commit` roda o `--check`
(`git config core.hooksPath .githooks`).

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
- Hex hardcoded: **2.116** em 51 arquivos, cobertos pela allowlist (só reprova quem passar do teto).
- `Button.Template` inline: **48** em 11 arquivos, todos casos únicos (cards com paleta própria).
- `dotnet build` 0 erros; `UiShotHarness` 52/52 páginas.
- Revisão visual por lote contra a captura anterior.

---

## 5.1 Hover: o transform congelado (05/11)

Devolver o storyboard de escala ao `CardStyle` não foi suficiente, e a sonda `tests/AnimCheck`
mostrou por quê: um `ScaleTransform` criado em `Setter` de `Style` (ou no corpo de um
`ControlTemplate`) chega ao elemento **congelado**, e animar propriedade de Freezable congelado
não dá erro nem movimento. O hover de escala do kit estava morto em todos esses lugares.

Regra a partir de agora: **escala no hover se faz com `controls:HoverZoom`** (comportamento
danexado, transform criado por elemento), nunca com `Storyboard` apontando para um transform
declarado no estilo. Cor por hover continua sendo `Setter` no trigger, que funciona.

## 5.2 Hover: card nao promete clique (05/11)

Tres prints mostraram o mesmo erro de contrato: `Cursor` e' propriedade **herdada**, e o
`CardStyle` (um `Border`) punha `Cursor="Hand"` - a mao de clique aparecia sobre o card inteiro da
lista de tweaks, onde so' o toggle clica. O zoom de 1,5% piorava: num card de ~700 px a borda saia
~7 px do lugar (o mesmo motivo de a linha dourada dos reparos parecer passar do limite).

- `CardStyle` = realce **so' por cor**; `InteractiveCardStyle` = card que clica (Cursor + 1,01/200).
- Cards grandes com template inline (`RepairsPage`, `CleanupPage`, `DashboardPage`) **nao** herdam
  mais o zoom de botao (1,05): `HoverZoom.Enabled="False"`, e o hover fica com o brilho que o
  proprio template ja' declarava - igual as branches antigas, que nao tinham escala nenhuma ali.
- `IconTextButtonStyle`: o `Padding` saiu do `Border` e foi para o conteudo, entao o realce cobre a
  largura inteira (**238 de 240 px**) em vez de nascer 10 px para dentro.

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
- [x] Subtítulos: `tests/ui_subtitle.py` aplica `PageSubtitleStyle` quando o cinza é subtítulo de verdade (7 páginas); rótulos de estado e descrição de item ficam fora
- [ ] `Controls/PageHeader` — só quando houver página nova; não vale reescrever 46 layouts

### F2 — Botões 🟡
- [x] `AppsPage`: 3 barras (13 botões) → `SecondaryButtonStyle`/`DeleteButtonStyle`, altura 32
- [x] `IconButtonTemplate` (template de ícone-botão) em `Themes/Buttons.xaml`
- [x] **127** templates substituídos: ExmTweaks 63, Tweaks 47, Network 7, GameBoost 8, Integrity 2
- [x] Medição: 175 inline → **48** em 11 arquivos (todos únicos, documentados)

### F3 — Superfícies ✅
- [x] `CardStyle` sem sombra e sem animação; hover por cor
- [x] `SubtleCardStyle`/`CompactCardStyle` sem `DropShadowEffect`
- [x] `InteractiveCardStyle` para card clicável isolado
- [x] `SurfacePanelStyle` novo
- [x] Raio unificado em 8 nos cards
- [ ] Sombras inline pontuais dentro das páginas

### F4 — Estados e carregamento 🟡
- [x] `Controls/EmptyStateView` (ícone/título/dica + `Update`) e adoção na `AllTweaksPage`
- [x] Bug da lista vazia na primeira carga (`_isLoaded` marcado depois do `ApplyFilter`)
- [x] Decisão 05/11: 2 variantes oficiais de carregamento (overlay da AppsPage + toast cinza);
      os demais ficam como exceção aceita
- [x] `Themes/Spinners.xaml` com `Win11SpinnerTemplate`/`Win11MiniSpinnerTemplate`
- [x] Overlay oficial em AppsPage (3 painéis), ProgramsPage e BloatwarePage
- [x] Estado vazio em Apps (Programas), Drivers (2 listas), Serviços e Programas
- [x] Badges: medido — **718** usos de `FontSize="11"` (caption) e **98** de `CornerRadius="4"`,
      que é o raio do contrato; não existe estilo duplicado de badge a unificar (criar
      "2 variantes" seria abstração sem duplicata para remover)
- [x] Tiles de ícone: só **4** tiles com 38/44/48 px no markup; os ícones dentro de card são
      16/20/24 por decisão de cada página — nada a padronizar sem redesenhar

### F5 — Contraste 🟡
- [x] `TextMuted` → `#8A8A8A`
- [x] `9`/`10` → 11 e `14` → 13 (419 atributos, 42 páginas)
- [x] `15` restante: 258 → 16 (título) e 2 → 13 (corpo)
- [x] `9`/`10`/`10.5`/`11.5`/`12.5` → 11/12; `14` → 13; `17` → 16; `8` → 11
- [x] Foco visível (`KitFocusVisual`) em Button/TextBox/CheckBox/ComboBox

### F6 — Capturas ✅
- [x] `tests/UiShotHarness` com 52 páginas e `_index.csv`
- [x] `--selfcheck` prova que o estilo resolve `FontSize` 28
- [x] Baselines: `out/` (antes), `out3/` (títulos), `out4/` (fontes), `out6/` (52 páginas, estado atual)
- [x] Verificação final: `52/52` páginas capturadas, `0` falhas, build `0` erros

### F7 — Antirregressão ✅
- [x] `ui_style_audit.py --check` com código de saída
- [x] Allowlist por arquivo (`ui_style_allowlist.txt`, `--baseline`)
- [x] Hook `.githooks/pre-commit` + `core.hooksPath`
- [x] Estado atual: `--check` sai 0 (0 título, 0 subtítulo, 0 arquivo com hex novo)

---

## 7. Revisão de 05/11 — o que fazer a seguir

Feita depois de 4 commits e de olhar as **52 páginas** renderizadas. O que sobrou no plano não
pesa igual: alguns itens são cosméticos e de risco alto (mexer em 400 templates sem ganho
visível), outros são baratos e consertam o que o usuário vê de errado. A ordem abaixo troca o
"ordem das fases" por **impacto visível ÷ risco**.

**Estado: E1–E7 concluídos em 05/11** (build 0 erros, 52/52 capturas, `--check` sai 0).

| Ordem | Etapa | Escopo | Esforço | Risco | Por que aqui |
|---|---|---|---|---|---|
| **E1 ✅** | Bugs de runtime do harness | `BloatwarePage` (storyboard anima `Angle`) e `DiskConverterPage` (NRE na carga) | ~2 arquivos | baixo | São **crashes reais**, não estética: o harness absorve a exceção e a página sai pela metade |
| **E2 ✅** | Carregamento oficial em 1 lugar | `Themes/Spinners.xaml` + adotar em Bloatware/Programas/Portáteis | 0,5 dia | baixo | O padrão que você aprovou deixa de existir só dentro da `AppsPage` |
| **E3 ✅** | F2 — botões por família | `NetworkPage` (32), `TweaksPage` (96), `ExmTweaksPage` (126) | 3–4 dias | médio | Maior fonte de inconsistência **visível**: paletas e alturas diferentes lado a lado |
| **E4 ✅** | Estados vazios | Apps, Drivers, Serviços, Programas (`EmptyStateView`) | 1–2 dias | baixo | Página vazia sem explicação é o defeito que mais parece "quebrado" |
| **E5 ✅** | F5 — resto da tipografia + foco | 260 usos de `15` decididos por página; `FocusVisualStyle` global | 1–2 dias | baixo/médio | Legibilidade e teclado; o grosso (419) já foi |
| **E6 ✅** | F1 — subtítulos por página | 46 cabeçalhos, 5 por lote com captura | 1 dia | baixo | Fecha o cabeçalho sem reescrever layout |
| **E7 ✅** | F7 — endurecer (allowlist + hook) | allowlist de hex por página, hook de pré-commit, versão + tag do release | 1 dia | baixo | Impede a volta do que já foi feito |

### Como executar E3 (o lote que assusta)

Os ~400 templates inline **não** são 400 trabalhos: são poucas famílias repetidas. O passo 1 é
um relatório, não uma edição:

1. Script lista cada `<Button.Template>` com uma **impressão digital** do conteúdo (cores +
   raio + altura). Templates idênticos caem no mesmo grupo — uma página com 96 cópias iguais é
   **uma** edição.
2. Cada grupo recebe um estilo nomeado em `Themes/Buttons.xaml` (`GhostButtonStyle`,
   `ChipButtonStyle`, `IconButtonStyle`, `DangerButtonStyle`) — os 6 estilos que já existem
   (`Gold`, `Green`, `Recycle`, `Delete`, `Secondary`, `Action`) ficam como base.
3. Um lote = uma página: trocar, compilar, capturar, comparar com `out6/`. Página aprovada entra
   no commit; página estranha volta.

### O que NÃO vale fazer (decidido agora)

- **Substituição em massa dos 2.133 hex.** A maioria é dourado de destaque por página; trocar às
  cegas apaga a identidade de páginas como Store Remake. O valor está na **allowlist** por arquivo
  (E7), não na substituição.
- **`Controls/PageHeader` para reescrever 46 cabeçalhos.** Cada um tem layout próprio; o ganho
  visual é pequeno e o risco de regressão é alto. Fica só para página nova.
- **Unificar os carregamentos que você aprovou manter.** Estão documentados como exceção (contrato,
  seção 2) e não entram no checklist como pendência.
- **Remover `DropShadowEffect` inline** de páginas isoladas: pontual, some na comparação de
  52 capturas, e cada remoção é um reteste.
- **Mexer na paleta do Gerenciador de Tarefas** (azul, espelha o Windows).

### Definição de pronto por etapa

| Etapa | Pronto quando |
|---|---|
| E1 | As duas páginas capturam sem exceção na UI e sem mudança de layout no PNG |
| E2 | `Spinners.xaml` mesclado; nenhuma página copia o template; PNG da `AppsPage` idêntico ao de `out6/` |
| E3 | Nenhum `<Button.Template>` inline nas 4 páginas-alvo; altura 32 e uma paleta por função |
| E4 | Lista vazia mostra ícone + título + dica + ação, com contagem real |
| E5 | `--check` sem `FontSize` fora da escala; `Tab` mostra foco em todo controle focável |
| E6 | Subtítulo de página no estilo, sem confundir com rótulo de estado |
| E7 | `--check` roda em pré-commit; release com versão nova e `-ReleaseTag vX.Y.Z` |

---

## 8. Ferramentas criadas

| Arquivo | Para que serve |
|---|---|
| `tests/UiShotHarness` | renderiza as 52 páginas fora da tela e salva PNG; `--only`, `--list`, `--selfcheck`, `--size`, `--pump`, `--out` |
| `tests/ui_style_audit.py` | `--check` (sai 2 em violação), `--apply` (títulos/subtítulos), `--baseline` (regrava a allowlist) |
| `tests/ui_button_audit.py` | agrupa os `<Button.Template>` inline por impressão digital (o que é cópia e o que é único) |
| `tests/ui_buttons_migrate.py` | troca um template inline por um do tema, preservando as propriedades locais do botão |
| `tests/ui_fontsize15.py` | decide cada `FontSize="15"` (16 título / 13 corpo) com relatório antes de gravar |
| `tests/ui_subtitle.py` | aplica `PageSubtitleStyle`, ignorando rótulo de estado e descrição de item |
| `tests/AnimCheck` | prova o hover: aplica o estilo real, dispara `MouseEnter`/`MouseLeave` e lê o `ScaleTransform` (um PNG não mostra hover) |
| `tests/WindowShot` | abre a `MainWindow` real fora da tela, fecha o splash e captura repouso x hover (o `UiShotHarness` só alcança as Pages) |
| `.githooks/pre-commit` | roda o `--check` a cada commit (habilitado com `git config core.hooksPath .githooks`) |

Scripts que reescrevem XAML sempre preservam o BOM do arquivo e o CRLF. O `open(..., "w")`
do Python com `newline="\r\n"` **duplica** o `\r` quando o texto veio de leitura binária — use
`newline=""` ou grave com `"wb"`.
