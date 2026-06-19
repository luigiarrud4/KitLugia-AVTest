# Hunter Overlay — Método de Seleção Visual (100% funcional)

## Problema
Overlay de seleção (borda dourada) em torno da janela alvo precisa ser:
- Visível, sem piscar, sem artefatos XOR (DWM destrói XOR)
- Não bloquear `WindowFromPoint` / `WindowFromPhysicalPoint`
- Não travar o app (sem crash de `PAINTSTRUCT` / `EntryPointNotFoundException`)

## Solução Final: WinForms `Form` + `SetWindowDisplayAffinity`

Usar `System.Windows.Forms.Form` (WPF hospeda WinForms) com:

```
Form {
    FormBorderStyle = None
    ShowInTaskbar = false
    TopMost = true
    Opacity = 0.78
    BackColor = Gold  // ou qualquer cor sólida
}
```

No evento `Load`:
```
1. SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT)
   // Mouse passa através do overlay

2. outterRgn = CreateRectRgn(0,0,w,h)
   innerRgn = CreateRectRgn(3,3,w-6,h-6)
   CombineRgn(outter, outter, inner, RGN_DIFF)
   DeleteObject(inner)
   SetWindowRgn(hwnd, outter, true)
   // Forma de borda de 3px (recorta o centro)

3. SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE = 0x11)
   // Overlay some de WindowFromPoint/WindowFromPhysicalPoint
   // mas continua VISÍVEL na tela
```

## Importante

- `WS_EX_TRANSPARENT` + `SetWindowRgn` + `WDA_EXCLUDEFROMCAPTURE` = os 3 juntos
- Faltando qualquer um: ou o overlay bloqueia o clique, ou o detection falha, ou aparece quadrado inteiro
- Não usar `CreateWindowEx` nativo — `PAINTSTRUCT` tem `bool` vs `BOOL` mismatch que crasha
- Não usar `InvertRect` / `DrawFocusRect` — DWM não suporta XOR, causa flickering infinito

## Crosshair Overlay (mira azul)

Mesma técnica, mas para mostrar uma mira azul seguindo o cursor fora da janela do app:

```
Form {
    FormBorderStyle = None
    TopMost = true
    ShowInTaskbar = false
    Size = 40x40
    BackColor = Fuchsia
    TransparencyKey = Fuchsia   // tudo Fuchsia = transparente
}
```

No evento `Paint`: desenha uma cruz azul (`DodgerBlue`) com 4 traços finos e um círculo central.
No `Load`: `WS_EX_TRANSPARENT` + `WDA_EXCLUDEFROMCAPTURE`.

O `ShowCrosshair(x, y)` move o form para `(mouseX-20, mouseY-20)` a cada timer tick (30ms). Como é `TopMost` e `TransparencyKey`, aparece em cima de QUALQUER janela.

## Código de referência

Ver `HunterWindow.xaml.cs`:
- `UpdateOverlay(RECT)` — overlay de seleção (borda dourada)
- `ShowCrosshair(x, y)` / `DestroyCrosshair()` — mira azul
- `ShowInfo(x, y, name, path)` / `DestroyInfo()` — tooltip com nome do app e caminho encurtado, ao lado da mira
- `TruncatePath(path)` — encurta caminhos longos: `C:\Users\...\pasta\app.exe`
