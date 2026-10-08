"""IDA Pro (batch) - dumper de funcoes por NOME e por XREF de string.

Uso (Git Bash):
  cmd /c "C:\\...\\idat.exe" -A -S"dump.py out.txt REGEX" binario.dll

Argumentos (idc.ARGV):
  1 = arquivo de saida
  2 = regex (case-insensitive) para nomes de funcao
  3 = regex opcional (case-insensitive) para STRINGS: decompila quem referencia

Quirks do IDA 9 (aprendizados em sessoes anteriores):
  - ida_auto.auto_wait() e OBRIGATORIO no inicio (sem ele o decompiler so
    desmonta 1 instrucao e o corpo vira JUMPOUT).
  - apague o .i64 antes de reanalisar o mesmo arquivo.
"""
import re
import sys

import ida_auto
import ida_funcs
import ida_hexrays
import ida_kernwin
import ida_nalt
import ida_name
import idaapi
import idautils
import idc

OUT = idc.ARGV[1] if len(idc.ARGV) > 1 else "dump.txt"
RE_FUNC = re.compile(idc.ARGV[2] if len(idc.ARGV) > 2 else r"BCD|Boot|Repair|Resize|EFI|Mbr|Gpt", re.I)
RE_STR = re.compile(idc.ARGV[3] if len(idc.ARGV) > 3 else r".*", re.I)

MAX_DECOMPILE = int(idc.ARGV[4]) if len(idc.ARGV) > 4 else 25

lines = []


def w(s=""):
    lines.append(str(s))


def decompile(ea):
    try:
        cfunc = ida_hexrays.decompile(ea)
        if not cfunc:
            return None
        return str(cfunc)
    except Exception as ex:  # noqa: BLE001
        return f"/* decompile falhou: {ex} */"


def func_name(ea):
    return ida_name.get_name(ea) or f"sub_{ea:X}"


def main():
    ida_auto.auto_wait()
    input_name = ida_nalt.get_input_file_path()

    w("=" * 100)
    w(f"ARQUIVO: {input_name}")
    w(f"RE_FUNC: {RE_FUNC.pattern}   RE_STR: {RE_STR.pattern}")
    w("=" * 100)

    # 1) funcoes cujo NOME casa
    by_name = []
    for ea in idautils.Functions():
        nm = ida_funcs.get_func_name(ea)
        if nm and RE_FUNC.search(nm):
            by_name.append((ea, nm))
    w(f"[1] funcoes por NOME: {len(by_name)}")

    # 2) funcoes que REFERENCIAM strings que casam
    hits_str = {}
    for s in idautils.Strings():
        try:
            txt = str(s)
        except Exception:  # noqa: BLE001
            continue
        if not RE_STR.search(txt):
            continue
        for xref in idautils.XrefsTo(s.ea):
            f = ida_funcs.get_func(xref.frm)
            if f and f.start_ea not in hits_str:
                hits_str[f.start_ea] = (ida_funcs.get_func_name(f.start_ea), txt)
    w(f"[2] funcoes por XREF de string: {len(hits_str)}")
    w("-" * 100)
    for ea, (nm, txt) in sorted(hits_str.items(), key=lambda kv: kv[0]):
        w(f"  0x{ea:X} {nm}   <- \"{txt[:90]}\"")
    w("-" * 100)

    # 3) decompile os alvos
    targets = []
    for ea, nm in by_name:
        targets.append((ea, nm, "NOME"))
    for ea, (nm, txt) in hits_str.items():
        targets.append((ea, nm, f"XREF({txt[:40]})"))

    done = set()
    n = 0
    for ea, nm, why in targets:
        if ea in done or n >= MAX_DECOMPILE:
            continue
        done.add(ea)
        n += 1
        w("")
        w("#" * 100)
        w(f"### {nm}   [0x{ea:X}]   ({why})")
        w("#" * 100)
        code = decompile(ea)
        w(code if code else "(sem corpo decompilavel)")

    open(OUT, "w", encoding="utf-8", errors="replace").write("\n".join(lines))
    print(f"[dump] {n} funcoes -> {OUT}")


try:
    main()
except Exception as ex:  # noqa: BLE001
    import traceback
    open(OUT, "a", encoding="utf-8", errors="replace").write(
        "\n\n!!! ERRO NO SCRIPT !!!\n" + traceback.format_exc())
    print("ERRO:", ex)
finally:
    idaapi.qexit(0)