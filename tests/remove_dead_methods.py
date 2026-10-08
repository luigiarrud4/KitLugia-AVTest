"""Remove um metodo morto por arquivo + assinatura, preservando UTF-8 BOM e CRLF.

Uso: python3 tests/remove_dead_methods.py <arquivo> <assinatura> [indent]
Inclui o bloco /// acima da assinatura e vai ate a primeira linha de
fechamento '}' com a mesma indentacao (default 8 espacos).
"""
import sys


def remove(path, signature, indent="        "):
    data = open(path, "rb").read()
    bom = data.startswith(b"\xef\xbb\xbf")
    lines = data.decode("utf-8-sig").split("\r\n")

    hits = [i for i, l in enumerate(lines) if signature in l]
    if len(hits) != 1:
        print("!! %-44s -> %d ocorrencias" % (signature, len(hits)))
        return 1
    i = hits[0]

    start = i
    while start - 1 >= 0 and lines[start - 1].lstrip().startswith("///"):
        start -= 1
    if start - 1 >= 0 and lines[start - 1].strip() == "":
        start -= 1

    close = indent + "}"
    end = next((k for k in range(i, len(lines)) if lines[k] == close), None)
    if end is None:
        print("!! %-44s -> sem fecha-chaves" % signature)
        return 1

    new = lines[:start] + lines[end + 1:]
    out = "\r\n".join(new).encode("utf-8")
    if bom:
        out = b"\xef\xbb\xbf" + out
    open(path, "wb").write(out)
    print("OK %-44s -> %d linhas" % (signature, end - start + 1))
    return 0


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(2)
    sys.exit(remove(sys.argv[1], sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else "        "))
