"""Resgate pos-crash do Win32PrioritySeparation.

O bug: _origWin32PrioritySeparation era int? e o NULL significava duas coisas
("nunca apliquei" E "a chave nao existia"). Maquina sem a chave -> aplicava 38 ->
revert caia no ramo "nunca ativamos" e nao desfazia -> 38 ficava para sempre,
sem ninguem para reverter. Nao havia resgate em disco.
"""
import io, re, sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
FALHAS = []


def Check(nome, ok, detalhe=""):
    print("  %s %-58s %s" % ("OK  " if ok else "FALHA", nome, detalhe))
    if not ok:
        FALHAS.append(nome)


C = io.open("KitLugia.GUI/Services/TrayIconService.cs", encoding="utf-8-sig", errors="replace").read()

print("=" * 76)
print("1) Os 3 estados sao distinguiveis")
print("=" * 76)
Check("flag _sepAplicado existe (separa 'nunca apliquei' de 'nao existia')",
      "private static bool _sepAplicado;" in C)
Check("ReadWin32PrioritySeparationGuardado devolve int.MinValue quando nunca aplicou",
      "if (!_sepAplicado) return int.MinValue;" in C)
Check("devolve -1 quando a chave nao existia (estado valido)",
      "return _origWin32PrioritySeparation ?? -1;" in C)
Check("_sepAplicado = true ao aplicar", "_sepAplicado = true;" in C)
Check("_sepAplicado = false ao reverter", "_sepAplicado = false;" in C)

print()
print("=" * 76)
print("2) O revert restaura o estado REAL (nao chuta)")
print("=" * 76)
Check("valor existente -> reescreve o original",
      "key.SetValue(\"Win32PrioritySeparation\", orig, Microsoft.Win32.RegistryValueKind.DWord);" in C)
Check("chave ausente -> APAGA a chave",
      'key.DeleteValue("Win32PrioritySeparation", throwOnMissingValue: false);' in C)
Check("nao grava mais o '2' de antigamente",
      "SetValue(\"Win32PrioritySeparation\", 2," not in C)
# so pode gravar 38 (o valor do boost) ou o original
vals = re.findall(r'SetValue\("Win32PrioritySeparation",\s*([^,]+),', C)
print("       valores gravados no codigo: %s" % vals)
Check("apenas 38 ou a variavel original", set(v.strip() for v in vals) <= {"38", "orig", "v"},
      str(sorted(set(v.strip() for v in vals))))

print()
print("=" * 76)
print("3) Persistencia: o original vai para o disco")
print("=" * 76)
Check("SaveCrashRescue grava a linha sep|",
      'sb.Append("sep|").Append(sepOrig)' in C)
Check("nao grava quando nunca aplicou (int.MinValue)",
      "if (sepOrig != int.MinValue)" in C)
Check("os 4 pontos que aplicam o sep tambem persistem",
      C.count("SaveCrashRescue();   // sep e' global") == 4,
      "%d pontos" % C.count("SaveCrashRescue();   // sep e' global"))

print()
print("=" * 76)
print("4) O leitor trata 'sep' ANTES de ler PID  (o erro que evitamos)")
print("=" * 76)
i_sep = C.find('if (p[0] == "sep")')
i_pid = C.find("uint.TryParse(p[1], out uint pid)")
Check("ramo 'sep' existe", i_sep > 0)
Check("ramo 'sep' vem ANTES do parse de PID", 0 < i_sep < i_pid,
      "sep@%d  pid@%d" % (i_sep, i_pid))
Check("o ramo sep faz continue (nao cai para o fluxo de processo)",
      C[i_sep:i_pid].count("continue;") >= 1)
Check("repassa o valor exato para o setter",
      "SetWin32PrioritySeparationPara(sepOriginal)" in C)

print()
print("=" * 76)
print("5) O motor AUTOMATICO nao mexe em nada global (o que voce perguntou)")
print("=" * 76)
i_auto = C.find("private void ApplyBoostAuto")
corpo = C[i_auto:i_auto + 4200]
Check("ApplyBoostAuto define TimerBoost = false", "TimerBoost = false" in corpo)
Check("ApplyBoostAuto define NetworkBoost = false", "NetworkBoost = false" in corpo)
Check("ApplyBoostAuto NAO chama SetWin32PrioritySeparation",
      "SetWin32PrioritySeparation(" not in corpo)
Check("ApplyBoostAuto NAO chama BoostTimerResolution", "BoostTimerResolution(" not in corpo)
Check("ApplyBoostAuto NAO chama ApplyNetworkBoostV3", "ApplyNetworkBoostV3(" not in corpo)

print()
print("=" * 76)
print("6) Encoding")
print("=" * 76)
raw = io.open("KitLugia.GUI/Services/TrayIconService.cs", "rb").read()
t = raw.decode("utf-8-sig", errors="replace")
Check("BOM presente", raw[:3] == b"\xef\xbb\xbf")
Check("CRLF puro", t.count("\n") - t.count("\r\n") == 0)
Check("sem U+FFFD", "\ufffd" not in t)

print()
print("=" * 76)
print("RESULTADO: %s" % ("TUDO OK" if not FALHAS else "%d FALHA(S): %s" % (len(FALHAS), FALHAS)))
print("=" * 76)
sys.exit(1 if FALHAS else 0)