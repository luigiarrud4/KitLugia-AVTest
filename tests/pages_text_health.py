#!/usr/bin/env python3
"""pages_text_health.py — saude de TEXTO dos XAML/CS da GUI.

Checa, arquivo por arquivo:
  1. MOJIBAKE   -> bytes tipicos de UTF-8 lido como Windows-1252 (Â, Ã, â€¦)
  2. PLACEHOLDER-> TODO / FIXME / "EM BREVE" / "Lorem" / "xxxx" visiveis ao usuario
  3. OBSOLETO   -> mencoes a recursos ja removidos da UI (ex.: Validation OS)
  4. PERGUNTA   -> '?' onde deveria haver acento (ex.: "Configura?oes")

Uso:  python3 tests/pages_text_health.py
"""
import re
from pathlib import Path

gui = Path('KitLugia.GUI')
# UTF-8 lido como CP1252 e regravado: "é" vira "Ã©" = C3 83 C2 A9, etc.
MOJI = [b'\xc3\x83\xc2', b'\xc3\x82\xc2', b'\xc3\xa2\xe2\x82\xac']
PLACEHOLDER = re.compile(r'(\bTODO\b|\bFIXME\b|\bXXX\b|Lorem ipsum|EM BREVE)')
OBSOLETE = re.compile(r'Validation\s?OS|VALIDATION OS')
BAD_QUESTION = re.compile(r'[A-Za-zÀ-ÿ]+\?[a-zà-ÿ]{2,}')

files = sorted([p for p in gui.rglob('*') if p.suffix in ('.xaml', '.cs')
                and 'obj' not in p.parts and 'bin' not in p.parts])

def out(s=''):
    """Print tolerante a console CP1252 (simbolos/emoji nao quebram o script)."""
    print(str(s).encode('utf-8', 'replace').decode('utf-8', 'replace').encode('ascii', 'replace').decode('ascii'))


print('=== 1) MOJIBAKE ===')
n = 0
for p in files:
    d = p.read_bytes()
    hits = [h for h in MOJI if h in d]
    if hits:
        n += 1
        print(f'  {p.as_posix()} -> {[h.decode("latin1") for h in hits]}')
print(f'  ({n} arquivo(s))\n')

print('=== 2) PLACEHOLDER visivel ===')
n = 0
for p in files:
    try:
        src = p.read_text(encoding='utf-8-sig')
    except Exception:
        continue
    for i, line in enumerate(src.splitlines(), 1):
        if PLACEHOLDER.search(line):
            n += 1
            out(f'  {p.as_posix()}:{i}  {line.strip()[:110]}')
print(f'  ({n} linha(s))\n')

print('=== 3) MENCAO A RECURSO REMOVIDO (Validation OS) ===')
n = 0
for p in files:
    try:
        src = p.read_text(encoding='utf-8-sig')
    except Exception:
        continue
    for i, line in enumerate(src.splitlines(), 1):
        if OBSOLETE.search(line):
            n += 1
            print(f'  {p.as_posix()}:{i}  {line.strip()[:110]}')
print(f'  ({n} linha(s))\n')

print('=== 4) "?" no meio de palavra (acento perdido) ===')
n = 0
for p in files:
    try:
        src = p.read_text(encoding='utf-8-sig')
    except Exception:
        continue
    for i, line in enumerate(src.splitlines(), 1):
        m = BAD_QUESTION.search(line)
        if m and '?' not in line[:m.start()]:
            n += 1
            print(f'  {p.as_posix()}:{i}  ...{m.group(0)}...  |  {line.strip()[:90]}')
print(f'  ({n} linha(s))')
