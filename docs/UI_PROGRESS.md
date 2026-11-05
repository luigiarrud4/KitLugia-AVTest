# Progresso da padronização visual do KitLugia

Arquivo de acompanhamento (**o que está feito, o que falta e o que já mordeu**). O *plano* é
`docs/UI_CONSISTENCY_PLAN.md` (diagnóstico, contrato visual, critérios de aceite); o *histórico
narrativo* de cada sessão fica no bloco de padronização do `AGENTS.md`. Este arquivo é a visão
única de estado: leia ele primeiro, depois vá para o plano quando precisar do "por quê".

Última atualização: **05/11** (rodada do hover do banner do Dashboard).

---

## 1. Onde o trabalho está

| Fase / etapa | Estado | Resultado |
|---|---|---|
| **F0** tokens de tipografia e cor | ✅ | `Themes/Typography.xaml` (escala 28/18/16/13/12/11) + `Surface0-2`, `BorderSubtle/Strong`, `WarningBrush` |
| **F1** cabeçalhos de página | ✅ | 30 títulos em `PageTitleStyle`; 7 subtítulos reais em `PageSubtitleStyle` |
| **F2** botões | 🟡 | 13 botões da `AppsPage` + `IconButtonTemplate` + **127** templates trocados; 175 → **48** inline |
| **F3** superfícies | ✅ | `CardStyle` sem sombra/zoom, `InteractiveCardStyle`, `SurfacePanelStyle`, raio 8 |
| **F4** estados e carregamento | ✅ | `Controls/EmptyStateView`, `Themes/Spinners.xaml`, 2 variantes oficiais de loading |
| **F5** contraste e tipografia | ✅ | `TextMuted` #8A8A8A, ~700 `FontSize` normalizados, `KitFocusVisual` |
| **F6** verificação por imagem | ✅ | `tests/UiShotHarness`: **52** páginas, 0 falhas |
| **F7** antirregressão | ✅ | `ui_style_audit.py --check` + allowlist + hook de pré-commit |
| **E1** bugs de runtime do harness | ✅ | `BloatwarePage` (animava `Angle`) e `DiagnosticPage` (NRE no `Cleanup`) |
| **E2** spinner em 1 arquivo | ✅ | `Spinners.xaml` mesclado; Apps/Programs/Bloatware usam o mesmo desenho |
| **E3** botões por família | ✅ | 127 ocorrências migradas por impressão digital |
| **E4** estados vazios | ✅ | Apps (Programas), Drivers (2 listas), Serviços, Programas |
| **E5** resto do `15` + foco | ✅ | 258 → 16 e 2 → 13; foco dourado em Button/TextBox/CheckBox/ComboBox |
| **E6** subtítulos | ✅ | 7 páginas convertidas |
| **E7** endurecer | ✅ | allowlist (51 arquivos, 2.116 hex), hook, `--baseline` |

**Nada aqui está bloqueado.** O que sobra é polimento opcional (seção 4).

---

## 2. Última rodada — hover (05/11)

1. **`Cursor="Hand"` era herdado** pelo `CardStyle`: todo texto dentro de um card mostrava a mão de
   clique sem haver clique. Saiu do estilo; quem clica usa `InteractiveCardStyle`.
2. **Zoom de card** virou opt-in por controle: `controls:HoverZoom.Enabled/Scale/DurationMs`.
   `CardStyle`/`SubtleCardStyle` ficaram sem zoom (1,01 só em `InteractiveCardStyle`).
3. **Cards-cartão** (8 botões grandes com template próprio) ganharam
   `controls:HoverZoom.Enabled="False"`: Repairs (1), Cleanup (3), Dashboard (4).
4. **Banner "Informações do Sistema"** (`Border x:Name="BtnPcInfo"`, `CardStyle`) é o único card do
   Dashboard que anima: **1,015 em 180 ms**, que era o zoom do `CardStyle` antigo.
5. **`IconTextButtonStyle`**: o `Padding` saiu do `Border` de fora para o `StackPanel` do conteúdo —
   o realce `hl` agora mede 238 de 240 px (antes nascia 10 px para dentro e as bordas laterais
   ficavam sem brilho).
6. **`CleanupPage`**: ícone de informação com `Cursor="Help"`, não `Hand`.

Provas: `AnimCheck` **44/44** (inclui case que instancia a `DashboardPage` de verdade),
`UiShotHarness` **52/52**, build **0 erros**, `--check` saída **0**.

---

## 3. Como verificar (comandos exatos)

```bash
# build de verificação (OutDir FORA da árvore: um OutDir relativo gera CS0101)
dotnet build KitLugia.sln -p:OutDir=C:/kltmpbuild/ -v:q --nologo

# hover: aplica o estilo real, dispara MouseEnter/MouseLeave e LÊ a escala (PNG não mostra hover)
dotnet build tests/AnimCheck/AnimCheck.csproj -v:q
./tests/AnimCheck/bin/Debug/net10.0-windows10.0.26100.0/AnimCheck.exe

# captura das páginas: --only casa por SUBSTRING ("TweaksPage" pega 3 páginas)
dotnet run --project tests/UiShotHarness/UiShotHarness.csproj -- --only DashboardPage --pump 500 --out tests/UiShotHarness/outN

# o harness só alcança Pages; a janela real precisa do WindowShot (off-screen, sem elevação)
dotnet run --project tests/WindowShot/WindowShot.csproj

# antirregressão (sai 2 em violação; roda também no pre-commit)
py -3 tests/ui_style_audit.py --check
```

