#!/usr/bin/env python3
"""core_deep_audit.py — refinamento do core_audit.py: so achados confiaveis.

  1. OBSOLETO EM USO   -> metodo marcado [Obsolete] e ainda chamado de verdade
  2. SLEEP DENTRO DE ASYNC -> Thread.Sleep dentro de metodo async (bloqueia a thread)
  3. DEAD API          -> metodo publico do Core que nao e chamado nem por reflection
  4. AWAIT SINCRONO    -> GetAwaiter().GetResult() / .Result (risco de deadlock)
  5. API MODERNA       -> onde a alternativa atual existe e nao e usada

Uso:  python3 tests/core_deep_audit.py
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

# indice de todo o codigo da solution (para saber se algo e usado)
ALL = []
for p in Path('.').rglob('*.cs'):
    if {'obj', 'bin', 'backups'} & set(p.parts):
        continue
    ALL.append((p.as_posix(), read(p)))

def mentions(name):
    """Mencoes reais ao nome (fora de comentario): cobre chamada, method group
    (`=> Classe.AddVsCode`) e string literal. A propria declaracao conta 1."""
    pat = re.compile(r'\b' + re.escape(name) + r'\b')
    total = 0
    for _, src in ALL:
        for line in src.splitlines():
            s = line.lstrip()
            if s.startswith('//') or s.startswith('///') or s.startswith('*'):
                continue
            total += len(pat.findall(line))
    return total


def uses(name):
    """Compat: (mencoes, mencoes usadas como string)"""
    return mentions(name), 0

def sec(t):
    out(f'\n{"=" * 78}\n== {t}\n{"=" * 78}')

# ------------------------------------------------------------------ 1
sec('1) METODO [Obsolete] QUE AINDA E CHAMADO')
found = 0
for p in files():
    src = read(p)
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
        calls, _ = uses(name)
        # desconta a propria declaracao
        if calls - 1 > 0:
            found += 1
            out(f'  {p.as_posix()}:{i + 1}')
            out(f'      {line.strip()[:100]}')
            out(f'      -> {name}: {calls - 1} chamada(s)')
out(f'  ({found} metodo(s) obsoleto(s) ainda em uso)')

# ------------------------------------------------------------------ 2
sec('2) Thread.Sleep DENTRO DE METODO async')
SIG = re.compile(r'(?:public|private|internal|protected)\s+async\s+(?:Task|ValueTask)[\w<>,\[\]\.\?\s]*?\s+([A-Za-z_]\w*)\s*\(')
n = 0
for p in files():
    src = read(p)
    lines = src.splitlines()
    for m in SIG.finditer(src):
        start_line = src[:m.start()].count('\n')
        # corpo aproximado: ate a linha da chave de fechamento na coluna 8
        body, depth = [], 0
        for j in range(start_line, min(start_line + 400, len(lines))):
            body.append((j + 1, lines[j]))
            depth += lines[j].count('{') - lines[j].count('}')
            if depth == 0 and j > start_line:
                break
        for ln, txt in body:
            if re.search(r'\bThread\.Sleep\s*\(', txt) and not txt.strip().startswith('//'):
                n += 1
                out(f'  {p.as_posix()}:{ln}  ({m.group(1)})  {txt.strip()[:90]}')
out(f'  ({n} ocorrencia(s))')

# ------------------------------------------------------------------ 3
sec('3) METODO PUBLICO DO CORE SEM USO NENHUM (nem por reflection)')
DEF = re.compile(
    r'^\s{8}public\s+(?:static\s+)?(?:async\s+)?(?:override\s+)?'
    r'(?:[\w<>,\[\]\.\?]+)\s+([A-Za-z_]\w*)\s*\(', re.M)
dead = []
for p in files():
    src = read(p)
    for m in DEF.finditer(src):
        name = m.group(1)
        if not name[:1].isupper() or name in ('Equals', 'GetHashCode', 'ToString', 'Dispose'):
            continue
        if mentions(name) - 1 <= 0:
            line = src[:m.start()].count('\n') + 1
            dead.append((p.as_posix(), line, src.splitlines()[line - 1].strip()[:105]))
out(f'  {len(dead)} metodo(s)\n')
for f, line, decl in dead:
    out(f'  {f}:{line}  {decl}')

# ------------------------------------------------------------------ 4
sec('4) AWAIT SINCRONO (.Result / GetAwaiter().GetResult())')
n = 0
for p in files():
    for i, line in enumerate(read(p).splitlines(), 1):
        s = line.strip()
        if s.startswith('//') or s.startswith('///'):
            continue
        if re.search(r'GetAwaiter\(\)\.GetResult\(\)|\.Result\b(?!\s*=|s\b)', line):
            n += 1
            out(f'  {p.as_posix()}:{i}  {s[:110]}')
out(f'  ({n})')
