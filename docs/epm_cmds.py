"""Mostra TODAS as strings de comando (shell-out) que o EaseUS executa.

Responde objetivamente: "o EaseUS usa diskpart? bcdedit? bootsect? manage-bde?".
Cada linha e uma string literal do binario que vira linha de comando.

Uso:
    python docs/epm_cmds.py            # todas as strings de comando por modulo
    python docs/epm_cmds.py --diskpart # so o que tem a ver com diskpart
"""
import os
import re
import sys
import tempfile

TARGETS = os.environ.get(
    "EPM_TARGETS", os.path.join(tempfile.gettempdir(), "epm_analysis", "targets")
)

CMD = re.compile(
    r"\.(exe|dll)\b|diskpart|attributes disk|bcdedit|bcdboot|bootsect|manage-bde|"
    r"wmic|powershell|chkdsk|defrag|fsutil|vssadmin|robocopy|xcopy|Unlock-BitLocker",
    re.I,
)
DISKPART = re.compile(r"diskpart|attributes disk|^(select|assign|create|delete|"
                      r"convert|online|offline|merge|resize|format|clean) ", re.I)
DOTDLL = re.compile(r"^[A-Za-z0-9_\-]+\.(dll|exe)$", re.I)
BSL = chr(92)

NOISE = ("qt5", "api-ms", "vcruntime", "msvcp", "ucrtbase", "opengl",
         "d3dcompiler", "concrt", "msvcp140")


def main():
    only_diskpart = "--diskpart" in sys.argv
    rx = DISKPART if only_diskpart else CMD
    for name in sorted(os.listdir(TARGETS)):
        if not name.lower().endswith((".dll", ".exe", ".mo")):
            continue
        data = open(os.path.join(TARGETS, name), "rb").read()
        hits = set()
        for pat, enc in [(rb"[\x20-\x7e]{4,}", "ascii"),
                         (rb"(?:[\x20-\x7e]\x00){4,}", "utf-16le")]:
            for m in re.finditer(pat, data):
                s = m.group().decode(enc, "ignore").strip()
                if not (4 < len(s) < 200):
                    continue
                if not rx.search(s) or s.startswith(".?AV"):
                    continue
                if any(n in s.lower() for n in NOISE):
                    continue
                if DOTDLL.match(s) and BSL not in s and "/" not in s:
                    continue
                hits.add(s)
        if hits:
            print("### " + name)
            for s in sorted(hits):
                print("   " + s)
            print()


if __name__ == "__main__":
    main()
