#!/usr/bin/env python3
"""core_redundant_api.py — antigo x novo no Core.

(a) IRMAO VIVO   -> para cada metodo morto, existe um metodo VIVO com nome parecido?
                    Ex.: ShrinkPartitionUsingWMI (morto) x ShrinkPartitionUsingStorageAPI (vivo)
(b) SOBREPOSICAO -> dois arquivos com o mesmo assunto compartilhando nomes de metodo
                    (sinal de uma implementacao que substituiu a outra).
(c) FAMILIA NATIVE -> a camada Native* duplica a BCL? (NativeRegex x Regex, ...)

Uso:  python3 tests/core_redundant_api.py
"""
import re
from collections import defaultdict
from pathlib import Path

CORE = Path('KitLugia.Core')

def out(s=''):
    print(str(s).encode('ascii', 'replace').decode('ascii'))

def read(p):
    try:
        return p.read_text(encoding='utf-8-sig')
    except Exception:
        return ''

FILES = sorted(p for p in CORE.rglob('*.cs')
               if not ({'obj', 'bin', 'Resources'} & set(p.parts)))

# Uma passada unica sobre a solution: conta ocorrencias de cada identificador em
# linhas que NAO sao comentario (chamada, method group ou mencao de texto).
from collections import Counter
COUNT = Counter()
IDENT = re.compile(r'[A-Za-z_]\w*')
for p in Path('.').rglob('*.cs'):
    if {'obj', 'bin', 'backups'} & set(p.parts):
        continue
    for line in read(p).splitlines():
        s = line.lstrip()
        if s.startswith(('//', '///', '*')):
            continue
        COUNT.update(IDENT.findall(line))

def mentions(name):
    return COUNT.get(name, 0)

DEF = re.compile(
    r'^\s{4,8}public\s+(?:static\s+)?(?:async\s+)?(?:override\s+)?'
    r'(?:[\w<>,\[\]\.\?]+)\s+([A-Za-z_]\w*)\s*\(', re.M)

methods = []          # (file, name, alive)
for p in FILES:
    src = read(p)
    for m in DEF.finditer(src):
        name = m.group(1)
        if not name[:1].isupper() or name in ('Equals', 'GetHashCode', 'ToString', 'Dispose'):
            continue
        alive = mentions(name) - 1 > 0
        methods.append((p.as_posix(), name, alive))

dead = [m for m in methods if not m[2]]
alive = [m for m in methods if m[2]]

def tokens(name):
    parts = re.findall(r'[A-Z][a-z0-9]*', name)
    return [t.lower() for t in parts if len(t) > 2]

out(f'metodos publicos analisados: {len(methods)}  (vivos {len(alive)} / mortos {len(dead)})')

# ------------------------------------------------------------------ (a)
out('\n' + '=' * 78)
out('(a) METODO MORTO COM IRMAO VIVO DO MESMO ASSUNTO (candidato a "versao antiga")')
out('=' * 78)
pairs = []
for f, name, _ in dead:
    tk = set(tokens(name))
    if not tk:
        continue
    for f2, name2, _ in alive:
        if name2 == name or f2 == f:
            continue
        tk2 = set(tokens(name2))
        common = tk & tk2
        # 2 tokens em comum ja caracteriza mesmo assunto (ex.: Shrink+Partition, Network+Adapter)
        if len(common) >= 2:
            pairs.append((name, f, name2, f2, common))
seen = set()
for name, f, name2, f2, common in pairs:
    key = (name, name2)
    if key in seen:
        continue
    seen.add(key)
    out(f'  MORTO  {name}   ({Path(f).name})')
    out(f'   VIVO  {name2}   ({Path(f2).name})   tokens em comum: {sorted(common)}')
out(f'  ({len(seen)} par(es))')

# ------------------------------------------------------------------ (b)
out('\n' + '=' * 78)
out('(b) ARQUIVOS QUE COMPARTILHAM METODOS (implementacao substituida?)')
out('=' * 78)
by_file = defaultdict(set)
for f, name, _ in methods:
    by_file[f].add(name)
names = sorted(by_file)
found = 0
for i in range(len(names)):
    for j in range(i + 1, len(names)):
        a, b = names[i], names[j]
        inter = by_file[a] & by_file[b]
        if len(inter) >= 3:
            found += 1
            out(f'  {Path(a).name} <> {Path(b).name}   {len(inter)} metodo(s): {sorted(inter)[:8]}')
out(f'  ({found} par(es))')

# ------------------------------------------------------------------ (c)
out('\n' + '=' * 78)
out('(c) FAMILIA Native* x BCL (a camada nativa duplica o .NET?)')
out('=' * 78)
NATIVE_BCL = {
    'NativeRegex': 'System.Text.RegularExpressions.Regex',
    'NativePath': 'System.IO.Path / FileInfo',
    'NativeGlob': 'Directory.EnumerateFiles + PatternMatcher',
    'NativeSearch': 'SearchEngine (busca global)',
    'NativeSha256': 'System.Security.Cryptography.SHA256',
    'NativeBlake3': 'sem equivalente na BCL (justificado)',
    'NativeRegistry': 'Microsoft.Win32.Registry',
    'NativeDiskIo': 'sem equivalente (Storage API + IOCTL) — justificado',
    'NativeMft': 'sem equivalente contraparte gerenciada — justificado',
    'NativeUsn': 'sem equivalente (USN Journal) — justificado',
}
for nat in sorted({m[0] for m in methods if 'Native' in Path(m[0]).name}):
    base = Path(nat).stem
    pub = sorted(by_file.get(nat, []))
    if not pub:
        continue
    out(f'\n  {base}  ({NATIVE_BCL.get(base, "?")})')
    for x in pub:
        tag = '' if mentions(x) - 1 > 0 else '   [MORTO]'
        out(f'      {x}{tag}')
