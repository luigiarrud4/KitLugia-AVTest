#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Auditoria temporaria v3: membros removidos (HEAD vs working) + referencias vivas.
Extracao de nome LINEAR (sem regex aninhado - v2 travava por backtracking)."""
import re, subprocess, os
from collections import defaultdict

ROOT = subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip()
os.chdir(ROOT)

def git_show(path):
    try:
        return subprocess.check_output(["git", "show", f"HEAD:{path}"], text=True, errors="replace")
    except subprocess.CalledProcessError:
        return ""

ID_RE = re.compile(r"[A-Za-z_]\w*")
SKIP = {"get", "set", "if", "else", "return", "new", "this", "base", "class", "struct", "void"}

def member_name(s):
    """Nome do membro: ultimo identificador antes do 1o delimitador ( ( { = ; )."""
    cut = len(s)
    for d in ("(", "{", "=", ";"):
        i = s.find(d)
        if 0 <= i < cut:
            cut = i
    ids = ID_RE.findall(s[:cut])
    return ids[-1] if ids else None

def decls(text):
    names = set()
    for line in text.splitlines():
        s = line.strip()
        if not s or s.startswith(("//", "*", "/*", "#")):
            continue
        if not re.match(r"^(public|private|protected|internal)\b", s):
            continue
        n = member_name(s)
        if n and n not in SKIP:
            names.add(n)
    return names

changed = subprocess.check_output(["git", "diff", "--name-only", "--diff-filter=M", "--", "*.cs"], text=True).split()
print(f"== {len(changed)} arquivos .cs modificados ==", flush=True)

removed_per_file = {}
for path in changed:
    try:
        with open(path, "r", encoding="utf-8-sig", errors="replace") as f:
            work = f.read()
    except OSError:
        work = ""
    gone = decls(git_show(path)) - decls(work)
    if gone:
        removed_per_file[path] = sorted(gone)

total = sum(len(v) for v in removed_per_file.values())
print(f"== membros removidos: {total} em {len(removed_per_file)} arquivos ==", flush=True)
only_names = set()
for path, names in sorted(removed_per_file.items()):
    print(f"\n{path} ({len(names)}):\n  " + ", ".join(names), flush=True)
    only_names.update(names)

name_origins = defaultdict(list)
for path, names in removed_per_file.items():
    for n in names:
        name_origins[n].append(path)

names_re = re.compile(r"\b(" + "|".join(sorted(map(re.escape, name_origins), key=len, reverse=True)) + r")\b") if name_origins else None

EXCLUDE_DIRS = {".git", "bin", "obj", "tests", "docs", ".vs", "packages"}
hits = defaultdict(list)
files_scanned = 0
for dirpath, dirnames, filenames in os.walk(ROOT):
    dirnames[:] = [d for d in dirnames if d not in EXCLUDE_DIRS]
    for fn in filenames:
        if not (fn.endswith(".cs") or fn.endswith(".xaml")):
            continue
        fp = os.path.join(dirpath, fn)
        rel = os.path.relpath(fp, ROOT).replace("\\", "/")
        try:
            with open(fp, "r", encoding="utf-8-sig", errors="replace") as f:
                txt = f.read()
        except OSError:
            continue
        files_scanned += 1
        for i, line in enumerate(txt.splitlines(), 1):
            ls = line.strip()
            if ls.startswith(("//", "*", "///")):
                continue
            for m in names_re.finditer(line):
                nm = m.group(1)
                if rel in name_origins[nm]:
                    continue
                hits[nm].append(f"{rel}:{i}: {ls[:130]}")
                break

print(f"\n== REFERENCIAS VIVAS (fora do arquivo de origem), {files_scanned} arquivos varridos ==", flush=True)
if hits:
    for nm in sorted(hits):
        print(f"\n### {nm} (removido de: {', '.join(name_origins[nm])})")
        for h in hits[nm][:10]:
            print("   " + h)
        print(f"   ... {len(hits[nm])} ocorrencia(s)")
else:
    print("NENHUMA referencia viva encontrada. OK", flush=True)

print("\n== DELETADOS: referencias residuais ==", flush=True)
for cls in ["BloatwareManager", "GPEditManager", "NativeBlake3"]:
    refs = []
    for dirpath, dirnames, filenames in os.walk(ROOT):
        dirnames[:] = [d for d in dirnames if d not in EXCLUDE_DIRS]
        for fn in filenames:
            if not (fn.endswith(".cs") or fn.endswith(".xaml") or fn.endswith(".csproj")):
                continue
            fp = os.path.join(dirpath, fn)
            try:
                with open(fp, "r", encoding="utf-8-sig", errors="replace") as f:
                    t = f.read()
            except OSError:
                continue
            if cls in t:
                refs.append(os.path.relpath(fp, ROOT).replace("\\", "/"))
    print(f"{cls}: {'-> ' + ', '.join(refs) if refs else 'sem referencias'}", flush=True)
