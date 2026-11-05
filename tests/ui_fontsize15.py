#!/usr/bin/env python3
"""Fecha a escala tipografica: decide cada `FontSize="15"` restante.

Regra do contrato (docs/UI_CONSISTENCY_PLAN.md, sec. 2): 15 nao existe. O que era
15 vira **16** quando o texto e' titulo de bloco/secao (negrito ou branco) e **13**
quando e' corpo. O script reporta a decisao de cada linha antes de gravar, para a
troca poder ser revista sem abrir 40 arquivos.

Uso:
  py -3 tests/ui_fontsize15.py            # so' relatorio
  py -3 tests/ui_fontsize15.py --apply    # grava
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

RAIZ = Path(__file__).resolve().parent.parent
GUI = RAIZ / "KitLugia.GUI"
FONTE = re.compile(r'FontSize="15"')
# A tag inteira em que o atributo aparece (para ler FontWeight/Foreground vizinhos).
TAG = re.compile(r"<[^<>]*?FontSize=\"15\"[^<>]*?>")


def papel(tag: str) -> tuple[str, str]:
    forte = re.search(r'FontWeight="(Bold|SemiBold|ExtraBold|Black)"', tag)
    branco = re.search(r'Foreground="(White|#FFF|#FFFFFF)"', tag, re.I)
    if forte or branco:
        return "16", "titulo"
    return "13", "corpo"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true")
    args = ap.parse_args()

    total = titulos = 0
    for caminho in sorted((GUI / "Pages").rglob("*.xaml")):
        bruto = caminho.read_bytes()
        texto = bruto.decode("utf-8-sig")
        if 'FontSize="15"' not in texto:
            continue
        bom = bruto.startswith(b"\xef\xbb\xbf")

        def troca(m: re.Match) -> str:
            nao_local[0] += 1
            destino, tipo = papel(m.group(0))
            if tipo == "titulo":
                nao_local[1] += 1
            return m.group(0).replace('FontSize="15"', f'FontSize="{destino}"')

        nao_local = [0, 0]
        novo = TAG.sub(troca, texto)
        total += nao_local[0]
        titulos += nao_local[1]
        print(f"  {caminho.name:34s} {nao_local[0]:3d} ocorrencia(s), {nao_local[1]:3d} como titulo")
        if args.apply:
            caminho.write_bytes((b"\xef\xbb\xbf" if bom else b"") + novo.encode("utf-8"))

    print(f"\ntotal: {total}  (16 titulo: {titulos} | 13 corpo: {total - titulos})"
          + ("" if args.apply else "   [relatorio - use --apply]"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
