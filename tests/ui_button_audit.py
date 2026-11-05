#!/usr/bin/env python3
"""Agrupa os <Button.Template> inline do KitLugia por semelhanca.

A troca de ~250 templates inline por estilos nomeados so' e' viavel se soubermos
quantos sao REALMENTE distintos: 96 copias iguais sao uma edicao. Este script
extrai cada bloco, calcula uma impressao digital (cores + raio + espessura +
padding/altura) e imprime os grupos por tamanho.

Uso:
  py -3 tests/ui_button_audit.py                 # resumo por arquivo
  py -3 tests/ui_button_audit.py --groups        # grupos com o markup de exemplo
  py -3 tests/ui_button_audit.py --file TweaksPage
"""
from __future__ import annotations

import argparse
import collections
import re
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

RAIZ = Path(__file__).resolve().parent.parent
GUI = RAIZ / "KitLugia.GUI"

BLOCO = re.compile(r"<Button\.Template>(.*?)</Button\.Template>", re.S)
HEX = re.compile(r"#[0-9A-Fa-f]{6,8}")
RAIO = re.compile(r'CornerRadius="(\d+)"')
ALTURA = re.compile(r'(?:Height|MinHeight|Padding)="([^"]+)"')


def blocos(caminho: Path):
    texto = caminho.read_text(encoding="utf-8-sig", errors="replace")
    for m in BLOCO.finditer(texto):
        corpo = m.group(1)
        # normaliza espaco para comparar markup que so' difere na indentacao
        normalizado = re.sub(r"\s+", " ", corpo).strip()
        normalizado = re.sub(r">\s+<", "><", normalizado)
        cores = tuple(sorted({h.upper() for h in HEX.findall(corpo)}))
        raios = tuple(sorted({r for r in RAIO.findall(corpo)}))
        medidas = tuple(sorted({a for a in ALTURA.findall(corpo)}))
        linha = texto[: m.start()].count("\n") + 1
        yield linha, normalizado, cores, raios, medidas


def arquivos():
    alvos = sorted((GUI / "Pages").rglob("*.xaml"))
    alvos.append(GUI / "MainWindow.xaml")
    for a in alvos:
        if a.exists():
            yield a


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--groups", action="store_true", help="imprime os grupos com exemplo de markup")
    ap.add_argument("--file", help="limita a um arquivo (sem extensao)")
    args = ap.parse_args()

    por_arquivo: dict[str, list] = {}
    grupos: dict[tuple, list] = collections.defaultdict(list)

    for arq in arquivos():
        if args.file and args.file.lower() not in arq.stem.lower():
            continue
        itens = list(blocos(arq))
        if not itens:
            continue
        por_arquivo[arq.name] = itens
        for linha, markup, cores, raios, medidas in itens:
            grupos[(markup, cores, raios, medidas)].append(f"{arq.name}:{linha}")

    total = sum(len(v) for v in por_arquivo.values())
    print(f"=== Button.Template inline: {total} em {len(por_arquivo)} arquivo(s); "
          f"{len(grupos)} grupo(s) distintos ===\n")

    for nome, itens in sorted(por_arquivo.items(), key=lambda kv: -len(kv[1])):
        print(f"  {nome:38s} {len(itens):4d}")

    ordenados = sorted(grupos.items(), key=lambda kv: -len(kv[1]))
    print("\n-- grupos (tamanho, arquivos, assinatura) --")
    for (markup, cores, raios, medidas), onde in ordenados:
        assinatura = f"cores={','.join(cores) or '-'} raio={','.join(raios) or '-'} medida={','.join(medidas) or '-'}"
        arquivos_grupo = sorted({o.split(":")[0] for o in onde})
        print(f"\n  x{len(onde):3d}  {assinatura}")
        print(f"         arquivos: {', '.join(arquivos_grupo)}")
        print(f"         linhas:   {', '.join(onde[:6])}{' ...' if len(onde) > 6 else ''}")
        if args.groups:
            print("         markup:   " + (markup[:260] + "..." if len(markup) > 260 else markup))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
