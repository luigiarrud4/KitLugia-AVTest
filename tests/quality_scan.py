"""Diagnostico rapido: catches vazios, GC.Collect, Kill sem dispose. Read-only."""
import os
import re

ROOT = "."
SKIP = ("/bin/", "/obj/", "/.git/")
files = []
for dp, dn, fn in os.walk(ROOT):
    dn[:] = [d for d in dn if d not in ("bin", "obj", ".git")]
    for f in fn:
        if f.endswith(".cs"):
            files.append(os.path.join(dp, f).replace("\\", "/"))

# catch vazio: 'catch { }' / 'catch { }  ' ou bloco catch cujo corpo (linhas) e vazio
empty_catch = re.compile(r"catch\s*(\{|\r?\n\s*\{)\s*(\}|\r?\n\s*\})")
counts = {}
lines_count = 0
for p in files:
    txt = open(p, "rb").read().decode("utf-8-sig", "replace")
    m = empty_catch.findall(txt)
    if m:
        counts[p] = len(m)
        lines_count += len(m)
print("== catch totalmente vazios:", lines_count)
for p, c in sorted(counts.items(), key=lambda x: -x[1])[:20]:
    print("  %3d  %s" % (c, p))

print()
print("== GC.Collect:")
for p in files:
    for i, l in enumerate(open(p, "rb").read().decode("utf-8-sig", "replace").split("\r\n"), 1):
        if "GC.Collect" in l:
            print("  %s:%d  %s" % (p, i, l.strip()[:110]))

print()
print("== .Kill( sem using/dispose visivel na mesma linha de inicio:")
for p in files:
    txt = open(p, "rb").read().decode("utf-8-sig", "replace").split("\r\n")
    for i, l in enumerate(txt, 1):
        if ".Kill(" in l:
            print("  %s:%d  %s" % (p, i, l.strip()[:110]))

print()
print("== referencias a IntegrityCheckManager / IntegrityPage:")
for p in files:
    txt = open(p, "rb").read().decode("utf-8-sig", "replace")
    if "IntegrityCheckManager" in txt or "IntegrityCheck" in txt:
        print("  ", p)
