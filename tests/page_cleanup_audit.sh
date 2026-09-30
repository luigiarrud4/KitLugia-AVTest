#!/bin/bash
# Verifica se cada pagina alcancavel para/desinscreve o que cria, DENTRO do Cleanup.
cd "$(git rev-parse --show-toplevel)" || exit 1
P=KitLugia.GUI/Pages

pages=$(git ls-files 'KitLugia.GUI/Pages/*.xaml.cs')

for f in $pages; do
  base=$(basename "$f" .xaml.cs)
  # corpo do Cleanup (ate a linha que fecha a assinatura no nivel de metodo)
  body=$(awk '/public void Cleanup\(\)/{flag=1} flag{print} flag&&/^        \}/{exit}' "$f")
  [ -z "$body" ] && continue
  # --- timers
  timers=$(grep -oE "_[A-Za-z0-9]*[Tt]imer" "$f" | sort -u)
  for t in $timers; do
    echo "$body" | grep -q "$t" || echo "$base: timer $t NAO tratado no Cleanup"
  done
  # --- eventos estaticos conhecidos
  for ev in "OnLogUpdate" "InstallMonitor.OnChange" "StutterDetector" "AudioGlitchMonitor"; do
    if grep -q "$ev" "$f"; then
      echo "$body" | grep -q "$ev" || echo "$base: evento estatico $ev NAO desinscrito no Cleanup"
    fi
  done
  # --- DataContext
  echo "$body" | grep -q "DataContext = null" || echo "$base: Cleanup sem DataContext = null"
  # --- Loaded desinscrito
  if grep -q "Loaded += " "$f"; then
    echo "$body" | grep -q "Loaded -= " || echo "$base: Cleanup nao desinscreve Loaded"
  fi
done
