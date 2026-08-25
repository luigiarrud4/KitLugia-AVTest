# KIT ISO STUDIO — Expansor + Legibilidade

**Data:** 21/08/2026  
**Componentes:** `KitIsoStudioWindow.xaml` (Window), `IsoEditorPage` (botão ESTÚDIO)

## Por que expansor

O `OverlayConfig` da IsoEditor é um `Border` dentro da `Page` (`Grid.RowSpan 2 MaxWidth 780` dentro do `Frame` Column 1). Ele nunca cobre a sidebar (`Sistema (Início)`) — fica só no frame, com `Background #CC000000` semi e `DropShadow Blur 30` que borra o texto atrás (print do usuário: sidebar visível, conteúdo borrado, scroll amarelo).

O `PathExplorerWindow` (`Guardian`) é um `Window` separado (`Height 720 Width 1060 CenterOwner Background #0F0F0F Border #FFD700 WindowChrome Caption 0`) que cobre o `MainWindow` inteiro. Copiamos esse padrão.

## Como o Studio cobre a tela inteira (como PathManager)

- `KitIsoStudioWindow.xaml` = `Window` (`ShowInTaskbar False`, `WindowStartupLocation CenterOwner`, `MaxWidth/Height = WorkArea`). `WindowChrome CaptionHeight 0 ResizeBorder 6`
- `Border BorderBrush #FFD700 BorderThickness 1` com `Grid Margin 14` → ocupa `1060x740` centralizado sobre o `MainWindow`, cobrindo sidebar + header + console (não só o Frame)
- Botão `🧬 ESTÚDIO` em `IsoEditorPage.xaml:146` faz `new KitIsoStudioWindow { Owner = Application.Current.MainWindow }.ShowDialog()` → modal, bloqueia kit até fechar

Antes: `OverlayIsoStudio Border Max 960x740 Margin 20 Center` dentro da Page → moldura escura em volta. Agora: `Window` opaco cobre tudo.

## O que foi feito para ficar legível (reusável)

1. **Fundo opaco:** `Background #EE000000` (93% transparente, deixava kit atrás borrado) → `#FF0A0A0A` opaco (Window `#0F0F0F`). Elimina transparência que causava blur de fundo.
2. **Sem DropShadow no conteúdo:** removido `<DropShadowEffect BlurRadius 30 ShadowDepth 15 Opacity 0.6>` do `Border` interno — era o principal borrado em `FontSize 10-11 Consolas`.
3. **Texto nítido:** `TextOptions.TextFormattingMode="Display"` + `SnapsToDevicePixels="True"` no `Window`/`Border` raiz — força ClearType em `Segoe UI Variable` e desativa sub-pixel blur do WPF em 96dpi.
4. **Contraste sólido:** `Foreground #CCC/#FFFFFF` em vez de `#88FFFFFF` semi, `Border #2A2A2A` sólido, sem `Opacity 0.6` em `TextBlock`.
5. **Tamanhos:** `FontSize 10.5 → 11` em `CheckBox` + `Padding 8` + `CornerRadius 8` para não espremer.

**Como reusar:** copie o header do `KitIsoStudioWindow.xaml:60` (`Border BorderBrush #FFD700 + TextOptions.Display`) e o `WindowChrome` para qualquer overlay futuro que precise caber muito. Para texto pequeno, sempre use `Background opaco` + `TextFormattingMode Display` + sem `DropShadow` no container de texto.

## Arquivos

- `KitLugia.GUI\Windows\KitIsoStudioWindow.xaml(.cs)` — novo Window 2 colunas (AppX granular, Drivers, OEM + Registro, Idioma, Branding)
- `KitLugia.GUI\Pages\IsoEditorPage.xaml:146` — `BtnIsoStudio 130x38` + `BtnIsoStudio_Click` abre Window
- `IsoEditorPage.xaml.cs` — handlers `BtnIsoStudio_Click` / `BtnClose/Apply` sincronizam com `OverlayConfig`

## Teste

Abrir `KIT ISO EDITOR` → selecionar ISO → `🧬 ESTÚDIO` → Window cobre kit inteiro, texto nítido, 2 colunas com scroll, `APLICAR E FECHAR` volta ao painel padrão.
