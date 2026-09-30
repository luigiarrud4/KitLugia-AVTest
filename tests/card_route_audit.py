#!/usr/bin/env python3
"""card_route_audit.py — cruza o ROTULO do cartao/botao com a PAGINA de destino.

Ideia: um cartao "Ferramentas de Sistema" que navega para PageType.Repairs e um
comando fora do lugar. O script monta o mapa handler -> PageType.X a partir dos
one-liners `NavigationHelper.NavigateTo(PageType.X)` e confronta com o Content/
ToolTip do botao no XAML, apontando os que nao compartilham NENHUM token.

Uso:  python3 tests/card_route_audit.py [pasta-xaml]
"""
import re
import sys
from pathlib import Path

root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path('KitLugia.GUI/Pages')
gui = Path('KitLugia.GUI')

# 1) handler -> destino
route = {}
for cs in gui.rglob('*.xaml.cs'):
    try:
        src = cs.read_text(encoding='utf-8-sig')
    except Exception:
        continue
    for m in re.finditer(r'void\s+([A-Za-z_]\w*)\s*\([^)]*\)\s*(?:=>|{)[^\n]*?NavigateTo\(\s*PageType\.(\w+)',
                         src):
        route[m.group(1)] = m.group(2)
    for m in re.finditer(r'void\s+([A-Za-z_]\w*)\s*\([^)]*\)\s*\{\s*[^\n]*?NavigateTo\(\s*PageType\.(\w+)', src):
        route.setdefault(m.group(1), m.group(2))

# 2) botoes com Content/Click
BTN = re.compile(r'<Button(?=[\s>])(.*?)(?:/>|>)', re.S)
label_re = re.compile(r'Content="([^"]*)"')
click_re = re.compile(r'Click="([A-Za-z_]\w*)"')

def words(s):
    s = re.sub(r'&#x[0-9A-Fa-f]+;', ' ', s)      # entidades
    s = re.sub(r'[^0-9A-Za-zÀ-ÿ]+', ' ', s)      # pontuacao
    return {w.lower() for w in s.split() if len(w) > 2}

STOP = {'botao', 'clicar', 'abre', 'abrir', 'para', 'com', 'dos', 'das', 'uma'}

suspects = []
for f in sorted(root.rglob('*.xaml')):
    try:
        src = f.read_text(encoding='utf-8-sig')
    except Exception:
        continue
    for m in BTN.finditer(src):
        tag = m.group(1)
        cm = label_re.search(tag)
        hm = click_re.search(tag)
        if not cm or not hm:
            continue
        dest = route.get(hm.group(1))
        if not dest:
            continue
        lw, dw = words(cm.group(1)), words(dest)
        if not (lw & dw) and lw - STOP:
            line = src[:m.start()].count('\n') + 1
            suspects.append((f.as_posix(), line, cm.group(1), hm.group(1), dest))

print(f'handlers de navegacao mapeados: {len(route)}')
print(f'cartoes com rotulo x destino sem palavra em comum: {len(suspects)}\n')
for f, line, label, handler, dest in suspects:
    print(f'  {f}:{line}')
    print(f'      rotulo  : {label}')
    print(f'      destino : PageType.{dest}   ({handler})')
