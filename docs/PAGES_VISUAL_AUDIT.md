# Auditoria Visual e de Comandos — página por página

Data: 29/09/2026 · Build: **0 erros** · Escopo: **todas as 48 páginas** (42 ativas + 6 mortas).

Complementa `docs/PAGES_ACTIVE_AUDIT.md` (que tratou ciclo de vida: `Cleanup`, `Unloaded`,
timers, vazamento). Aqui o foco é o que o usuário **vê** e **clica**: elementos visuais
fora do lugar e comandos/handlers incorretos, órfãos ou mentirosos.

---

## 1. Método (4 scripts read-only, ficam no repo)

| Script | O que responde |
|---|---|
| `bash tests/pages_wiring_audit.sh` | Handler citado no XAML que não existe · método `*_Click` que nenhum XAML referencia · `FindName` sem `x:Name` · path hardcoded |
| `python3 tests/buttons_without_action.py` | `<Button>` real sem `Click`/`Command` (ignora os botões "i" de ToolTip, que são o padrão do kit) |
| `python3 tests/pages_text_health.py` | Mojibake · `TODO`/`EM BREVE` visível · menção a recurso removido · acento perdido ("Configura?oes") |
| `python3 tests/pages_visual_audit.py` | `Title` x cabeçalho divergentes · título de seção repetido · página sem `ScrollViewer` · texto com altura fixa sem wrap |
| `python3 tests/pages_accent_audit.py` | palavra portuguesa sem acento em `Text`/`Content`/`ToolTip`/`Header` |
| `bash tests/normalize_crlf.sh <arquivos>` | rede de segurança: garante CRLF (`.editorconfig`) preservando o BOM |

Estado **depois** das correções:

```
handlers ausentes (XAML→cs) ......... 0
handlers órfãos (cs→XAML) .......... 0
botões reais sem ação .............. 0   (na auditoria, sobram só falsos positivos conhecidos)
mojibake ........................... 0 arquivo
acento faltando em texto visível ... 0 ocorrência
menção a recurso removido .......... 0
```

---

## 2. Correções aplicadas

### 2.1 Regressões reais (funcionalidade inalcançável)

| # | Página | Achado | Correção |
|---|---|---|---|
| 1 | `SecurityPage` | O card **"Ações Rápidas"** sumiu do XAML, mas `BtnMaxSecurity_Click`, `BtnGamingMode_Click` e `BtnRestoreDefaults_Click` continuavam no `.cs` — três perfis completos (Máxima Segurança / Modo Gaming / Restaurar Padrões) **impossíveis de acionar** | Card restaurado com os 3 botões, descrições e estilos do resto da página |
| 2 | `KitIsoStudioWindow` (janela viva do ISO Studio) | **3 botões sem handler** ("Importar .reg", "Limpar", "Desmarcar tudo") e `BtnApplyDebloat_Click` com **corpo vazio** ("✅ Marcar 40+" não marcava nada) | Handlers implementados: importar `.reg` via `OpenFileDialog`, limpar o campo, marcar/desmarcar todos os AppX da aba (`PanelAppxPacks`) |
| 3 | `SettingsPage` | Dois toggles **mentiam**: "Log Detalhado" salvava a preferência e nunca aplicava (havia um `// TODO` no lugar); "Notificações" era gravado e lido por ninguém | `Log Detalhado` → `Logger.VerboseCheckLogs`; `Notificações` → `AppSettingsHelper.ShowNotifications` (cache lazy, sem I/O no construtor) lido por `MainWindow.ShowNotification` — erros e toasts de progresso continuam aparecendo; subtítulo do toggle explica isso |
| 4 | `WinTunePage` | `InfoButton_Click` órfão: o código que abre o texto do tooltip num diálogo legível existia mas nenhum botão o chamava | Religado por evento roteado (`ButtonBase.ClickEvent`) no construtor — funciona em qualquer aba, inclusive as materializadas depois do load |
| 5 | `DriversPage` | `BtnWindowsUpdate_Click` órfão (abre as atualizações de driver do Windows) | Botão **Windows Update** adicionado ao card "Instalar Driver" (a capacidade já existia no Core) |

### 2.2 Comandos fora do lugar

