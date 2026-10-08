"""Extrai os caminhos de FONTE vazados pelos binarios EaseUS (assert/__FILE__/PDB).

Os binarios do EPM carregam os caminhos dos .cpp/.pdb originais (release build
com asserts). Isso reconstrói a arquitetura interna sem reversing: 177 arquivos
em 37 diretorios.

Uso:
    python docs/epm_srcmap.py             # agrupa por diretorio
    python docs/epm_srcmap.py --raw       # lista plana "caminho<TAB>modulo"
"""
import os
import re
import sys
import tempfile

TARGETS = os.environ.get(
    "EPM_TARGETS", os.path.join(tempfile.gettempdir(), "epm_analysis", "targets")
)

SRC = re.compile(
    rb"[A-Za-z]:[\\/][\x20-\x7e]{2,150}?\.(?:cpp|c|h|hpp|pdb|asm|rc|qml)", re.I)


def main():
    raw = "--raw" in sys.argv
    found = {}
    for name in sorted(os.listdir(TARGETS)):
        if not name.lower().endswith((".dll", ".exe", ".mo")):
            continue
        data = open(os.path.join(TARGETS, name), "rb").read()
        for m in SRC.finditer(data):
            found.setdefault(m.group().decode("ascii", "ignore").strip(),
                             set()).add(name)
    if raw:
        for s in sorted(found):
            print("%s\t%s" % (s, ",".join(sorted(found[s]))))
        return
    by_dir = {}
    for s, mods in found.items():
        by_dir.setdefault(os.path.dirname(s).lower(), set()).add(
            (os.path.basename(s), tuple(sorted(mods))))
    print("TOTAL de caminhos de fonte: %d em %d diretorios\n"
          % (len(found), len(by_dir)))
    for d in sorted(by_dir):
        print("## %s   (%d arquivos)" % (d, len(by_dir[d])))
        for base, mods in sorted(by_dir[d]):
            print("   %-44s %s" % (base, ",".join(mods)))
        print()


if __name__ == "__main__":
    main()
