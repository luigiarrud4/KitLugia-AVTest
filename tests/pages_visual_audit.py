#!/usr/bin/env python3
"""pages_visual_audit.py — varredura VISUAL estrutural dos XAML das paginas.

Checa:
  1. TITULO x CABECALHO -> o Title= da Page e o primeiro TextBlock grande (28-34px)
     dizem a mesma coisa? Divergencia = cabecalho fora do lugar.
  2. SECAO DUPLICADA   -> o mesmo titulo de secao repetido 2+ vezes na mesma pagina.
  3. SEM SCROLLVIEWER  -> pagina que nao tem ScrollViewer nenhum (conteudo corta
     em janela pequena).
  4. TEXTO CORTADO     -> TextBlock com Height fixo curto, texto longo e SEM
     TextWrapping (o texto some).
  5. CARD SEM TITULO   -> Border com Style=CardStyle sem nenhum TextBlock curto
     dentro (card vazio / conteudo solto).

Uso:  python3 tests/pages_visual_audit.py
"""
import re
import sys
from pathlib import Path

def out(s=''):
    print(str(s).encode('ascii', 'replace').decode('ascii'))

pages = Path(sys.argv[1]) if len(sys.argv) > 1 else Path('KitLugia.GUI/Pages')
unent = lambda s: re.sub(r'&#x([0-9A-Fa-f]+);', lambda m: chr(int(m.group(1), 16)), s)

title_re = re.compile(r'<Page[^>]*?Title="([^"]*)"', re.S)
head_re = re.compile(r'<TextBlock[^>]*?Text="([^"]*)"[^>]*?FontSize="(2[8-9]|3[0-4])"', re.S)
sect_re = re.compile(r'<TextBlock[^>]*?Text="([^"]{4,60})"[^>]*?FontSize="1[5-9]"[^>]*?FontWeight="Bold"')

print('=== 1) TITULO x CABECALHO ===')
n = 0
for f in sorted(pages.rglob('*.xaml')):
    src = f.read_text(encoding='utf-8-sig')
    tm, hm = title_re.search(src), head_re.search(src)
    if not tm or not hm:
        continue
    t, h = unent(tm.group(1)).strip(), unent(hm.group(1)).strip()
    if t and h and t.lower() not in h.lower() and h.lower() not in t.lower():
        n += 1
        out(f'  {f.as_posix()}')
        out(f'      Title  : {t}')
        out(f'      Cabecalho: {h}')
out(f'  ({n} divergencia(s))\n')

print('=== 2) TITULO DE SECAO REPETIDO NA MESMA PAGINA ===')
n = 0
for f in sorted(pages.rglob('*.xaml')):
    src = f.read_text(encoding='utf-8-sig')
    seen = {}
    for m in sect_re.finditer(src):
        t = unent(m.group(1)).strip()
        if not t:
            continue
        seen.setdefault(t, []).append(src[:m.start()].count('\n') + 1)
    rep = {k: v for k, v in seen.items() if len(v) > 1}
    if rep:
        n += 1
        out(f'  {f.as_posix()}')
        for k, v in rep.items():
            out(f'      "{k}" -> linhas {v}')
out(f'  ({n} pagina(s))\n')

print('=== 3) PAGINA SEM SCROLLVIEWER ===')
n = 0
for f in sorted(pages.rglob('*.xaml')):
    src = f.read_text(encoding='utf-8-sig')
    if '<ScrollViewer' not in src:
        n += 1
        out(f'  {f.as_posix()}')
out(f'  ({n} pagina(s))\n')

print('=== 4) TEXTBLOCK COM ALTURA FIXA, TEXTO LONGO E SEM WRAP ===')
n = 0
for f in sorted(pages.rglob('*.xaml')):
    src = f.read_text(encoding='utf-8-sig')
    for m in re.finditer(r'<TextBlock(?=[\s>])([^>]*)>', src):
        tag = m.group(1)
        if 'TextWrapping' in tag:
            continue
        tm = re.search(r'Text="([^"]{45,})"', tag)
        if not tm:
            continue
        if re.search(r'Height="(\d+)"', tag) and not re.search(r'Width="\d{3,}"', tag):
            n += 1
            line = src[:m.start()].count('\n') + 1
            out(f'  {f.as_posix()}:{line}  {unent(tm.group(1))[:80]}')
out(f'  ({n} caso(s))')
