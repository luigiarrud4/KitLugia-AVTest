# Plano — trazer o "fechado à mão" para o padrão (extração sem exclusão)

> Pedido: páginas/janelas feitas à mão (fora de `Pages/`) às vezes têm elementos
> bem-feitos que merecem virar padrão reutilizável. Regra do usuário:
> **ajustar, não excluir** — nada do que funciona é removido sem substituto.

## 1. Inventário do que está "fora"

| Local | Estilos/templates próprios | Hex | Observação |
|---|---|---|---|
| `Windows/TaskManager/KitTaskManagerWindow.xaml` | **139** (sistema `Tm*` completo: brushes, card, row, menu, scrollbar, combo, badge, toolbutton) | 399 | Tema paralelo azul-escuro (#4FC3F7) — identidade própria do TM, NÃO forçar paleta gold |
| `Windows/KitIsoStudioWindow.xaml` | 7 (`StudioCard`, `StudioSectionTitle`, `StudioCheck`, `StudioTab`...) | 40 | Cards/seções simples |
| `Windows/DeepCleanupDialog.xaml` | 7 | 14 | |
| `Windows/PresetsPersonalizadosWindow.xaml` | 6 | 18 | |
| `Windows/StoreRemakeWindow.xaml` | 6 (`CardBorder`, `PrimaryBtn`, `GhostBtn`, `ChipBtn`) | 12 | Botões genéricos bons candidatos |
| `Pages/*` (estilos locais) | 47 `<Style x:Key>` espalhados | — | `DarkComboBoxStyle` ×11 é o maior |

## 2. Candidatos a extração (o que é custom E reutilizável)

| Item | De onde | Vira | Por quê |
|---|---|---|---|
| ComboBox escuro canônico | `Themes/ComboBox.xaml` **já existe** (estilo implícito) | remover as 11 `DarkComboBoxStyle` locais + refs | mesmo visual, tema já tem hover dourado |
| `TmToolButton`, `TmMenuItem`, `TmBadge`, `TmSectionHeader` | KitTaskManagerWindow | referência para `Buttons.xaml`/`Menus.xaml`/`Cards.xaml` **somente se** forem genéricos o bastante; senão ficam no TM | TM é janela fechada — extrair só o que for neutro |
| `StudioCard`/`StudioSectionTitle`/`StudioTab` | KitIsoStudioWindow | mapear para `CardStyle`/`SectionHeaderStyle`/`TabControl.xaml` | provavelmente já cobertos |
| `PrimaryBtn`/`GhostBtn`/`ChipBtn` | StoreRemakeWindow | candidatas a `Buttons.xaml` se diferirem das existentes | botão ghost/chip pode não existir no tema |
| Loading overlay inline (10+ páginas) | Pages | `Controls/LoadingOverlay.xaml` **já existe** — migrar os que faltam | adoção parcial (5 páginas usam) |
| Confirm overlay inline (5+) | Pages | `Controls/LugiaConfirmationOverlay.xaml` **já existe** | idem |
| `RowSpinnerStyle`/spinner azul do StoreRemake | NetworkPage/StoreRemakePage | `Win11SpinnerTemplate`/`Win11MiniSpinnerTemplate` | templates oficiais existem |

## 3. O que NÃO mexer

- **Paleta `Tm*` do Task Manager**: é uma identidade visual deliberada (azul
  #4FC3F7 vs gold do kit). Forçar tokens mudaria o visual da janela — fora
  do "ajustar", seria redesign. Fica como exceção documentada.
- Páginas que funcionam: nenhuma remoção sem substituto compilando.

## 4. Execução (fases, cada uma com build + UiShotHarness)

- **F-A ComboBox dedup** ✅ FEITO (13/11)
  12 páginas migradas p/ estilo implícito de `Themes/ComboBox.xaml`
  (Apps, Dashboard, DiskConverter, GameBoost, IsoEditor, Network,
  Partitions, ReinstallPreserve, TraySettings, Winboot, WindowsUpdate,
  ContextMenuManager). ToolsPage preservada. Build 0 erros,
  UiShotHarness 5/5.
  1. Em cada uma das 11 páginas: remover o bloco `<Style x:Key="DarkComboBoxStyle">` (+ `DarkComboBoxItemStyle`/`ComboBoxToggleButton` se só serviam a ele)
  2. Remover `Style="{StaticResource DarkComboBoxStyle}"` dos ComboBox → estilo implícito do tema assume
  3. Exceção: página cujo combo depende de visual específico divergente → manter e anotar
  4. Build + `UiShotHarness` nas páginas tocadas (Dashboard, Apps, GameBoost, Network...)
- **F-B Loading/Confirm overlays** ✅ FEITO (13/11)
  `Controls/AreaLoadingOverlay` novo (Variante A: scrim + Win11Spinner +
  Message + Subtitle + Progress + AnimateMessage). Migradas: AppsPage×3,
  BloatwarePage, ProgramsPage, SystemInfoPage (com Subtitle), DriversPage.
  Puladas (widgets diferentes): ServicesPage StartupLoadingOverlay,
  DashboardPage benchmark, IntegrityPage círculo. `Controls/LoadingOverlay`
  antigo segue sem uso — candidato a remoção futura.
- **F-C Spinners avulsos** ✅ PARCIAL (13/11)
  IsoEditorPage: markup inline do spinner → `Win11SpinnerTemplate`.
  NetworkPage `RowSpinnerStyle`: MANTIDO — é micro-spinner data-bound de 13px
  por linha (DataTrigger/TestState), não loader de página; mini-template de
  24px seria downgrade. StoreRemakePage: pulada — tema azul #0078D4 é
  identidade da janela Store (mesma regra do TM).
- **F-D StoreRemake buttons** ⏭️ PULADO — `PrimaryBtn`/`GhostBtn`/`ChipBtn`
  são azul #0078D4, identidade da Store remake; mover pro tema gold
  quebraria a identidade. Mesma regra do `Tm*`.
- **F-E Tm* neutros** ✅ PARCIAL (13/11)
  Extraído: ToolTip implícito do kit → `Themes/BaseStyles.xaml` (estava
  duplicado em GameBoostPage e WinTunePage; TM/StoreRemake mantêm os seus
  por identidade). Resto do `Tm*` avaliado: TmToolButton/TmCard/
  TmSectionHeader/TmBadge/TmMenuItem/TmGrid já têm equivalentes no tema
  (SecondaryButtonStyle, CardStyle, SectionHeaderStyle...) ou são azul-TM —
  nada neutro a extrair.
- **F-F Páginas locais restantes** ✅ FEITO (13/11)
  Movidos p/ Themes (mesma x:Key, usos intactos): StepCardStyle→Cards,
  StatCard→Cards, SettingsCard→Cards, SearchResultCardStyle→Cards,
  StatValue/StatLabel→Typography, StutterButtonBase/SecButton/FlatIconButton/
  InfoButtonStyle→Buttons, ProcessItemStyle→ListStyles, TrayToggle/
  AdvancedToggle→CheckBox, SearchChipStyle→BaseStyles,
  DarkPlaceholderTextBoxStyle→BaseStyles (ex-DnsPlaceholderTextBoxStyle×2).
  Mantidos c/ motivo: WarningBoxStyle, StopServerButtonStyle (sem uso),
  ActionBtn, DashboardPage InfoButtonStyle (variante diferente),
  ToolsPage ComboBoxToggleButton (serve GoldComboBoxStyle).
  ToolTip: GameBoost/WinTune migrados — tema já tinha o impl. em
  BaseStyles (duplicata removida). Build 0 erros, UiShotHarness 25/25,
  ui_style_audit SAIDA 0 (hex 2188→2054).

## 5. Verificação por fase

```bash
dotnet build KitLugia.sln -p:OutDir=C:/kltmpbuild/ -v:q --nologo
tests/UiShotHarness --only <paginas tocadas>   # PNG antes/depois
python tests/ui_style_audit.py --check          # não pode regredir
```

## 6. MVVM — explicação rápida (pedido do usuário)

**MVVM = Model-View-ViewModel.** A tela (View) não tem código de lógica: ela
mostra dados e dispara comandos; uma classe separada (ViewModel) guarda o
estado e conversa com o motor (Model/serviços). A cola é o **data binding**
do WPF (`{Binding Nome}` no XAML).

- **O KitLugia hoje**: code-behind — o `.xaml.cs` da página contém UI + lógica
  + chamadas WMI/processos. Funciona, é o estilo clássico WPF, e o projeto já
  tem convenções (`Cleanup()`, CTS, timers) que seguram a disciplina.
- **MVVM traria**: lógica testável sem abrir janela, menos arquivos gigantes,
  designer mais limpo. **Custaria**: reescrever ~30k linhas de code-behind ou
  conviver com dois mundos por anos.
- **Veredito**: para este app (ferramenta de manutenção, 1 dev, iteração
  rápida), code-behind + UserControls é a escolha pragmática. MVVM compensa
  em apps com muito CRUD/formulário ou equipe grande. Se um dia doer, o
  caminho é extrair UserControls primeiro (ViewModels depois, se precisar).
