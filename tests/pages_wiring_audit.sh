#!/usr/bin/env bash
# pages_wiring_audit.sh — auditoria READ-ONLY da fiacao XAML <-> code-behind das paginas.
#
#   1. HANDLER AUSENTE  -> Click="X" (ou outro evento) sem metodo X no .xaml.cs
#   2. HANDLER ORFAO    -> metodo *_Click/... que nenhum XAML do projeto referencia
#   3. PATH HARDCODED   -> C:\Users\... literal no .cs
#   4. NOME FANTASMA    -> FindName("X") sem x:Name="X" no .xaml
#
# Uso:  bash tests/pages_wiring_audit.sh [pasta]
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIR="${1:-$ROOT/KitLugia.GUI/Pages}"
GUI="$ROOT/KitLugia.GUI"

# --- indice global (uma passada): todos os valores de atributo de evento citados em qualquer XAML
ALLREFS=$(mktemp)
grep -rhoE '(Click|Checked|Unchecked|SelectionChanged|ValueChanged|TextChanged|MouseLeftButtonDown|MouseDoubleClick|Toggled|Indeterminate|Loaded|Unloaded|Drop|KeyDown|MouseWheel|MouseDown|MouseUp|IsVisibleChanged|SizeChanged)="[A-Za-z_][A-Za-z0-9_]*"' \
    "$GUI" --include=*.xaml | sed -E 's/.*="([^"]+)".*/\1/' | sort -u > "$ALLREFS"

ALLHANDLERS=$(mktemp)   # todos os handlers definidos no projeto inteiro
grep -rhoE '(void|Task|bool|int)[[:space:]]+[A-Za-z_][A-Za-z0-9_]*[[:space:]]*\(' "$GUI" --include=*.xaml.cs \
    | sed -E 's/.*[[:space:]]([A-Za-z_][A-Za-z0-9_]*)[[:space:]]*\(.*/\1/' | sort -u > "$ALLHANDLERS"

# frequencia de cada identificador (identificadores usados >=2x nao estao orfaos)
FREQ=$(mktemp)
grep -rhoE '[A-Za-z_][A-Za-z0-9_]*' "$GUI" --include=*.xaml --include=*.xaml.cs \
    | sort | uniq -c | awk '{print $2" "$1}' | sort > "$FREQ"
count_uses() { awk -v k="$1" '$1==k{print $2; found=1} END{if(!found) print 0}' "$FREQ"; }

report=/tmp/pages_wiring_report.txt; : > "$report"
tm=0; to=0; tn=0; th=0; tpages=0

while IFS= read -r xaml; do
    cs="${xaml%.xaml}.xaml.cs"
    [ -f "$cs" ] || continue

    refs=$(grep -oE '(Click|Checked|Unchecked|SelectionChanged|ValueChanged|TextChanged|MouseLeftButtonDown|MouseDoubleClick|Toggled|Indeterminate|Loaded|Unloaded|Drop|KeyDown|MouseWheel|MouseDown|MouseUp|IsVisibleChanged|SizeChanged)="[A-Za-z_][A-Za-z0-9_]*"' "$xaml" | sed -E 's/.*="([^"]+)".*/\1/' | grep -vE '^(True|False)$' | sort -u)
    missing=""
    for r in $refs; do
        grep -qx "$r" "$ALLHANDLERS" || { missing="$missing $r"; tm=$((tm+1)); }
    done

    orphan=""
    while IFS= read -r m; do
        [ -z "$m" ] && continue
        case "$m" in Cleanup) continue;; esac
        # usado em XAML (evento/binding) OU no codigo (+= handler / chamada) >=2 vezes
        if grep -qx "$m" "$ALLREFS"; then continue; fi
        [ "$(count_uses "$m")" -ge 2 ] && continue
        orphan="$orphan $m"; to=$((to+1))
    done < <(grep -oE '(private|public|internal|protected)[[:space:]]+(async[[:space:]]+)?(void|Task|bool)[[:space:]]+[A-Za-z_][A-Za-z0-9_]*_(Click|Changed|Checked|Unchecked|Loaded|Unloaded|Down|Up|Selected|Toggled)[[:space:]]*\(' "$cs" | sed -E 's/.*[[:space:]]([A-Za-z_][A-Za-z0-9_]*)[[:space:]]*\(.*/\1/' | sort -u)

    name=""
    while IFS= read -r nm; do
        [ -z "$nm" ] && continue
        grep -q "x:Name=\"$nm\"" "$xaml" || name="$name $nm"
    done < <(grep -oE 'FindName\("[A-Za-z_][A-Za-z0-9_]*"\)' "$cs" | sed -E 's/FindName\("([^"]+)"\)/\1/' | sort -u)

    hard=$(grep -nE '[A-Za-z]:\\\\?(Users|KL_WINPE)' "$cs" | grep -vE ':[[:space:]]*//' | head -5)

    if [ -n "$missing" ] || [ -n "$orphan" ] || [ -n "$name" ] || [ -n "$hard" ]; then
        tpages=$((tpages+1))
        {
            echo "=== $(basename "$xaml")"
            [ -n "$missing" ] && echo "  [HANDLER AUSENTE]$missing"
            [ -n "$orphan" ]  && echo "  [HANDLER ORFAO]$(printf '\n   -%s' $orphan)"
            [ -n "$name" ]    && echo "  [NOME FANTASMA]$name"
            [ -n "$hard" ]    && { echo "  [PATH HARDCODED]"; echo "$hard" | sed 's/^/    /'; }
        } >> "$report"
    fi
done < <(find "$DIR" -name '*.xaml' ! -name '_PageTemplate.xaml' | sort)

echo "===== RESUMO ($tpages paginas com achado) ====="
echo "handlers ausentes : $tm"
echo "handlers orfaos   : $to"
echo ""
cat "$report"
rm -f "$ALLREFS" "$ALLHANDLERS" "$FREQ"
