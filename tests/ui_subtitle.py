#!/usr/bin/env python3
"""Normaliza o subtitulo de pagina para `PageSubtitleStyle` (F1/E6).

O titulo ja' e' `PageTitleStyle`. O subtitulo e' o PRIMEIRO TextBlock cinza logo
depois dele: recebe o estilo e perde FontSize/Foreground locais (a Margin fica,
porque cada cabecalho tem a sua). O script imprime o texto de cada candidato —
se algum for rotulo de estado, e' so' nao aplicar naquele arquivo.

Uso:
  py -3 tests/ui_subtitle.py           # relatorio
  py -3 tests/ui_subtitle.py --apply
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

RAIZ = Path(__file__).resolve().parent.parent
GUI = RAIZ / "KitLugia.GUI"
MUTED = ("#AAA", "#888", "#A0A0A0", "Gray", "TextMuted", "#999", "#777")
TITULO = 'Style="{StaticResource PageTitleStyle}"'


def tag_apos(texto: str, inicio: int) -> tuple[int, int, str] | None:
    """Primeiro <TextBlock ...> autocontido depois de `inicio`."""
    for m in re.finditer(r"<TextBlock\b[^<>]*?/>", texto[inicio:]):
        a, b = inicio + m.start(), inicio + m.end()
        tag = texto[a:b]
        tem_texto = re.search(r'\bText="', tag)
        cinza = any(c in tag for c in MUTED)
        pequeno = re.search(r'FontSize="(1[2-6])"', tag)
        if tem_texto and cinza and pequeno and "PageSubtitleStyle" not in tag:
            return a, b, tag
    return None


def elegivel(texto: str) -> bool:
    limpo = texto.strip()
    if len(limpo) < 25:
        return False
    if limpo.endswith("...") or limpo.endswith("\u2026"):
        return False
    if limpo.isupper():
        return False
    return True


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true")
    args = ap.parse_args()

    achados = 0
    for caminho in sorted((GUI / "Pages").rglob("*.xaml")):
        bruto = caminho.read_bytes()
        texto = bruto.decode("utf-8-sig")
        pos = texto.find(TITULO)
        if pos < 0:
            continue
        alvo = tag_apos(texto, pos)
        if not alvo:
            continue
        a, b, tag = alvo
        rotulo = re.search(r'\bText="([^"]{0,120})', tag)
        texto_rotulo = rotulo.group(1) if rotulo else ""
        # Rotulo de estado ("Pronto.", "Carregando...", "PROTECAO ATUAL") nao e' subtitulo.
        # Cabecalho de verdade fica colado no titulo; descricao do PRIMEIRO item de uma
        # lista longa (Tweaks/ExmTweaks) esta centenas de caracteres depois.
        if a - pos > 1200:
            print(f"  -- {caminho.name:30s} {texto_rotulo[:50]}  (descricao de item, mantido)")
            continue
        if not elegivel(texto_rotulo):
            print(f"  -- {caminho.name:30s} {texto_rotulo[:60]}  (rotulo, mantido)")
            continue
        print(f"  {caminho.name:32s} {texto_rotulo}")
        if not args.apply:
            continue
        nova = re.sub(r'\s+FontSize="1[2-6]"', "", tag)
        nova = re.sub(r'\s+Foreground="[^"]*"', "", nova)
        nova = nova.replace("<TextBlock", '<TextBlock Style="{StaticResource PageSubtitleStyle}"', 1)
        texto = texto[:a] + nova + texto[b:]
        bom = bruto.startswith(b"\xef\xbb\xbf")
        caminho.write_bytes((b"\xef\xbb\xbf" if bom else b"") + texto.encode("utf-8"))
        achados += 1

    print(f"\n{achados if args.apply else 'candidatos: ' + str(sum(1 for _ in (GUI / 'Pages').rglob('*.xaml'))) }"
          + ("" if args.apply else "  [relatorio - use --apply]"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
