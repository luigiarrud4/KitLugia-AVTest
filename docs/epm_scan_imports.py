"""Pre-scan dos binarios EaseUS WinPE (EPM 20.8).

Objetivo: mapear COMO eles operam disco/BCD antes de gastar tempo de IDA.
- Lista imports de cada PE (kernel32/ntdll/ole32/vds = mecanismo de disco).
- Extrai strings (ASCII + UTF-16) com palavras-chave de BCD/particao.
Nao modifica nada: leitura pura dos arquivos copiados em %TEMP%/epm_analysis/targets.
"""
import os
import re
import sys
import tempfile

import pefile

# Copias de trabalho (binarios ORIGINAIS ficam intocados em ~/Downloads/easeuswinpeepm).
TARGETS = os.environ.get("EPM_TARGETS", os.path.join(tempfile.gettempdir(), "epm_analysis", "targets"))

KEYWORDS = [
    # BCD / boot
    "bcd", "bcdboot", "bcdedit", "bootsect", "bootmgr", "winload", "bootmgfw",
    "efi", "esp", "msdos", "boot.sdi", "wimboot", "ramdisk", "{bootmgr}",
    "loadoptions", "identifier", "recoveryenabled", "displayorder", "bootsequence",
    # disco / particao
    "diskpart", "defrag", "vds", "storage space", "MSFT_", "diskpart.txt",
    "ioctl", "FSCTL", "DeviceIoControl", "ntfs", "resize", "shrink", "extend",
    "mbr", "gpt", "convert", "partition", "align", "4k", "cluster",
]

INTERESTING_DLLS = ("kernel32", "ntdll", "ole32", "vds", "oleaut", "setupapi", "cfgmgr", "wmi", "wbem", "advapi", "shell32", "version")


def imports(path):
    try:
        pe = pefile.PE(path, fast_load=True)
        pe.parse_data_directories(
            directories=[
                pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IMPORT"],
                pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT"],
            ]
        )
        out = {}
        for attr in ("DIRECTORY_ENTRY_IMPORT", "DIRECTORY_ENTRY_DELAY_IMPORT"):
            for entry in getattr(pe, attr, []) or []:
                dll = entry.dll.decode("ascii", "ignore")
                if any(k in dll.lower() for k in INTERESTING_DLLS):
                    names = []
                    for imp in entry.imports:
                        names.append(imp.name.decode("ascii", "ignore") if imp.name else f"ord{imp.ordinal}")
                    out[dll] = names
        pe.close()
        return out
    except Exception as ex:  # noqa: BLE001
        return {"ERRO": [str(ex)]}


def strings_scan(path, min_len=5):
    data = open(path, "rb").read()
    hits = []
    seen = set()
    # ASCII
    for m in re.finditer(rb"[\x20-\x7e]{%d,}" % min_len, data):
        s = m.group().decode("ascii", "ignore")
        low = s.lower()
        for kw in KEYWORDS:
            if kw.lower() in low and (kw, s[:80]) not in seen:
                seen.add((kw, s[:80]))
                hits.append((m.start(), "A", s[:200]))
                break
    # UTF-16LE
    for m in re.finditer(rb"(?:[\x20-\x7e]\x00){%d,}" % min_len, data):
        s = m.group().decode("utf-16le", "ignore")
        low = s.lower()
        for kw in KEYWORDS:
            if kw.lower() in low and (kw, s[:80]) not in seen:
                seen.add((kw, s[:80]))
                hits.append((m.start(), "U", s[:200]))
                break
    return sorted(hits)


def main():
    only = sys.argv[1:] or None
    for name in sorted(os.listdir(TARGETS)):
        path = os.path.join(TARGETS, name)
        if only and name not in only:
            continue
        print("=" * 100)
        print(f"### {name}  ({os.path.getsize(path)} bytes)")
        print("=" * 100)
        print("-- IMPORTS (dlls relevantes) --")
        imps = imports(path)
        for dll, names in sorted(imps.items()):
            interesting = [n for n in names if re.search(
                r"IoControl|CreateFile|WriteFile|ReadFile|SetFilePointer|CreateInstance|CoCreate|Vds|Disk|Volume|Partition|LoadLibrary|GetProcAddress|Process|ShellExecute|CreateProcess|WinExec|RegOpenKey|RegSetValue|DeviceIoControl|GetLogical|SetVolume|Dismount|Lock|FSCTL|NtDevice|CreateSymbolic|GetVolumeInformation|GetDiskFreeSpace",
                n, re.I)]
            if interesting:
                print(f"  {dll}: {', '.join(interesting[:40])}")
        print("-- STRINGS (keywords) --")
        hits = strings_scan(path)
        for off, kind, s in hits:
            print(f"  0x{off:08x} [{kind}] {s}")
        print()


if __name__ == "__main__":
    main()