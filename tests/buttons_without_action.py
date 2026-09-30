#!/usr/bin/env python3
"""buttons_without_action.py — lista <Button> sem Click/Command (candidatos a botao morto).

Le todo XAML do KitLugia.GUI (ignora /obj/) e imprime os botoes reais (nao os
sub-tags <Button.ToolTip>, <Button.Style> etc.) que nao tem Click nem Command.

Uso:  python3 tests/buttons_without_action.py [pasta-raiz-xaml]
"""
import re
import sys
from pathlib import Path

root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path('KitLugia.GUI')

TAG = re.compile(r'<Button(?=[\s>])(.*?)(?:/>|>)', re.S)
found = 0
for f in sorted(root.rglob('*.xaml')):
    if '/obj/' in str(f) or '\\obj\\' in str(f):
        continue
    try:
        src = f.read_text(encoding='utf-8-sig')
    except Exception:
        continue
    out = []
    for m in TAG.finditer(src):
        if 'Click=' in m.group(1) or 'Command=' in m.group(1):
            continue
        # InfoButtonStyle = botao "i" que so existe pelo ToolTip (padrao do kit)
        if 'InfoButtonStyle' in m.group(1):
            continue
        # botao "i" inline: Cursor=Help + <Button.ToolTip> (padrao do kit)
        if 'Cursor="Help"' in m.group(1) or '<Button.ToolTip' in m.group(0):
            continue
        line = src[:m.start()].count('\n') + 1
        body = re.sub(r'\s+', ' ', m.group(0))
        if len(body) > 160:
            body = body[:160] + '...'
        out.append(f'    L{line}: {body}')
    if out:
        found += len(out)
        print(f'--- {f.as_posix()}')
        print('\n'.join(out))

print(f'\ntotal de botoes sem acao: {found}')
