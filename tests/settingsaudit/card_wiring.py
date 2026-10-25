"""Wiring do card MONITORAMENTO AVANÇADO + guarda contra toggle orfao.

O erro cometido DUAS vezes nesta sessao: criar CheckBox sem handler
(compila, nao faz nada) e handler sem CheckBox. Este teste pega os dois.
NAO mexe no registro, NAO chama SetAutoStart.
"""
import io, re, sys, os

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
FALHAS = []


def Check(nome, ok, detalhe=""):
    print("  %s %-58s %s" % ("OK  " if ok else "FALHA", nome, detalhe))
    if not ok:
        FALHAS.append(nome)


X = io.open("KitLugia.GUI/Pages/SettingsPage.xaml", encoding="utf-8-sig", errors="replace").read()
C = io.open("KitLugia.GUI/Pages/SettingsPage.xaml.cs", encoding="utf-8-sig", errors="replace").read()

print("=" * 76)
print("1) Todo CheckBox tem acao (e vice-versa)")
print("=" * 76)

# Esta pagina usa DOIS estilos de wired, e o teste precisa ver os dois:
#   (a) XAML   : <CheckBox ... Click="OnX" />
#   (b) codigo : AddToggleEvents() faz Chk.Click += OnX
ADD = C.split("AddToggleEvents()")[-1].split("private void")[0] if "AddToggleEvents()" in C else ""
ADD_REAIS = C[C.find("private void AddToggleEvents()"):C.find("private void AddToggleEvents()") + 2000]

NAMED_CHECK = dict(re.findall(
    r'<CheckBox\s+x:Name="([A-Za-z0-9_]+)"(.*?)/>', X, re.S))
EVENTOS = ("Click", "Checked", "Unchecked", "LostFocus", "SelectionChanged", "TextChanged")

for nome, attrs in sorted(NAMED_CHECK.items()):
    no_xaml = any(re.search(r'\b%s="' % e, attrs) for e in EVENTOS)
    no_codigo = re.search(r'%s\.Click\s*\+=' % re.escape(nome), ADD_REAIS) is not None
    tem = no_xaml or no_codigo
    Check("CheckBox %s tem acao" % nome, tem,
          ("XAML" if no_xaml else "") + ("+codigo" if no_xaml and no_codigo else ("codigo" if no_codigo else ">>> SEM ACAO <<<")))

# handlers referenciados no XAML que nao existem no .cs
# (pega CheckBox E TextBox — os limiares usam LostFocus num TextBox)
usados = set(re.findall(r'\b(?:Click|Checked|Unchecked|LostFocus|SelectionChanged|TextChanged)="([A-Za-z0-9_]+)"', X))
metodos = set(re.findall(r'(?:private|public|internal)\s+(?:static\s+)?(?:async\s+)?void\s+([A-Za-z0-9_]+)\s*\(', C))
faltando = sorted(u for u in usados if u not in metodos)
Check("todo handler do XAML existe no code-behind", not faltando, str(faltando))

# metodos handler sem nenhum wired (XAML nem +=  no lado do metodo)
orfaos = sorted(m for m in metodos
                if (m.startswith("On") or m.endswith("_LostFocus"))
                and m not in usados
                and not re.search(r'\+= %s;' % re.escape(m), C)
                and m not in ("OnStartup", "OnCleanup",))
Check("nenhum handler orfao no .cs", not orfaos, str(orfaos))

# e o inverso: todo += tem o -= (vazamento de assinatura)
pluses = re.findall(r'(\w+)\.Click \+= (\w+);', ADD_REAIS)
sem_menos = ["%s -> %s" % (o, h) for o, h in pluses
             if ("%s.Click -= %s;" % (o, h)) not in C]
Check("todo += tem o -= correspondente (Cleanup)", not sem_menos,
      str(sem_menos) if sem_menos else "%d bindings ok" % len(pluses))

print()
print("=" * 76)
print("2) Os 5 settings sem tela agora tem tela")
print("=" * 76)

ESPERADO = {
    "EnableSmartAlerts":      ("ToggleSmartAlerts", "tray.EnableSmartAlerts"),
    "HighRamThresholdMB":     ("TxtHighRamThreshold", "tray.HighRamThresholdMB ="),
    "HighCpuThresholdPercent": ("TxtHighCpuThreshold", "tray.HighCpuThresholdPercent ="),
    "AntiLeak":               ("ToggleAntiLeak", "t.MemoryLeakDetectionEnabled ="),
    "EnableBehaviorAnalysis": ("ToggleBehaviorAnalysis", "t.EnableBehaviorAnalysis ="),
    "FocusAssist":            ("ToggleFocusAssist", "t.FocusAssistEnabled ="),
}
for chave, (ctrl, uso) in ESPERADO.items():
    Check("%s tem controle" % chave, ('x:Name="%s"' % ctrl) in X, ctrl)
    Check("%s e lido do TrayService" % chave, uso in C, uso)
    Check("%s e persistido" % chave, "tray.SaveSettings()" in C)

print()
print("=" * 76)
print("3) Coerencia de leitura")
print("=" * 76)
Check("le do TrayService na carga", "ToggleSmartAlerts.IsChecked = tray.EnableSmartAlerts;" in C)
Check("limiaresem clamp na UI (setter do tray ja trava)", "mb < 512" in C and "pc < 10 || pc > 100" in C)
Check("aceita virgula decimal (pt-BR)", ".Replace(',', '.')" in C)
Check("texto invalido reverte para o valor atual, nao exception", "t.Text = Tray?.HighRamThresholdMB" in C)
Check("campos desabilitados quando alertas estao off", "TxtHighRamThreshold.IsEnabled = on" in C)
Check("atualiza a dica apos mudar", "UpdateThresholdStatus();" in C)

print()
print("=" * 76)
print("4) Nada regrediu")
print("=" * 76)
Check("ToggleRAMMonitor continua no AutoCleanEnabled real",
      "TrayService.AutoCleanEnabled = on" in C)
Check("nao voltou a ler RAMMonitor do JSON",
      "ToggleRAMMonitor.IsChecked = settings.RAMMonitorEnabled" not in C)
Check("os 13 toggles antigos continuam", all(('x:Name="%s"' % t) in X for t in
      ["ToggleStartup", "ToggleIntroAnimation", "ToggleRAMMonitor", "ToggleGameBoost",
       "ToggleTurboBoot", "ToggleTurboShutdown", "ToggleStandbyClean",
       "ToggleAudioAntiStutter", "ToggleNotifications", "ToggleVerboseLogging",
       "ToggleDeveloperMode", "ToggleRestorePoint", "ToggleAskRestorePoint"]))

print()
print("=" * 76)
print("5) Encoding")
print("=" * 76)
for f in ["KitLugia.GUI/Pages/SettingsPage.xaml", "KitLugia.GUI/Pages/SettingsPage.xaml.cs"]:
    raw = io.open(f, "rb").read()
    bom = raw[:3] == b"\xef\xbb\xbf"
    t = raw.decode("utf-8-sig", errors="replace")
    lf = t.count("\n") - t.count("\r\n")
    Check("%s BOM+CRLF" % f.split("/")[-1], bom and lf == 0, "BOM=%s LF=%d" % (bom, lf))
    Check("%s sem U+FFFD" % f.split("/")[-1], "\ufffd" not in t)

print()
print("=" * 76)
print("RESULTADO: %s" % ("TUDO OK" if not FALHAS else "%d FALHA(S): %s" % (len(FALHAS), FALHAS)))
print("=" * 76)
sys.exit(1 if FALHAS else 0)