---

## 4. Pendências reais (por impacto ÷ risco)

| Item | Tamanho | Vale? |
|---|---|---|
| 48 `<Button.Template>` inline únicos (11 arquivos) | por página, com captura antes/depois | só onde o botão aparece lado a lado com outro de altura/paleta diferente |
| Sombras inline pontuais | 1 remoção = 1 reteste | baixo ganho visual; deixar |
| 2.116 hex na allowlist | — | **não substituir em massa** (apaga a identidade de Store Remake etc.) |
| `Controls/PageHeader` | reescreveria 46 cabeçalhos | só para página nova |
| Badges (718 `FontSize=11`, 98 `CornerRadius=4`) | medido, não unificado | opcional; hoje já é consistente por família |
| Chaves de aparência (ligar/desligar animação/sombra/zoom em runtime) | seção em `TraySettingsPage` + flag em `HoverZoom` | pedido do usuário em aberto, não iniciado |
| Brilho do hover do banner (blur 5 → 12 do `CardStyle` antigo) | animação hoje morta | opcional; exigiria animar sem passar por Freezable congelado |

---

## 5. Armadilhas medidas (não repita)

1. **`StaticResource` de token entre dicionários mesclados** dentro de `Setter` resolve para
   `DependencyProperty.UnsetValue`: o estilo aplica sem `FontSize` e o texto cai para 14. Token e
   estilo que o consome precisam morar no **mesmo** arquivo (`Typography.xaml`), ou use
   `DynamicResource`. Diagnóstico: `UiShotHarness --selfcheck` imprime token, setters e o `FontSize`
   real de um `TextBlock`.
2. **`Freezable` vindo de `Setter`/`ControlTemplate` chega CONGELADO** (`IsFrozen = True`): animar
   `ScaleTransform` ou `Effect.BlurRadius` não lança erro e não move nada
   (`HasAnimatedProperties = False`). Era o caso do zoom do `CardStyle`, do hover do estilo global
   de `Button`, de `NavStyles`, `StutterPage`, `NetworkPage`, `MainWindow` e do blur dos cards.
   Solução: `Controls/HoverZoom.cs` (descongela/cria o transform por elemento).
3. **`Cursor` é herdado**: setar `Cursor="Hand"` num estilo de container contamina todo o conteúdo.
4. **`Page` só aceita `Window` ou `Frame` como pai** — sonda que hospeda página num `Grid` estoura
   `InvalidOperationException: Page somente pode ter Window ou Frame como pai`.
5. **`Grid.Row` é clampado** (sem `RowDefinitions`, todas as linhas caem na célula 0 e se
   sobrepõem) — declare as `RowDefinition`s.
6. **`<Setter Property="Style">` dentro de `Style` é inválido** (`CardContainerStyle` declarava isso
   e nunca poderia ser usado).
7. **CS0104 com WinForms** (o projeto referencia `System.Windows.Forms`): qualifique
   `System.Windows.Input.MouseEventArgs`, `System.Windows.Point`,
   `System.Windows.Shapes.Path`, `System.IO.Path`/`File` e declare
   `System.Windows.Controls.UserControl`.
8. **Encoding dos XAML**: preservar **BOM** (por arquivo) e **CRLF**. `open(..., "w", newline="\r\n")`
   em texto lido em modo binário **duplica** o `\r` (`\r\r\n`); use `newline=""` ou `"wb"`.
9. **Build**: `-p:OutDir` **relativo** cria `KitLugia.GUI/klbuild/` e o glob do SDK compila a cópia
   de `Resources/FilterCommands.cs` → **CS0101** (`Program` 2x). Use OutDir fora da árvore.
10. **Captura não prova hover** e nem dado vivo: duas rodadas do mesmo código dão PNGs iguais, mas
    scan em andamento muda o texto entre horas diferentes.

---

## 6. Arquivos-chave

| Caminho | Papel |
|---|---|
| `KitLugia.GUI/Themes/Generic.xaml` | ordem de merge: Brushes → **Typography** → Cards → Spinners → Buttons/NavStyles → PageStyles |
| `KitLugia.GUI/Themes/Typography.xaml` | escala (`sys:Double`) **e** os estilos de texto (juntos de propósito) |
| `KitLugia.GUI/Themes/Brushes.xaml` | paleta e superfícies |
| `KitLugia.GUI/Themes/Cards.xaml` | `CardStyle` / `InteractiveCardStyle` / `SubtleCardStyle` / `SurfacePanelStyle` |
| `KitLugia.GUI/Themes/Buttons.xaml` | estilos de botão + `IconTextButtonStyle` + zoom global 1,05/120 |
| `KitLugia.GUI/Themes/Icons.xaml` | `PathGeometry` 24x24 (Material Design Icons, Apache 2.0) — vetor, sem fonte |
| `KitLugia.GUI/Themes/Spinners.xaml` | `Win11SpinnerTemplate` (44) e `Win11MiniSpinnerTemplate` (24) |
| `KitLugia.GUI/Controls/HoverZoom.cs` | hover por escala que **funciona** (descongela o transform) |
| `KitLugia.GUI/Controls/EmptyStateView.cs` | estado vazio padrão (ícone/título/dica + `Update`) |
| `tests/UiShotHarness` | render das 52 páginas |
| `tests/AnimCheck` | prova de hover (44 checks) |
| `tests/WindowShot` | janela real off-screen (o harness só vê Pages) |
