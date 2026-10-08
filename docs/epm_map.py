"""Mapeador completo dos binarios EaseUS WinPE (EPM 20.8) — um passe, sem re-analise.

Para cada PE: EXPORTS (a API limpa que o binario expoe), IMPORTS por mecanismo
(disco/boot/registro/processo) e STRINGS agrupadas por feature da UI do EPM.

Uso:
    python docs/epm_map.py                      # todos os alvos
    python docs/epm_map.py PartitionManager.dll # so um
    python docs/epm_map.py --exports-only Win32UEFI.dll

Os ORIGINAIS ficam intocados em ~/Downloads/easeuswinpeepm/epm; aqui so le-se
as copias de trabalho em %TEMP%/epm_analysis/targets (EPM_TARGETS).
"""
import os
import re
import sys
import tempfile

import pefile

TARGETS = os.environ.get(
    "EPM_TARGETS", os.path.join(tempfile.gettempdir(), "epm_analysis", "targets")
)

# ---------------------------------------------------------------- categorias
# Cada feature da UI do EaseUS -> termos que revelam a implementacao.
CATEGORIES = {
    "MBR/GPT": [
        "mbr", "gpt", "partition style", "convertstyle", "protective", "msdos",
        "55aa", "boot record", "boot sector", "partition table", "disk signature",
        "initdisk", "0x55", "0xaa",
    ],
    "Boot/BCD": [
        "bcd", "bcdboot", "bcdedit", "bootsect", "bootmgr", "winload",
        "bootmgfw", "boot.sdi", "wimboot", "ramdisk", "loadoptions",
        "recoveryenabled", "displayorder", "bootsequence", "firmware",
        "bootorder", "bootnext", "boot####", "winload.efi", "bcdboot",
    ],
    "Resize/mover": [
        "resize", "shrink", "extend", "expand", "movestart", "move start",
        "cluster", "mft", "$bitmap", "$boot", "sector size", "align",
        "unallocated", "contiguous", "space", "defrag",
    ],
    "Clone/copia": [
        "clone", "mirror", "sector by sector", "rawcopy", "copy sector",
        "vhd", "vmdk", "img", "source disk", "target disk", "sizepolicy",
    ],
    "Check/setor ruim": [
        "bad sector", "badsector", "surface scan", "chkdsk", "sector scan",
        "winchkdsk", "read error", "uncorrectable", "media error",
        "low level format", "llf", "zero fill", "fill pattern",
    ],
    "BitLocker": [
        "bitlocker", "recovery key", "manage-bde", "protector", "aes",
        "xex", "fve", "lock", "unlock",
    ],
    "Filesystem/format": [
        "format", "ntfs", "fat32", "exfat", "ext2", "ext4", "refs", "convert fat",
        "quick", "label", "volume serial",
    ],
    "Dinamico/LDM": [
        "ldm", "dynamic disk", "mirror", "span", "raid", "dg", "gpt attribute",
        "basic disk",
    ],
    "Particao/partinfo": [
        "partition", "drive letter", "volume", "hidden", "system partition",
        "active", "offset", "length", "start sector", "disk layout",
    ],
    "Registro": [
        "hklm", "hkcu", "regopen", "regset", "currentcontrolset", "services",
        "mountmgr", "system\\current", "software\\microsoft",
    ],
    "Driver/inject": [
        "add driver", "devcon", "setupdi", "pnputil", "inf", "sys", "\\device\\",
        "driver", "inject",
    ],
    "Recuperacao/undelete": [
        "recover", "undelete", "scan sector", "deleted", "signature", "lost partition",
        "file carving", "raw",
    ],
}

MECHANISM_IMPORT_RE = re.compile(
    r"IoControl|CreateFile|WriteFile|ReadFile|SetFilePointer|SetFilePointerEx|"
    r"CreateInstance|CoCreate|CoInitialize|Vds|Disk|Volume|Partition|LoadLibrary|"
    r"GetProcAddress|CreateProcess|ShellExecute|WinExec|RegOpenKey|RegSetValue|"
    r"GetLogicalDrive|SetVolume|Dismount|LockVolume|FSCTL|NtDevice|"
    r"GetVolumeInformation|GetDiskFreeSpace|SetPartitionAttributes|"
    r"GetFirmwareEnvironment|SetFirmwareEnvironment|CreateSymbolicLink|"
    r"FindFirstVolume|GetVolumePathName|DeviceIoControl",
    re.I,
)

