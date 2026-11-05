#!/usr/bin/env python3
"""ui_style_audit.py — tipografia e cores das paginas (F1/F7 do docs/UI_CONSISTENCY_PLAN.md).

Duas coisas, no mesmo arquivo:

  --check   (padrao) relatorio do estado: titulos fora do PageTitleStyle, subtitulos
            fora do PageSubtitleStyle, FontSize fora da escala e hex hardcoded.

  --apply   normaliza o TITULO e o SUBTITULO de cada pagina para os estilos do tema
            (PageTitleStyle / PageSubtitleStyle). Nao mexe em layout, margens nem
            em nenhum outro elemento.

Regra do titulo: primeiro TextBlock do arquivo com FontSize 28/30/32 e Text com letra
(emoji puro nao conta). Regra do subtitulo: primeiro TextBlock com FontSize 13/14/15 e
Foreground cinza (ou TextMuted/TextDescription) ate 14 linhas depois do titulo.

Preserva BOM + CRLF.

Uso: python3 tests/ui_style_audit.py [--apply] [KitLugia.GUI/Pages]
"""
import html
import re
import sys
from pathlib import Path

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

APPLY = "--apply" in sys.argv
args = [a for a in sys.argv[1:] if not a.startswith("--")]
raiz = Path(args[0]) if args else Path("KitLugia.GUI/Pages")

ESCALA = {"28", "18", "16", "13", "12", "11"}
TITULO_SIZES = {"28", "30", "32"}
SUB_SIZES = {"13", "14", "15"}
CINZAS = {"#888", "#888888", "#AAA", "#AAAAAA", "#A0A0A0", "#666", "#666666", "#999", "#999999", "#777", "#777777"}
TAG_RE = re.compile(r'^(\s*)<TextBlock\b([^>]*?)/>\s*$')
ATTR_RE = re.compile(r'(\w[\w.:]*)="([^"]*)"')
HEX_RE = re.compile(r'#[0-9A-Fa-f]{6,8}\b')
LETRA_RE = re.compile(r'[A-Za-zÀ-ÿ]')

def attrs(tag: str) -> dict:
    return {m.group(1): m.group(2) for m in ATTR_RE.finditer(tag)}

def sem(attrs_dict: dict, remover) -> str:
    return " ".join(f'{k}="{v}"' for k, v in attrs_dict.items() if k not in remover)

def eh_titulo(attrs_dict: dict) -> bool:
    if attrs_dict.get("FontSize") not in TITULO_SIZES:
        return False
    return bool(LETRA_RE.search(html.unescape(attrs_dict.get("Text", ""))))

def eh_subtitulo(attrs_dict: dict) -> bool:
    if attrs_dict.get("FontSize") not in SUB_SIZES:
        return False
    fg = attrs_dict.get("Foreground", "")
    return fg in CINZAS or "TextMuted" in fg or "TextDescription" in fg

def normalizar(linhas, idx, estilo):
    tag = linhas[idx]
    m = TAG_RE.match(tag.rstrip("\r"))
    if not m:
        return None
    ind, corpo = m.group(1), m.group(2)
    d = attrs(corpo)
    if "Style" in d:
        return None
    removidos = {"FontSize", "FontWeight", "Foreground"} if estilo == "PageTitleStyle" else {"FontSize", "Foreground"}
    novo = sem(d, removidos)
    # Style primeiro: e' o padrao do projeto (Style="{StaticResource ...}" antes de Text).
    linhas[idx] = f'{ind}<TextBlock Style="{{StaticResource {estilo}}}" {novo}/>\r\n' if tag.endswith("\r\n") else f'{ind}<TextBlock Style="{{StaticResource {estilo}}}" {novo}/>'
    return estilo

def processar(arq: Path, aplicar: bool):
    bruto = arq.read_bytes()
    texto = bruto.decode("utf-8-sig")
    linhas = texto.splitlines(keepends=True)
    mudancas = []
    feito_titulo = False
    for i, linha in enumerate(linhas):
        m = TAG_RE.match(linha.rstrip("\r\n"))
        if not m:
            continue
        d = attrs(m.group(2))
        if not feito_titulo and eh_titulo(d):
            if aplicar:
                if normalizar(linhas, i, "PageTitleStyle"):
                    mudancas.append(("titulo", d.get("Text", "")[:40]))
            else:
                if "Style" not in d:
                    mudancas.append(("titulo-off", d.get("Text", "")[:40]))
            feito_titulo = True
            continue
        if feito_titulo and eh_subtitulo(d):
            # Subtitulo NAO e' normalizado automaticamente: em varias paginas o primeiro
            # TextBlock cinza de 13-15px e' rotulo de estado ("---", "Status: -"), nao o
            # subtitulo. O check so' relata; o ajuste e' manual, pagina a pagina.
            if aplicar:
                break
            if "Style" not in d:
                mudancas.append(("subtitulo-off", d.get("Text", "")[:40]))
            break
    if aplicar and mudancas:
        saida = "".join(linhas)
        tinham_bom = bruto.startswith(b"\xef\xbb\xbf")
        bruto_novo = (b"\xef\xbb\xbf" if tinham_bom else b"") + saida.encode("utf-8")
        arq.write_bytes(bruto_novo)
    return mudancas

print(f"=== ui_style_audit {'(APPLY)' if APPLY else '(check)'} — {raiz} ===")
total = {"titulo": 0, "subtitulo": 0, "titulo-off": 0, "subtitulo-off": 0}
for f in sorted(raiz.rglob("*.xaml")):
    mud = processar(f, APPLY)
    if mud:
        print(f"  {f.relative_to(raiz)}: " + ", ".join(f"{k}={v!r}" for k, v in mud))
        for k, _ in mud:
            total[k] = total.get(k, 0) + 1

print()
print("--- resumo ---")
if APPLY:
    print(f"titulos normalizados: {total['titulo']} · subtitulos normalizados: {total['subtitulo']}")
else:
    print(f"titulos sem Style: {total['titulo-off']} · subtitulos sem Style: {total['subtitulo-off']}")

# FontSize fora da escala (todos os XAMLs de pagina)
fora = {}
for f in sorted(raiz.rglob("*.xaml")):
    src = f.read_text(encoding="utf-8-sig")
    for m in re.finditer(r'FontSize="([0-9.]+)"', src):
        if m.group(1) not in ESCALA:
            fora[m.group(1)] = fora.get(m.group(1), 0) + 1
print("FontSize fora da escala:", ", ".join(f"{k}={v}" for k, v in sorted(fora.items(), key=lambda kv: -kv[1])) or "nenhum")

# Hex hardcoded por arquivo: a allowlist do F7 nascera' deste relatorio.
hexes = {}
for f in sorted(raiz.rglob("*.xaml")):
    n = len(HEX_RE.findall(f.read_text(encoding="utf-8-sig")))
    if n:
        hexes[f.relative_to(raiz).as_posix()] = n
print(f"hex hardcoded: {sum(hexes.values())} em {len(hexes)} arquivo(s)")
for k, v in sorted(hexes.items(), key=lambda kv: -kv[1])[:5]:
    print(f"   {k}: {v}")

if not APPLY:
    violacoes = total.get("titulo-off", 0) + total.get("subtitulo-off", 0)
    print()
    print("SAIDA:", 0 if violacoes == 0 else 2, f"({violacoes} titulo(s)/subtitulo(s) fora do padrao)")
    sys.exit(0 if violacoes == 0 else 2)