| # | Página | Achado | Correção |
|---|---|---|---|
| 6 | `IsoEditorPage` | O **CANCELAR** do overlay "Configurar Criação" chamava o mesmo handler do "← VOLTAR" do rodapé e **jogava o usuário para AdvancedTools** (ou pior: para outra página, se a ISO Editor foi aberta pelo Dashboard) | Novo `BtnCancelConfigOverlay_Click` fecha só o overlay; "← VOLTAR" segue saindo da página |
| 7 | `PrivacyPage` | Botão **"Expandir Tudo"** não expandia nada (a página não tem um único `Expander`) — apenas recarregava, duplicando o "⟳ Atualizar" | Botão e handler removidos |
| 8 | `DashboardPage` | Card **"Ferramentas de Sistema"** com tooltip prometendo "SFC, DISM, limpeza de caches…" abria a `ExmTweaksPage` ("Tweaks Avançados (EXM)") — a busca global chama a mesma página de "Exm Tweaks" | Card renomeado para **"Tweaks Avançados (EXM)"**, tooltip fiel ao conteúdo e ícone alinhado ao da busca |
| 9 | `MainWindow` (3 pontos) | Atalho desconhecido respondia **"EM BREVE — Página em desenvolvimento"** (enganoso: a página existe, o atalho é que é inválido) | Agora responde "PÁGINA NÃO ENCONTRADA" e orienta menu lateral / busca (Ctrl+K) |

### 2.3 Handlers órfãos removidos (código sem botão)

`DashboardPage` (×4: `BtnGoToTools`, `BtnGoToAllTweaks`, `BtnOptimizeStandard`,
`BtnOptimizeExtreme` — os dois últimos já vinham marcados `[Obsolete]`), `CleanupPage`
(`RegistryIssueSelection_Changed`), `TraySettingsPage` (`BtnAddProcessLimit_Click`, shim de
compatibilidade), `WinbootPage` (`BtnQuickViewXml_Click`, duplicava o botão EDITAR).

### 2.4 Elementos visuais e texto

| # | Página | Achado | Correção |
|---|---|---|---|
| 10 | `WindowsUpdatePage` | O cabeçalho da página tinha um **"X" de 32px** no lugar do emoji (glifo quebrado em todas as outras páginas) | Trocado pelo emoji 🔄 |
| 11 | `ScreenPage` | Cabeçalho "Calibragem e Tela" (e o toast "Calibragem de cores salva") | "Calibração" |
| 12 | 11 páginas | **42 textos visíveis sem acento** (`Usuario`, `nao`, `versao`, `configuracoes`, `ACAO CRITICA: NAO FECHE...`, tooltips longos do Windows Update/Tweaks) | Todos corrigidos — a varredura `pages_accent_audit.py` fechou em 0 |

---

## 3. Tabela página a página

`OK` = varrido pelas 5 auditorias sem achado · `corrigido` = achado e corrigido nesta rodada ·
`backlog` = achado conhecido, não corrigido (com motivo) · `morta` = não alcançável pela UI.

| Página | Estado | Observação |
|---|---|---|
| `DashboardPage` | corrigido | Card renomeado + 4 handlers órfãos removidos |
| `TweaksPage` | corrigido | 16 acentos em tooltips |
| `ScreenPage` | corrigido | "Calibração" no cabeçalho e no toast |
| `AppsPage` | OK | — |
| `CleanupPage` | corrigido | Handler órfão removido |
| `NetworkPage` | OK | `Title` "Rede e Internet" x cabeçalho "Rede e Latência" (aceitável) |
| `WindowsPage` | corrigido | 4 subtítulos sem acento |
| `ToolsPage` | OK | `Title="ToolsPage"` cru (não exibido) |
| `GameBoostPage` | OK | — |
| `ServicesPage` | OK | `Title="ServicesPage"` cru (não exibido) |
| `RepairsPage` | OK | `FindName("OverlayContainer")` = MainWindow (falso positivo) |
| `DriversPage` | corrigido | Botão Windows Update adicionado |
| `PartitionsPage` | OK | — |
| `WinbootPage` | corrigido | Handler órfão + 3 rótulos "Usuário" |
| `AdvancedToolsPage` | OK | — |
| `IsoEditorPage` | corrigido | CANCELAR do overlay; overlay morto no backlog |
| `IntegrityPage` | OK | — |
| `SecurityPage` | corrigido | Card "Ações Rápidas" restaurado |
| `PrivacyPage` | corrigido | Botão "Expandir Tudo" removido |
| `ActivationPage` | OK | — |
| `TraySettingsPage` | corrigido | Handler órfão + 2 acentos |
| `UpdatePage` | OK | — |
| `DiagnosticPage` | OK | Timers por reflection (falso positivo conhecido) |
| `ServerPage` | OK | — |
| `RufusPage` | corrigido | Rótulo "Usuário" |
| `AdvancedRamCleanSettingsPage` | OK | — |
| `AllTweaksPage` | OK | `ListBox` provê o scroll |
| `ExmTweaksPage` | OK | `Title` "EXM Tweaks" x cabeçalho novo (coerente) |
| `StutterPage` | corrigido | "Memória" |
| `WinTunePage` | corrigido | Clique nos botões "i" religado |
| `QuickInstallPage` | OK | `Title="QuickInstall"` cru (não exibido) |
| `ShrinkPage` | corrigido | 2 acentos |
| `WindowsUpdatePage` | corrigido | Emoji do cabeçalho + 13 acentos |
| `WinpeToolsPage` | corrigido | 4 acentos (`C:\KL_WINPE` é texto de instrução/log — OK) |
| `ReinstallPreservePage` | corrigido | "AÇÃO CRÍTICA" |
| `SettingsPage` | corrigido | 2 toggles religados + `TODO` removido |
| `GlobalSearchPage` | OK | — |
| `ContextMenuPage` | OK | Só alcançável pela busca (backlog) |
| `ContextMenuManagerPage` | OK | — |
| `ContextMenuAddPage` | OK | — |
| `ForceStopUnlockPage` | OK | `FindName("TxtPath")` = janela dedicada (falso positivo) |
| `StoreRemakePage` | OK | — |
| `AboutPage` | morta | Substituída pelo GameBoost no rodapé |
| `BloatwarePage` | morta | `AppsPage` cobre |
| `OptimizationPage` | morta | Absorvida por Tweaks/WinTune |
| `ProcessMonitorPage` | morta | Virou a KitTaskManager; 5 handlers com corpo vazio |
| `ProgramsPage` | morta | `AppsPage` cobre |
| `ContextMenuPreviewPage` | morta | Preview embutido na `ContextMenuPage` |