MECHANISM_DLLS = (
    "kernel32", "ntdll", "ole32", "vds", "oleaut", "setupapi", "cfgmgr",
    "wmi", "wbem", "advapi", "shell32", "version", "iphlpapi", "netapi",
)

# NAO filtrar "??" — os nomes MSVC-mangled sao justamente a API limpa do EPM
# (ex.: ??0CResizeNtfsVolume@@... revela a classe CResizeNtfsVolume).
IGNORE_EXPORTS = re.compile(r"^(__|\$|Qt[0-9])")


def pe_exports(path):
    out = []
    try:
        pe = pefile.PE(path, fast_load=True)
        pe.parse_data_directories(
            directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_EXPORT"]]
        )
        d = getattr(pe, "DIRECTORY_ENTRY_EXPORT", None)
        if d:
            for s in d.symbols:
                if not s.name:
                    continue
                nm = s.name.decode("ascii", "ignore")
                if IGNORE_EXPORTS.match(nm):
                    continue
                out.append(nm)
        pe.close()
    except Exception as ex:  # noqa: BLE001
        return [f"<erro: {ex}>"]
    return sorted(set(out))


def pe_imports(path):
    out = {}
    try:
        pe = pefile.PE(path, fast_load=True)
        pe.parse_data_directories(
            directories=[
                pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IMPORT"],
                pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT"],
            ]
        )
        for attr in ("DIRECTORY_ENTRY_IMPORT", "DIRECTORY_ENTRY_DELAY_IMPORT"):
            for entry in getattr(pe, attr, []) or []:
                dll = entry.dll.decode("ascii", "ignore")
                if not any(k in dll.lower() for k in MECHANISM_DLLS):
                    continue
                names = [
                    (i.name.decode("ascii", "ignore") if i.name else f"ord{i.ordinal}")
                    for i in entry.imports
                ]
                keep = [n for n in names if MECHANISM_IMPORT_RE.search(n)]
                if keep:
                    out.setdefault(dll, set()).update(keep)
        pe.close()
    except Exception as ex:  # noqa: BLE001
        return {"ERRO": {str(ex)}}
    return {k: sorted(v) for k, v in out.items()}


def pe_strings(path, min_len=5):
    data = open(path, "rb").read()
    ascii_hits = {}
    utf16_hits = {}
    for m in re.finditer(rb"[\x20-\x7e]{%d,}" % min_len, data):
        s = m.group().decode("ascii", "ignore")
        low = s.lower()
        for cat, terms in CATEGORIES.items():
            for t in terms:
                if t in low:
                    ascii_hits.setdefault(cat, set()).add(s.strip()[:180])
                    break
    for m in re.finditer(rb"(?:[\x20-\x7e]\x00){%d,}" % min_len, data):
        s = m.group().decode("utf-16le", "ignore")
        low = s.lower()
        for cat, terms in CATEGORIES.items():
            for t in terms:
                if t in low:
                    utf16_hits.setdefault(cat, set()).add(s.strip()[:180])
                    break
    merged = {}
    for cat in CATEGORIES:
        vals = set(ascii_hits.get(cat, set())) | set(utf16_hits.get(cat, set()))
        if vals:
            merged[cat] = sorted(vals)
    return merged


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    exports_only = "--exports-only" in sys.argv
    names = sorted(
        f for f in os.listdir(TARGETS)
        if f.lower().endswith((".dll", ".exe", ".mo"))
    )
    for name in names:
        if args and name not in args:
            continue
        path = os.path.join(TARGETS, name)
        print("=" * 100)
        print(f"### {name}   ({os.path.getsize(path):,} bytes)")
        print("=" * 100)

        exp = pe_exports(path)
        if exp:
            print(f"-- EXPORTS ({len(exp)}) --")
            for i in range(0, len(exp), 6):
                print("   " + " | ".join(exp[i:i + 6]))
        if exports_only:
            print()
            continue

        imps = pe_imports(path)
        if imps:
            print("-- IMPORTS (mecanismo) --")
            for dll, names_i in sorted(imps.items()):
                print(f"   {dll}: {', '.join(names_i[:30])}")

        cat_map = pe_strings(path)
        print("-- STRINGS por categoria --")
        for cat in CATEGORIES:
            # remove ruido: strings muito genericas
            vals = [v for v in cat_map.get(cat, []) if len(v) > 6]
            if not vals:
                continue
            print(f"   [{cat}] {len(vals)}")
            for v in vals[:26]:
                print(f"      {v}")
            if len(vals) > 26:
                print(f"      ... (+{len(vals) - 26} mais)")
        print()


if __name__ == "__main__":
    main()
