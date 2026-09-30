#!/usr/bin/env bash
# normalize_crlf.sh — normaliza line endings para CRLF preservando o encoding (BOM incluso).
#
# Motivo: o .editorconfig do projeto exige end_of_line = crlf em [*], mas as
# ferramentas de edicao escrevem LF. Rodar isto depois de editar .cs/.xaml/.ps1/.cmd
# evita arquivos com line endings misturados (diff sujo e warnings no VS).
#
# Uso:  bash tests/normalize_crlf.sh <arquivo> [arquivo...]
set -euo pipefail

[ $# -ge 1 ] || { echo "uso: bash tests/normalize_crlf.sh <arquivo> [...]" >&2; exit 2; }

for f in "$@"; do
    [ -f "$f" ] || { echo "  (ignorado, nao existe) $f" >&2; continue; }
    before=$(python3 -c "import sys;print(len(open(sys.argv[1],'rb').read()))" "$f")
    python3 - "$f" <<'PY'
import sys
p = sys.argv[1]
d = open(p, 'rb').read()
d = d.replace(b'\r\n', b'\n').replace(b'\n', b'\r\n')
open(p, 'wb').write(d)
PY
    after=$(python3 -c "import sys;print(len(open(sys.argv[1],'rb').read()))" "$f")
    mixed=$(python3 - "$f" <<'PY'
import sys
d = open(sys.argv[1], 'rb').read()
lf_only = sum(1 for i, c in enumerate(d) if c == 10 and (i == 0 or d[i-1] != 13))
print(lf_only)
PY
)
    echo "  $f  (${before}B -> ${after}B, linhas LF-solto restantes: ${mixed})"
done
