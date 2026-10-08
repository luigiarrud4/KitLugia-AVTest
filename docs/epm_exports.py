"""Demangle + agrupa os EXPORTS dos binarios EaseUS (EPM 20.8) por classe.

Os .dll/.mo/.exe do EPM exportam a API interna em nomes MSVC-mangled
(??0CResizeNtfsVolume@@QEAA@XZ = construtor de CResizeNtfsVolume). Sao esses
nomes que revelam a arquitetura sem precisar de IDA.

Uso:
    python docs/epm_exports.py                        # mapa completo
    python docs/epm_exports.py ToolBox.dll ResizeNTFS # so alguns modulos

Entrada: %TEMP%/epm_analysis/targets (copias de trabalho).
"""
import os
import re
import sys
import tempfile

import pefile

TARGETS = os.environ.get(
    "EPM_TARGETS", os.path.join(tempfile.gettempdir(), "epm_analysis", "targets")
)

# Prefixos de template do MSVC que nao sao API real.
NOISE = re.compile(r"^(IObject|IStorageDevice|IPartitionDevice|IDiskDevice|"
                   r"CSimpleArray|MyTemplate|tag[A-Z]|\?_)")
CTOR = re.compile(r"^\?\?0?([A-Za-z_][A-Za-z0-9_]*)@@")
METH2 = re.compile(r"^\?([A-Za-z_][A-Za-z0-9_]*)@([A-Za-z_][A-Za-z0-9_]*)@")
METH1 = re.compile(r"^\?([A-Za-z_][A-Za-z0-9_]*)@")


def split_name(tok):
    """Devolve (classe, metodo) a partir de um export mangled."""
    m = CTOR.match(tok)
    if m:
        return m.group(1), "<ctor>"
    m = METH2.match(tok)
    if m:
        return m.group(2), m.group(1)
    m = METH1.match(tok)
    if m:
        return m.group(1), None
    return None, None


def exports(path):
    pe = pefile.PE(path, fast_load=True)
    pe.parse_data_directories(
        directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_EXPORT"]]
    )
    out = []
    d = getattr(pe, "DIRECTORY_ENTRY_EXPORT", None)
    if d:
        for s in d.symbols:
            if s.name:
                out.append(s.name.decode("ascii", "ignore"))
    pe.close()
    return out


def main():
    want = sys.argv[1:]
    total = 0
    for name in sorted(os.listdir(TARGETS)):
        if not name.lower().endswith((".dll", ".exe", ".mo")):
            continue
        if want and name not in want:
            continue
        toks = exports(os.path.join(TARGETS, name))
        total += len(toks)
        classes, cfuncs = {}, []
        for t in toks:
            c, meth = split_name(t)
            if c is None:
                if not t.startswith("?") and not NOISE.match(t):
                    cfuncs.append(t)
                continue
            if NOISE.match(c):
                continue
            classes.setdefault(c, set()).add(meth)
        if not classes and not cfuncs:
            continue
        print("=" * 88)
        print("### %s   (%d exports, %d classes)" % (name, len(toks), len(classes)))
        for c in sorted(classes):
            meths = sorted(m for m in classes[c] if m and m != "<ctor>")
            print("  %s%s: %s" % (c, " [ctor]" if "<ctor>" in classes[c] else "",
                                  ", ".join(meths)))
        if cfuncs:
            print("  [C/estaticas]: " + ", ".join(sorted(set(cfuncs))))
    print("\nTOTAL de exports em todos os modulos:", total)


if __name__ == "__main__":
    main()
