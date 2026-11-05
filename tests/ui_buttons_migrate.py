#!/usr/bin/env python3
"""Troca um template de botao inline por um template do tema.

O `ui_button_audit.py` mostra quais templates sao identicos; este script aplica.
Para cada `<Button.Template>` cujo markup normalizado contenha TODOS os tokens
pedidos (e nenhum dos `--exclude`), ele:

  1. injeta `Template="{StaticResource <chave>}"` na tag `<Button ...>` que o contem;
  2. apaga o bloco `<Button.Template>...</Button.Template>`.

Assim as propriedades locais de cada botao (Width/Height/Margin/ToolTip/Content)
ficam intactas e so' o desenho passa a vir de um lugar so'.

Uso:
  py -3 tests/ui_buttons_migrate.py --file TweaksPage --key IconButtonTemplate \
      --match '<Border Background="Transparent"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>' --apply
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

RAIZ = Path(__file__).resolve().parent.parent
BLOCO = re.compile(r"<Button\.Template>(.*?)</Button\.Template>", re.S)


def normalizar(corpo: str) -> str:
    texto = re.sub(r"\s+", " ", corpo).strip()
    return re.sub(r">\s+<", "><", texto)


def migrar(caminho: Path, chave: str, tokens: list[str], excluir: list[str], aplicar: bool) -> int:
    bruto = caminho.read_bytes()
    bom = bruto.startswith(b"\xef\xbb\xbf")
    texto = bruto.decode("utf-8-sig")

    alvos = []
    for m in BLOCO.finditer(texto):
        corpo = normalizar(m.group(1))
        if all(t in corpo for t in tokens) and not any(e in corpo for e in excluir):
            alvos.append(m)

    if not alvos:
        print(f"  {caminho.name}: nenhum template casou")
        return 0

    trocados = 0
    for m in reversed(alvos):                      # de tras para frente: os indices anteriores seguem validos
        # `<Button.ToolTip>` tambem comeca com "<Button" - tem que ser tag de elemento
        # (`<Button ` ou `<Button>`), senao o atributo cai num property element (MC3057).
        achados = list(re.finditer(r"<Button[\s>]", texto[: m.start()]))
        abertura = achados[-1].start() if achados else -1
        if abertura < 0:
            print(f"  AVISO: {caminho.name}: bloco sem <Button> antes (linha aprox. {texto[:m.start()].count(chr(10)) + 1})")
            continue
        fim_tag = texto.index(">", abertura)
        if texto[fim_tag - 1] == "/":              # <Button .../> nao pode ter filhos; nao e' o caso
            print(f"  AVISO: {caminho.name}: tag auto-fechada, pulando")
            continue
        texto = texto[:fim_tag] + f' Template="{{StaticResource {chave}}}"' + texto[fim_tag:]
        # o bloco se deslocou junto com a insercao acima
        deslocamento = len(f' Template="{{StaticResource {chave}}}"')
        inicio = m.start() + deslocamento
        fim = m.end() + deslocamento
        texto = texto[:inicio] + texto[fim:]
        trocados += 1

    print(f"  {caminho.name}: {trocados} template(s) -> {chave}")
    if aplicar and trocados:
        caminho.write_text(texto, encoding="utf-8-sig" if bom else "utf-8", newline="\r\n")
    return trocados


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--file", required=True, help="nome (ou parte) do XAML em KitLugia.GUI/Pages")
    ap.add_argument("--key", required=True, help="x:Key do template no tema")
    ap.add_argument("--match", action="append", default=[], help="token que o template deve conter (repetivel)")
    ap.add_argument("--exclude", action="append", default=[], help="token que o template NAO pode conter (repetivel)")
    ap.add_argument("--apply", action="store_true", help="grava (sem isso e' so' simulacao)")
    args = ap.parse_args()

    alvos = sorted((RAIZ / "KitLugia.GUI" / "Pages").rglob("*.xaml"))
    alvos += [RAIZ / "KitLugia.GUI" / "MainWindow.xaml"]
    total = 0
    for caminho in alvos:
        if caminho.exists() and args.file.lower() in caminho.stem.lower():
            total += migrar(caminho, args.key, args.match, args.exclude, args.apply)
    print(f"total: {total}" + ("" if args.apply else " (simulacao - use --apply)"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
