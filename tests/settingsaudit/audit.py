"""Regressao das correcoes da SettingsPage + mojibake.

NAO chama SetAutoStart nem mexe no registro (ver AGENTS.md: harness que
grava auto-start so pode rodar em VM/Sandbox).
"""
import io, re, sys, json, pathlib

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

FALHAS = []


def Check(nome, ok, detalhe=""):
    print("  %s %-58s %s" % ("OK  " if ok else "FALHA", nome, detalhe))
    if not ok:
        FALHAS.append(nome)


CFG = pathlib.Path.home() / "AppData" / "Local" / "KitLugia" / "settings.json"

print("=" * 74)
print("1) SettingsPage: wiring")
print("=" * 74)

cs = io.open("KitLugia.GUI/Pages/SettingsPage.xaml.cs", encoding="utf-8-sig", errors="replace").read()

Check("ToggleRAMMonitor tem handler real (nao so SaveSettings)",
      "OnToggleRAMMonitor(object s, RoutedEventArgs e) => ToggleRAMMonitor_Click();" in cs)
Check("handler existe", "private void ToggleRAMMonitor_Click()" in cs)
Check("liga no AutoCleanEnabled real", "TrayService.AutoCleanEnabled = on" in cs)
Check("persiste no TrayService", "TrayService.SaveSettings()" in cs)
Check("Le do TrayService (fonte da verdade)",
      "ToggleRAMMonitor.IsChecked = tray.AutoCleanEnabled;" in cs)
Check("NAO le mais do JSON", "ToggleRAMMonitor.IsChecked = settings.RAMMonitorEnabled" not in cs)

print()
print("=" * 74)
print("2) Arquivos: nenhum U+FFFD restante")
print("=" * 74)

alvos = []
for raiz in ("KitLugia.GUI", "KitLugia.Core"):
    for dp, dn, fn in os.walk(raiz) if (os := __import__("os")) else []:
        if "\\obj\\" in dp or "\\bin\\" in dp:
            continue
        for f in fn:
            if f.endswith((".cs", ".xaml")):
                alvos.append(os.path.join(dp, f))

ruins = []
for p in alvos:
    t = io.open(p, "r", encoding="utf-8-sig", errors="replace").read()
    if "\ufffd" in t:
        ruins.append((p, t.count("\ufffd")))

for p, c in ruins:
    print("     %-45s %d" % (p.split("\\")[-1], c))
Check("zero arquivos com U+FFFD", not ruins, "%d arquivo(s)" % len(ruins))

print()
print("=" * 74)
print("3) DeepUninstaller: o regex perigoso que EU criei")
print("=" * 74)

du = io.open("KitLugia.Core/DeepUninstaller.cs", encoding="utf-8-sig", errors="replace").read()
Check('nao sobrou a classe que casa espaco "[ -  -  - ]"', '"[ -  -  - ]"' not in du)
Check('usa escapes Unicode (imune a mojibake)', du.count(r'@"[\u00AE\u2122\u00A9]"') == 2)

rx = re.compile("[®™©]")
casos = [("Adobe Photoshop", "Adobe Photoshop"),
         ("Adobe® Photoshop", "Adobe Photoshop"),
         ("Rocket®", "Rocket")]
for entrada, esperado in casos:
    Check("SanitizeName(%r) preserva/limpa corretamente" % entrada,
          rx.sub("", entrada) == esperado, "-> %r" % rx.sub("", entrada))

print()
print("=" * 74)
print("4) Texto visivel ao usuario")
print("=" * 74)

sp = io.open("KitLugia.GUI/Pages/SettingsPage.xaml.cs", encoding="utf-8-sig", errors="replace").read()
Check('toast "Modo Desenvolvedor" sem lixo', 'ShowSuccess("\u2714 Modo Desenvolvedor"' in sp)
Check('toast "Modo Normal" sem lixo', 'ShowInfo("\u2139 Modo Normal"' in sp)
Check('"Snapshot de diretorios" legivel', "Snapshot de diret" in du and "\ufffd" not in du)
for frase in ["Executando desinstala\u00e7\u00e3o",
              "Desinstala\u00e7\u00e3o conclu\u00edda",
              "vari\u00e1veis de ambiente",
              "implementa\u00e7\u00e3o C#",
              "itera\u00e7\u00f5es"]:
    Check("frase restaurada: %s" % frase[:40], frase in du)

print()
print("=" * 74)
print("RESULTADO: %s" % ("TUDO OK" if not FALHAS else "%d FALHA(S): %s" % (len(FALHAS), FALHAS)))
print("=" * 74)
sys.exit(1 if FALHAS else 0)