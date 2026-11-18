# Auditoria visual por pixels (sem "ver" a imagem)

Como verificar se uma janela nativa/WPF está renderizando corretamente quando o
agente **não consegue abrir imagens**. Em vez de "olhar" o print, comparamos
cores de pixels em coordenadas conhecidas contra a paleta esperada do tema.

Funciona para qualquer app Win32/WPF/D3D desde que a janela possa ser capturada.

---

## 1. Capturar a janela

Existem duas formas. **Prefira `PrintWindow`** — ela renderiza o conteúdo da
própria janela, ignorando o que estiver por cima (z-order).

### a) `PrintWindow` (recomendado — imune a janelas sobrepostas)

```powershell
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;using System.Runtime.InteropServices;using System.Drawing;
public class W{
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
  public struct RECT{public int Left,Top,Right,Bottom;}
}
'@
$h = (Get-Process -Id <PID>).MainWindowHandle
$r = New-Object W+RECT; [W]::GetWindowRect($h,[ref]$r) | Out-Null
$w = $r.Right-$r.Left; $hh = $r.Bottom-$r.Top
$bmp = New-Object System.Drawing.Bitmap $w,$hh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[W]::PrintWindow($h,$hdc,2) | Out-Null   # 2 = PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save("$env:TEMP\shot.png")
```

`PW_RENDERFULLCONTENT` (flag `2`) força o redraw completo — essencial para
janelas D3D11/D2D1 e WPF, que não respondem bem ao `PrintWindow` clássico
(flag `0`).

### b) `CopyFromScreen` (só se a janela estiver visível e em primeiro plano)

```powershell
$g.CopyFromScreen($r.Left,$r.Top,0,0,$bmp.Size)
```

**Problema real encontrado:** capturou uma janela escura de outro app por cima
da nossa — todos os pixels "errados" eram de outra janela. `SetForegroundWindow`
nem sempre funciona (requer direito de foreground). Por isso `PrintWindow` é o
padrão.

---

## 2. Ler pixels

```powershell
function px($x,$y){ $c=$bmp.GetPixel($x,$y); "$($c.R),$($c.G),$($c.B)" }
px 640 15    # -> "26,36,50"  = #1A2432
```

`Bitmap.GetPixel` retorna `Color` com `R,G,B,A` (0–255). Compare com o hex do
tema dividido em canais: `#1A2432` → R=0x1A=26, G=0x24=36, B=0x32=50.

---

## 3. Identificar "o que é o que" — o método

O segredo é **amostrar coordenadas que você conhece do layout**, não pixels
aleatórios:

### a) Mapa de coordenadas

Antes de amostrar, descubra onde cada elemento está:

- `GetWindowRect` dá a posição/tamanho da janela → converta para coordenadas
  relativas ao bitmap (`x_rel = x_tela - rect.Left`).
- Para janelas WS_POPUP/D3D: o client area = window rect inteiro.
- Para layout interno: leia o código de render e calcule — ex.: no
  `KitTaskManager` nativo, barra de título = `y ∈ [0,30)`, abas = `[30,60)`,
  header da grade = logo abaixo da toolbar, status bar = últimos 24 px.

### b) Amostrar o CENTRO dos elementos, nunca a borda

Anti-aliasing e bordas de 1 px geram cores intermediárias. Amostre o interior:

```
ERRADO:  px 640 30    (pode cair na linha divisória)
CERTO:   px 640 15    (meio da barra de título)
ERRADO:  px 200 100   (pode cair no texto ou numa linha de grade)
CERTO:   px 200 105   (fundo do header, longe dos glifos)
```

### c) Texto vs fundo

Texto é desenhado como glifos escuros (ou claros) sobre o fundo liso:

- Fundo liso → amostra retorna a cor exata do tema (ex.: `17,24,35`).
- Glifo de texto → retorna a cor do texto (`238,238,238`) ou a mistura
  anti-aliased com o fundo.

Para saber se a grade "tem dados", conte pixels escuros/claros na área:

```powershell
$dark=0;$tot=0
for($y=80;$y -lt 700;$y+=4){for($x=10;$x -lt 1100;$x+=4){
  $c=$bmp.GetPixel($x,$y);$tot++
  if($c.R -lt 100 -and $c.G -lt 100 -and $c.B -lt 100){$dark++}}}
"$dark/$tot"   # ~3% de pixels escuros = texto renderizado na grade
```

### d) Tabela de correspondência

Monte a expectativa ANTES de amostrar (ex.: paleta do tema):

| Elemento | Coordenada (1280×800) | Esperado | Medido |
|---|---|---|---|
| Barra de título | (640,15) | `#1A2432` → 26,36,50 | 26,36,50 ✓ |
| Fundo janela | (640,300) | `#111823` → 17,24,35 | 17,24,35 ✓ |
| Header da grade | (200,100) | `#1A2432` | 26,36,50 ✓ |
| Caixa de busca | (80,72) | `#1F2A3A`/branco | 255,255,255 ✓ |
| Painel detalhes | (1100,300) | `#141C28` → 20,28,40 | 236,233,216* |

*Se divergir, investigue: pode ser hover, seleção, ou elemento sobreposto.

---

## 4. Checklist de diagnóstico

| Sintoma | Causa provável |
|---|---|
| Tudo `255,255,255` | Janela sem conteúdo D3D (device não criado, `hbrBackground` branco aparecendo) ou captura de janela errada |
| Tudo de outra cor escura | `CopyFromScreen` capturou janela por cima → use `PrintWindow` |
| Cores "quase certas" mas escuras | Pixel caiu em texto/borda → amostre o centro do elemento |
| Alguns elementos certos, outros faltando | Render parcial — provável exceção no meio do frame ou layout errado |
| `PrintWindow` retorna `False` | HWND inválido ou janela minimizada |

---

## 5. Exemplo real (KitTaskManager nativo)

```powershell
# 1. descobre o pid
$p = Get-Process KitTaskManager
# 2. captura via PrintWindow -> ktm.png
# 3. amostra pontos conhecidos do layout:
px 640 15    # barra de título  -> esperado #1A2432
px 640 300   # fundo            -> esperado #111823
px 200 100   # header da grade  -> esperado #1A2432
px 80 72     # caixa de busca   -> esperado #1F2A3A ou branco
px 640 790   # status bar       -> esperado #111823
# 4. conta pixels de texto na área da grade (prova de que há linhas)
```

**Lição aprendida:** a primeira captura via `CopyFromScreen` mostrou tudo
branco; a via `PrintWindow` revelou que o app estava correto o tempo todo —
o problema era z-order, não render.

---

## 6. Limitações

- Não verifica layout fino (alinhamento, truncamento de texto) — só cor.
- Não "lê" o conteúdo dos textos (não é OCR).
- DPI scaling: se a janela usa `SetProcessDpiAwareness`, as coordenadas do
  `GetWindowRect` são físicas — bate com o bitmap 1:1.
- Para texto legível, combine com captura de tela real + leitura humana, ou
  use ferramentas de OCR se precisar.
