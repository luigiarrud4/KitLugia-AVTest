#!/usr/bin/env python3
"""core_audit.py — auditoria READ-ONLY do backend (KitLugia.Core e afins).

Secoes:
  1. API OBSOLETA/LENTA  -> WebClient, WebRequest, new Thread, Thread.Sleep,
     ManagementObjectSearcher com Win32_*, Environment.Exit, Add-Type/PS,
     System.Drawing, Assembly.LoadFrom, string.Split(char), etc.
  2. DUPLICACAO          -> P/Invoke e blocos repetidos em varios arquivos
     (LUID, AdjustTokenPrivileges, RunProcess, GetVolumePathNames...)
  3. METODO MORTO        -> metodo publico do Core que ninguem chama na solution
  4. [Obsolete] EM USO   -> quem ainda chama algo marcado obsoleto
  5. TODO/FIXME/HACK     -> pendencias declaradas
  6. CATCH VAZIO         -> erro engolido sem log
  7. NOME LEGADO         -> metodos/classes com Old/Legacy/V1/V2/V3/New/Improved

Uso:  python3 tests/core_audit.py [--dead]
"""
import re
import sys
from pathlib import Path

ROOT = Path('.')
CORE = ROOT / 'KitLugia.Core'
SOL_DIRS = ['.']  # toda a solution

def out(s=''):
    print(str(s).encode('ascii', 'replace').decode('ascii'))

def core_files():
    return sorted(p for p in CORE.rglob('*.cs')
                  if 'obj' not in p.parts and 'bin' not in p.parts
                  and 'Resources' not in p.parts)

def read(p):
    try:
        return p.read_text(encoding='utf-8-sig')
    except Exception:
        return ''

# ------------------------------------------------------------------ 1
OBSOLETE = [
    (r'\bnew\s+WebClient\s*\(', 'WebClient -> HttpClient'),
    (r'\bHttpWebRequest\b|\bWebRequest\.Create\b', 'WebRequest -> HttpClient'),
    (r'\bnew\s+Thread\s*\(', 'new Thread -> Task.Run'),
    (r'\bThread\.Sleep\s*\(', 'Thread.Sleep -> await Task.Delay'),
    (r'ManagementObjectSearcher[^;]*Win32_', 'WMI Win32_* -> Storage API (MSFT_*) / Win32 nativo'),
    (r'\bEnvironment\.Exit\s*\(', 'Environment.Exit -> nao matar o processo do app'),
    (r'\bAdd-Type\b|\bInvoke-Expression\b', 'PowerShell embutido -> API nativa'),
    (r'\bSystem\.Drawing\b', 'System.Drawing (GDI+) -> WPF/ImageSharp'),
    (r'\bAssembly\.LoadFrom\s*\(', 'Assembly.LoadFrom -> LoadFromAssemblyPath/plugin ALC'),
    (r'\bAppDomain\.CurrentDomain\b', 'AppDomain -> AppContext/Assembly.Location'),
    (r'\bNewtonsoft\b', 'Newtonsoft.Json -> System.Text.Json'),
    (r'\.GetAwaiter\(\)\.GetResult\(\)', 'GetAwaiter().GetResult() -> async de verdade'),
    (r'\bDirectory\.GetFiles\([^)]*\)\s*\.Length\b', 'GetFiles().Length -> EnumerationOptions/EnumerateFiles'),
    (r'\bGC\.Collect\s*\(', 'GC.Collect -> sem forcar GC'),
    (r'\bRegistryKey\.OpenRemoteBaseKey\b', 'OpenRemoteBaseKey -> Registry local'),
    (r'\bServiceController\b', 'ServiceController -> ServiceHelper (WMI) ou sc.exe'),
    (r'\bnew\s+FileStream\s*\(', 'FileStream cru -> File.ReadAllBytes/FileStream async'),
]

def sec(title):
    out(f'\n{"=" * 78}\n== {title}\n{"=" * 78}')

sec('1) API OBSOLETA / LENTA')
hits = {}
for p in core_files():
    src = read(p)
    for i, line in enumerate(src.splitlines(), 1):
        s = line.strip()
        if s.startswith('//') or s.startswith('///'):
            continue
        for pat, why in OBSOLETE:
            if re.search(pat, line):
                hits.setdefault(why, []).append((p.as_posix(), i, s[:100]))
for why, lst in sorted(hits.items(), key=lambda kv: -len(kv[1])):
    out(f'\n  [{len(lst)}x] {why}')
    for f, i, s in lst[:6]:
        out(f'      {f}:{i}  {s}')
    if len(lst) > 6:
        out(f'      ... +{len(lst) - 6}')