---

## 4. Backlog (achado, não corrigido nesta rodada)

1. **`IsoEditorPage` — overlay `OverlayIsoStudio` é código morto.** ~158 linhas de XAML
   (a partir de `x:Name="OverlayIsoStudio"`) que **nunca ficam visíveis**: o único ponto que
   mexe na sua `Visibility` é `Collapsed`. O Studio real é a janela `KitIsoStudioWindow`
   (`BtnIsoStudio_Click`). Dentro do overlay morto sobraram 2 botões sem `Click`
   ("Importar .reg" / "Limpar") e 3 handlers que só servem a ele
   (`BtnCloseIsoStudio_Click`, `BtnApplyIsoStudio_Click`, `BtnStudioPickDriverFolder_Click`,
   `BtnIsoStudioApplyDebloat_Click`). Remover tudo junto é seguro, mas é um corte grande —
   pedir confirmação antes.
2. **`KitIsoStudioWindow` não aplica nada.** É um "expansor visual" (documentado no próprio
   código): o `TxtReg` / `TxtDriverFolder` / checkboxes do Studio não são lidos pelo fluxo
   real de criação da ISO, que usa `ChkRemoveAI` / `ChkInjectDrivers` do painel padrão.
   Consequência prática: colar um `.reg` no Studio e clicar APLICAR não injeta o registro.
3. **`Title` cru** em `ServicesPage`, `ToolsPage`, `IsoEditorPage`, `QuickInstallPage`,
   `WinbootPage`. `Page.Title` não é exibido em lugar nenhum do `MainWindow` — só higiene.
4. **6 páginas mortas** (tabela acima): continuam compilando e entram nos benchmarks.
5. **`ContextMenuPage`** só tem a busca global como entrada.
6. **`KitTaskManagerWindow.BtnKillTree_Click`** aparece como órfão; o script varre só o
   `*.xaml.cs` irmão e a janela usa partials (`KitTaskManager.*.cs`) — confirmar à mão.

---

## 5. Como revalidar

```bash
bash tests/pages_wiring_audit.sh
python3 tests/buttons_without_action.py
python3 tests/pages_text_health.py
python3 tests/pages_visual_audit.py
python3 tests/pages_accent_audit.py
```

Depois, no app (validação que só o usuário faz):

- **SecurityPage**: os 3 botões de Ações Rápidas aparecem e pedem confirmação.
- **ISO Studio** (IsoEditorPage → ESTÚDIO): "Importar .reg", "Limpar", "Marcar 40+" e
  "Desmarcar tudo" respondem.
- **ISO Editor**: CANCELAR na configuração volta para a página (não para AdvancedTools).
- **WinTune**: clicar num botão "i" abre o texto do tooltip numa janela.
- **Drivers**: botão "Windows Update" abre as atualizações opcionais.
- **Settings**: desligar "Notificações" silencia avisos (erros/progresso continuam);
  ligar "Log Detalhado" passa a registrar as checagens detalhadas.
