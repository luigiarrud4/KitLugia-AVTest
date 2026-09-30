#!/usr/bin/env python3
"""core_deep_audit2.py — auditoria confiavel do KitLugia.Core (v2, indexada em 1 passada).

  1. OBSOLETO EM USO    -> metodo [Obsolete] ainda chamado
  2. SLEEP EM ASYNC     -> Thread.Sleep dentro de metodo async
  3. DEAD API           -> metodo publico do Core sem nenhuma mencao fora da declaracao
  4. AWAIT SINCRONO     -> GetAwaiter().GetResult() / .Result

Uso:  python3 tests/core_deep_audit2.py
"""
import re
from pathlib import Path

CORE = Path('KitLugia.Core')

def out(s=''):
    print(str(s).encode('ascii', 'replace').decode('ascii'))

def read(p):
    try:
        return p.read_text(encoding='utf-8-sig')
    except Exception:
        return ''

def files(base=CORE):
    return sorted(p for p in base.rglob('*.cs')
                  if not ({'obj', 'bin', 'Resources'} & set(p.parts)))

# ── indice de TODA a solution em 1 passada (deterministico: sorted + read_once) ──
ALL = []
for p in sorted(Path('.').rglob('*.cs')):
    if {'obj', 'bin', 'backups'} & set(p.parts):
        continue
    ALL.append((p.as_posix(), read(p)))

# linhas nao-comentario pre-extraidas (imutavel)
NON_COMMENT = []
for path, src in ALL:
    for ln, line in enumerate(src.splitlines(), 1):
        s = line.lstrip()
        if s.startswith('//') or s.startswith('///') or s.startswith('*'):
            continue
        NON_COMMENT.append((path, ln, line))

def mentions(name):
    """Mencoes reais ao nome em linhas nao-comentario (chamada, method group, literal).
    A propria declaracao conta 1."""
    pat = re.compile(r'\b' + re.escape(name) + r'\b')
    return sum(len(pat.findall(line)) for _, _, line in NON_COMMENT)

def sec(t):
    out()
    out('=' * 78)
    out('== ' + t)
    out('=' * 78)

def is_comment(s):
    return not isinstance(s, str) or s.startswith('//') or s.startswith('///') or s.startswith('*')

# ── 1 ──────────────────────────────────────────────────────────────
sec('1) METODO [Obsolete] QUE AINDA E CHAMADO')
found = 0
for path, src in ALL:
    lines = src.splitlines()
    for i, line in enumerate(lines):
        if '[Obsolete' not in line:
            continue
        name = None
        for j in range(i, min(i + 4, len(lines))):
            m = re.search(r'([A-Za-z_]\w*)\s*\(', lines[j])
            if m and 'Obsolete' not in m.group(1):
                name = m.group(1)
                break
        if not name:
            continue
        if mentions(name) - 1 > 0:
            found += 1
            out(f'  {path}:{i + 1}  -> {name}: {mentions(name) - 1} chamada(s)')
out(f'  ({found} metodo(s) obsoleto(s) ainda em uso)')

# ── 2 ──────────────────────────────────────────────────────────────
sec('2) Thread.Sleep DENTRO DE METODO async')
SIG = re.compile(
    r'(?:public|private|internal|protected)\s+async\s+(?:Task|ValueTask)[\w<>,\[\]\.\?\s]*?\s+([A-Za-z_]\w*)\s*\(')
n = 0
for path, src in ALL:
    lines = src.splitlines()
    for m in SIG.finditer(src):
        start_line = src[:m.start()].count('\n')
        body, depth = [], 0
        for j in range(start_line, min(start_line + 400, len(lines))):
            body.append((j + 1, lines[j]))
            depth += lines[j].count('{') - lines[j].count('}')
            if depth == 0 and j > start_line:
                break
        for ln, txt in body:
            if re.search(r'\bThread\.Sleep\s*\(', txt) and not is_comment(txt.strip()):
                n += 1
                out(f'  {path}:{ln}  ({m.group(1)})  {txt.strip()[:90]}')
out(f'  ({n} ocorrencia(s))')

# ── 3 ──────────────────────────────────────────────────────────────
sec('3) METODO PUBLICO DO CORE SEM USO NENHUM (nem por reflection)')
DEF = re.compile(
    r'^\s{8}public\s+(?:static\s+)?(?:async\s+)?(?:override\s+)?'
    r'(?:[\w<>,\[\]\.\?]+)\s+([A-Za-z_]\w*)\s*\(', re.M)
dead = []
for path, src in ALL:
    if not path.startswith('KitLugia.Core/'):
        continue
    for m in DEF.finditer(src):
        name = m.group(1)
        if not name[:1].isupper() or name in ('Equals', 'GetHashCode', 'ToString', 'Dispose'):
            continue
        if mentions(name) - 1 <= 0:
            line = src[:m.start()].count('\n') + 1
            dead.append((path, line, src.splitlines()[line - 1].strip()[:105]))
out(f'  {len(dead)} metodo(s)')
out()
for f, line, decl in dead:
    out(f'  {f}:{line}  {decl}')

# ── 4 ──────────────────────────────────────────────────────────────
sec('4) AWAIT SINCRONO (.Result / GetAwaiter().GetResult())')
n = 0
for path, ln, line in NON_COMMENT:
    if re.search(r'GetAwaiter\(\)\.GetResult\(\)|\.Result\b(?!\s*=|s\b)', line):
        n += 1
        out(f'  {path}:{ln}  {line.strip()[:110]}')
out(f'  ({n})')
