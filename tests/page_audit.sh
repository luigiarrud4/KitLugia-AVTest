#!/bin/bash
# Auditoria das paginas ALCANCAVEIS do KitLugia (convencoes de _CONVENTIONS.md).
# Read-only: apenas le e reporta.
cd "$(git rev-parse --show-toplevel)" || exit 1

PAGES_DIR="KitLugia.GUI/Pages"

# Paginas alcancaveis (UI): sidebar/toolbar + mapa NavigateToPage + busca global + cards.
REACH=(
  DashboardPage TweaksPage ScreenPage AppsPage CleanupPage NetworkPage ServicesPage
  RepairsPage DriversPage PartitionsPage TraySettingsPage IntegrityPage GameBoostPage
  DiagnosticPage WinpeToolsPage ReinstallPreservePage WinbootPage AdvancedToolsPage
  IsoEditorPage SecurityPage PrivacyPage ActivationPage UpdatePage ServerPage RufusPage
  AdvancedRamCleanSettingsPage AllTweaksPage ExmTweaksPage StutterPage WinTunePage
  QuickInstallPage ShrinkPage WindowsUpdatePage GlobalSearchPage SettingsPage
  PagesControl/ToolsPage
  WindowsSettings/WindowsPage
  WindowsSettings/StoreRemakePage
  WindowsSettings/ForceStopUnlockPage
  WindowsSettings/ContextMenuPage
  WindowsSettings/ContextMenuManagerPage
  WindowsSettings/ContextMenuAddPage
  WindowsSettings/ContextMenuPreviewPage
)

printf '%-34s %-7s %-8s %-7s %-7s %-7s %s\n' PAGINA CLEANUP UNLOADED CTORPES TIMERS DCTX OBS
for p in "${REACH[@]}"; do
  case "$p" in
    PagesControl/*) f="$PAGES_DIR/${p#PagesControl/}.xaml.cs" ;;
    WindowsSettings/*) f="$PAGES_DIR/$p.xaml.cs" ;;
    *) f="$PAGES_DIR/$p.xaml.cs" ;;
  esac
  [ -f "$f" ] || { printf '%-34s ARQUIVO AUSENTE\n' "$p"; continue; }
  obs=""

  # Cleanup() publico
  if grep -q "public void Cleanup()" "$f"; then cl="ok"; else cl="FALTA"; obs="$obs sem-Cleanup;"; fi
  # Unloaded registrado
  if grep -q "Unloaded += " "$f"; then ul="ok"; else ul="FALTA"; obs="$obs sem-Unloaded;"; fi
  # lambda em Unloaded (nao removivel)
  if grep -q "Unloaded += *(\|Unloaded += *delegate" "$f"; then obs="$obs lambda-Unloaded;"; fi
  # DataContext = null dentro do Cleanup
  if grep -q "DataContext = null" "$f"; then dc="ok"; else dc="-"; fi
  # Timers: se cria timer, precisa Stop no arquivo
  if grep -qE "DispatcherTimer|System\.Timers\.Timer|PeriodicTimer|new Timer\(" "$f"; then
    if grep -qE "\.Stop\(\)|\.Dispose\(\)|\.Cancel\(\)" "$f"; then tm="ok"; else tm="SUSP"; obs="$obs timer-sem-stop;"; fi
  else tm="-"; fi

  # Trabalho pesado no construtor: primeiro bloco { ... } do ctor
  ctor=$(awk '/public [A-Za-z]*Page\(/{flag=1} flag{print} flag&&/^        }/{exit}' "$f")
  hp=""
  echo "$ctor" | grep -qE "Task\.Run|Process\.Start|GetProcesses|ManagementObject|Directory\.GetFiles|Directory\.EnumerateFiles|Thread\.Sleep|PerformanceCounter|Registry\.(LocalMachine|CurrentUser)|File\.ReadAll" && hp="SIM"
  [ -z "$hp" ] && hp="-"

  # Caminhos absolutos hardcoded em C# (ignora docs/comentarios de exemplo)
  hc=$(grep -nE '"[A-Za-z]:\\\\|@"[A-Za-z]:\\\\|"[A-Za-z]:/' "$f" | grep -v "^\s*//" | wc -l)

  [ "$hc" -gt 0 ] && obs="$obs hardcoded($hc);"
  printf '%-34s %-7s %-8s %-7s %-7s %-7s %s\n' "$p" "$cl" "$ul" "$hp" "$tm" "$dc" "$obs"
done
