#!/usr/bin/env python3
"""core_privilege_layout.py — prova que TODA copia de TOKEN_PRIVILEGES/LUID esta
com o layout nativo correto (o bug historico: `long Luid` sem Pack=4 desloca
Attributes e o Windows devolve TRUE + ERROR_NOT_ALL_ASSIGNED 1300).

Layout nativo do Windows (x64):
    typedef struct _TOKEN_PRIVILEGES {
        DWORD PrivilegeCount;   // offset 0
        LUID  Luid;             // offset 4  (DWORD LowPart + LONG HighPart = 8 bytes)
        DWORD Attributes;       // offset 12
    } TOKEN_PRIVILEGES;         // 16 bytes

Formas corretas no C#:
  A) [StructLayout(LayoutKind.Sequential)] + struct LUID { uint LowPart; int HighPart; } + LUID Luid
  B) [StructLayout(LayoutKind.Sequential, Pack = 4)] + long Luid
Forma ERRADA: long Luid com layout padrao (offset 8) ou Pack = 1.

Uso:  python3 tests/core_privilege_layout.py
"""
import re
from pathlib import Path

def out(s=''):
    print(str(s).encode('ascii', 'replace').decode('ascii'))

roots = [Path('KitLugia.Core'), Path('KitLugia.GUI')]
files = []
for r in roots:
    files += [p for p in r.rglob('*.cs')
              if not ({'obj', 'bin', 'Resources'} & set(p.parts))]

out('== TOKEN_PRIVILEGES / LUID nas P/Invoke ==\n')
ok = bad = 0
for p in sorted(files):
    try:
        src = p.read_text(encoding='utf-8-sig')
    except Exception:
        continue
    for m in re.finditer(r'(?:\[StructLayout[^\]]*\]\s*)?(?:private|public|internal)?\s*struct\s+TOKEN_PRIVILEGES\s*\{(.*?)\}', src, re.S):
        body = m.group(1)
        # o atributo [StructLayout(...)] faz parte do match — olhar head + o match inteiro
        head = src[max(0, m.start() - 200):m.end()]
        pack4 = 'Pack = 4' in head or 'Pack=4' in head
        has_luid_struct = re.search(r'\bLUID\s+\w+\s*;', body) is not None
        has_long = re.search(r'\blong\s+\w+\s*;', body) is not None
        line = src[:m.start()].count('\n') + 1
        ptype = None
        if has_luid_struct:
            ptype = 'A) struct LUID (8 bytes)'
            verdict = 'OK' if not pack4 else 'OK'
        elif has_long and pack4:
            ptype = 'B) long + Pack=4'
            verdict = 'OK'
        elif has_long and not pack4:
            ptype = 'long SEM Pack=4'
            verdict = 'ERRADO (Attributes desloca; AdjustTokenPrivileges = TRUE + erro 1300)'
        else:
            ptype = '?'
            verdict = 'REVISAR'
        if verdict == 'OK':
            ok += 1
        else:
            bad += 1
        out(f'  {p.as_posix()}:{line}')
        out(f'      forma: {ptype}  ->  {verdict}')
        # conta campos
        fields = [f.strip() for f in body.strip().split(';') if f.strip()]
        out(f'      campos: {fields}')

out(f'\n  {ok} OK / {bad} com problema')

# confere tambem a struct LUID solta
out('\n== struct LUID avulsa ==')
for p in sorted(files):
    try:
        src = p.read_text(encoding='utf-8-sig')
    except Exception:
        continue
    for m in re.finditer(r'struct\s+LUID\s*\{([^}]*)\}', src):
        body = ' '.join(m.group(1).split())
        line = src[:m.start()].count('\n') + 1
        good = ('LowPart' in body and 'HighPart' in body
                and re.search(r'\b(byte|sbyte|short|uint|int)\b', body) is not None
                and 'long' not in body)
        out(f'  {p.as_posix()}:{line}  {{{body}}}  ->  {"OK" if good else "REVISAR"}')