# ------------------------------------------------------------------ 2
sec('2) DUPLICACAO ENTRE ARQUIVOS')
DUP = {
    'struct LUID': r'\bstruct\s+LUID\b',
    'AdjustTokenPrivileges P/Invoke': r'AdjustTokenPrivileges\s*\(',
    'OpenProcessToken P/Invoke': r'OpenProcessToken\s*\(',
    'LookupPrivilegeValue P/Invoke': r'LookupPrivilegeValue\s*\(',
    'GetVolumePathNamesForVolumeName P/Invoke': r'GetVolumePathNamesForVolumeName',
    'SendMessageTimeout P/Invoke (refresh do shell)': r'SendMessageTimeout',
    'RunExternalProcess/RunProcess helper': r'(RunExternalProcess|RunProcess|ExecuteShellCommand)\s*\(',
    'UseShellExecute = true/false': r'UseShellExecute\s*=',
    'new HttpClient': r'new\s+HttpClient\s*\(',
    'ProcessStartInfo': r'new\s+ProcessStartInfo\b',
}
for why, pat in DUP.items():
    files = set()
    total = 0
    for p in core_files():
        c = len(re.findall(pat, read(p)))
        if c:
            files.add(p.as_posix())
            total += c
    if total:
        out(f'  {total:>4}x  {why}   em {len(files)} arquivo(s)')
        if len(files) <= 12:
            for f in sorted(files):
                out(f'          {f}')

# ------------------------------------------------------------------ 3
sec('3) METODO PUBLICO DO CORE SEM CHAMADAS NA SOLUTION (dead API)')
whole = []
for p in ROOT.rglob('*.cs'):
    parts = set(p.parts)
    if 'obj' in parts or 'bin' in parts or 'backups' in parts:
        continue
    whole.append(read(p))
WHOLE = '\n'.join(whole)

DEF = re.compile(
    r'^\s*public\s+(?:static\s+)?(?:async\s+)?(?:sealed\s+)?'
    r'(?:partial\s+)?(?:readonly\s+)?[\w<>,\[\]\.\?\s]+?\s+([A-Za-z_]\w*)\s*\(',
    re.M)
dead = []
for p in core_files():
    src = read(p)
    for m in DEF.finditer(src):
        name = m.group(1)
        if name in ('if', 'while', 'for', 'switch', 'catch', 'using', 'return'):
            continue
        if not name[:1].isupper():
            continue
        uses = len(re.findall(r'\b' + name + r'\s*\(', WHOLE))
        if uses <= 1:
            line = src[:m.start()].count('\n') + 1
            decl = src.splitlines()[line - 1].strip()[:110]
            dead.append((p.as_posix(), line, name, decl))
out(f'  {len(dead)} candidato(s) (>=1 chamada fora da declaracao = vivo)\n')
for f, line, name, decl in dead[:60]:
    out(f'  {f}:{line}  {decl}')
if len(dead) > 60:
    out(f'  ... +{len(dead) - 60}')

# ------------------------------------------------------------------ 4
sec('4) [Obsolete] E QUEM AINDA CHAMA')
obs = []
for p in ROOT.rglob('*.cs'):
    if 'obj' in p.parts or 'bin' in p.parts:
        continue
    src = read(p)
    lines = src.splitlines()
    for i, line in enumerate(lines):
        if '[Obsolete' in line:
            name = ''
            for j in range(i, min(i + 3, len(lines))):
                m = re.search(r'\b([A-Za-z_]\w*)\s*\(', lines[j])
                if m:
                    name = m.group(1)
                    break
            obs.append((p.as_posix(), i + 1, name, line.strip()[:90]))
for f, i, name, decl in obs:
    calls = len(re.findall(r'\b' + name + r'\s*\(', WHOLE)) - 1 if name else 0
    out(f'  {f}:{i}  {decl}')
    if name:
        out(f'          -> {name}: {calls} chamada(s) fora da declaracao')

# ------------------------------------------------------------------ 5
sec('5) TODO / FIXME / HACK / WORKAROUND')
n = 0
for p in core_files():
    src = read(p)
    for i, line in enumerate(src.splitlines(), 1):
        if re.search(r'\b(TODO|FIXME|HACK|WORKAROUND|XXX)\b', line) and 'Obsolete' not in line:
            n += 1
            out(f'  {p.as_posix()}:{i}  {line.strip()[:110]}')
out(f'  ({n})')

# ------------------------------------------------------------------ 6
sec('6) CATCH VAZIO (erro engolido sem log)')
n = 0
for p in core_files():
    src = read(p)
    for m in re.finditer(r'catch\s*(?:\([^)]*\))?\s*\{\s*\}', src):
        n += 1
        line = src[:m.start()].count('\n') + 1
        out(f'  {p.as_posix()}:{line}')
out(f'  ({n})')

# ------------------------------------------------------------------ 7
sec('7) NOME LEGADO (Old/Legacy/V1/V2/V3/New/Improved/Fixed/Final)')
n = 0
for p in core_files():
    src = read(p)
    for i, line in enumerate(src.splitlines(), 1):
        if re.search(r'\b\w*(Old|Legacy|V[123]|New|Improved|Fixed|Final|Backup)\w*\s*\(', line) \
           and re.search(r'(public|private|internal)\s', line):
            n += 1
            out(f'  {p.as_posix()}:{i}  {line.strip()[:110]}')
out(f'  ({n})')
