#!/usr/bin/env python3
"""core_modern_api.py — o que no Core esta DESATUALIZADO e o padrao atual.

Cada item tem o motivo tecnico (nao e questao de gosto):

  Environment.OSVersion  -> no .NET 5+ mente sem manifest; usar OperatingSystem.IsWindowsVersionAtLeast
  new HttpClient()       -> 1 socket/handler por instancia; estourar portas; usar HttpClient estatico
  Encoding.ASCII/Default -> acento/mojibake em texto PT; usar UTF-8
  GC.Collect()           -> micro-freeze; so com gate
  Directory.GetFiles().Length -> enumera tudo antes de decidir; usar EnumerateFiles().Any()
  DateTime.Now           -> ok p/ UI, errado p/ log/medicao (usar UtcNow)
  Task.Run(...).Result   -> deadlock em UI thread; usar async
  new Random()           -> seed do relogio; dois em sequencia repetem a sequencia
  Thread.Abort           -> removido no .NET Core/5+
  BinaryFormatter        -> removido/desabilitado por seguranca no .NET 5+
  Assembly.LoadFrom      -> sem contexto de descarregamento
  string.Format desnecessario / string.Concat

Uso:  python3 tests/core_modern_api.py
"""
import re
from pathlib import Path

CORE = Path('KitLugia.Core')

def out(s=''):
    print(str(s).encode('ascii', 'replace').decode('ascii'))

def files():
    return sorted(p for p in CORE.rglob('*.cs')
                  if not ({'obj', 'bin', 'Resources'} & set(p.parts)))

CHECKS = [
    ('Environment.OSVersion', r'\bEnvironment\.OSVersion\b',
     'OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) — no .NET 5+ OSVersion mente sem manifest'),
    ('new HttpClient() por chamada', r'\bnew\s+HttpClient\s*\(',
     'HttpClient estatico (ou IHttpClientFactory) — evita esgotar sockets/portas'),
    ('Encoding.ASCII/Default em texto', r'Encoding\.(ASCII|Default)',
     'Encoding.UTF8 (+BOM quando o script for lido por ferramenta legada)'),
    ('GC.Collect sem gate', r'\bGC\.Collect\s*\(',
     'reaproveitar alocacoes; se realmente precisar, com gate/force e comentario'),
    ('GetFiles().Length', r'Directory\.GetFiles\([^)]*\)\s*\.Length',
     'Directory.EnumerateFiles(...).Any() — para na primeira ocorrencia'),
    ('Task.Run(...).Result / GetAwaiter', r'Task\.Run\([^;]*\)\s*\.(Result|GetAwaiter\(\))',
     'tornar o metodo async e aguardar de verdade'),
    ('new Random() repetido', r'\bnew\s+Random\s*\(\s*\)',
     'Random.Shared (thread-safe, .NET 6+) ou uma instancia por classe'),
    ('Thread.Abort', r'\bThread\.Abort\b', 'nao existe no .NET Core/5+ — cancelamento cooperativo'),
    ('BinaryFormatter', r'\bBinaryFormatter\b', 'removido no .NET 9; usar System.Text.Json/protobuf'),
    ('Assembly.LoadFrom', r'\bAssembly\.LoadFrom\b',
     'AssemblyLoadContext + LoadFromAssemblyPath (permite descarregar)'),
    ('Assembly.Load(bytes)', r'\bAssembly\.Load\s*\(', 'idem — sem contexto de descarregamento'),
    ('Process.Kill() sem dispose', r'\.Kill\(\)\s*;', 'usar using/try + WaitForExit para nao vazar handle'),
    ('DateTime.Now para medicao/log', r'\bDateTime\.Now\b', 'DateTime.UtcNow (medicao/log) — Now so p/ exibir'),
    ('Marshal.Copy manual sem necessidade', r'\bMarshal\.Copy\b',
     'fixed/Span<T>/MemoryMarshal quando possivel'),
    ('DllImport kernel32/advapi32 repetido', r'\[DllImport\("(kernel32|advapi32|ntdll|user32|shell32|gdi32)',
     'permitido, mas vale agrupar em Native* por area (Kernel32/Advapi32/Fsutil)'),
]

n_total = 0
for title, pat, why in CHECKS:
    hits = []
    for p in files():
        try:
            src = p.read_text(encoding='utf-8-sig')
        except Exception:
            continue
        for i, line in enumerate(src.splitlines(), 1):
            s = line.strip()
            if s.startswith('//') or s.startswith('///'):
                continue
            if re.search(pat, line):
                hits.append((p.as_posix(), i, s[:100]))
    if not hits:
        continue
    n_total += len(hits)
    out(f'\n[{len(hits):>3}x] {title}')
    out(f'       moderno: {why}')
    show = hits if len(hits) <= 12 else hits[:12]
    for f, i, s in show:
        out(f'         {f}:{i}  {s}')
    if len(hits) > 12:
        out(f'         ... +{len(hits) - 12}')
        seen = {}
        for f, i, s in hits:
            seen[f] = seen.get(f, 0) + 1
        out('         por arquivo: ' + ', '.join(
            f"{Path(f).name}({c})" for f, c in sorted(seen.items(), key=lambda kv: -kv[1])[:10]))

out(f'\n\nTOTAL de ocorrencias sinalizadas: {n_total}')
