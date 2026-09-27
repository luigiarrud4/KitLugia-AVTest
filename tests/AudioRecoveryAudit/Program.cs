// ══════════════════════════════════════════════════════════════════════════════
//  AUDIO RECOVERY AUDIT — revisão de robustez da recuperação automática de áudio
//
//  Roda contra o código REAL do KitLugia.Core (AudioGlitchMonitor + StorageDiagnostics),
//  via reflection nos membros privados. Nada aqui altera o comportamento do app:
//  o processo é descartável e roda fora do Kit.
//
//  Fase 1: tabelas de decisão, isolamento dos eventos provocados, proteção de PID
//          reciclado, identidade do processo, cooldown/limite pelo caminho real e
//          a observação pós-recuperação (eficaz vs sem efeito).
//  Fase 2 (admin): recuperação REAL — suspende/retoma o audiodg de verdade.
//          Sem privilégio a fase 2 é saltada (SKIP), nada é tocado no áudio.
//
//  Uso:  dotnet run --project tests/AudioRecoveryAudit              (fase 1 + 2)
//        dotnet run --project tests/AudioRecoveryAudit -- phase1    (só a fase 1)
//
//  ATENÇÃO: a fase 1 (e a 2) executam resets REAIS do motor de áudio quando o
//  critério fecha — é deliberado: o objetivo é provar a recuperação de ponta a
//  ponta, não só a tabela de decisão. Cada reset dura ~300 ms e não reinicia nada.
//
//  Conclusões e riscos residuais: docs/AUDIO_RECOVERY_ROBUSTNESS.md
// ══════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using KitLugia.Core.TaskManager;
using static KitLugia.Core.TaskManager.StorageDiagnostics;

Console.OutputEncoding = System.Text.Encoding.UTF8;

int pass = 0, fail = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {name}"); }
    else { fail++; Console.WriteLine($"  FAIL  {name}{(detail.Length > 0 ? "  → " + detail : "")}"); }
}
void Head(string t) => Console.WriteLine("\n=== " + t + " ===");

bool isAdmin = false;
try
{
    using var id = WindowsIdentity.GetCurrent();
    isAdmin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
}
catch { }

var argsList = Environment.GetCommandLineArgs().Skip(1).ToArray();
bool phase1Only = argsList.Contains("phase1");

var mon = AudioGlitchMonitor.Instance;

// ── acesso aos membros privados ────────────────────────────────────────────
var tMon = typeof(AudioGlitchMonitor);
const BindingFlags PRIV = BindingFlags.NonPublic | BindingFlags.Instance;

var mReport = tMon.GetMethod("Report", PRIV)!;
var mMaybe = tMon.GetMethod("MaybeAutoRecover", PRIV)!;
var fProvoked = tMon.GetField("_provokedUntilMs", PRIV)!;
var fRecoveries = tMon.GetField("_recoveries", PRIV)!;
var fRun = tMon.GetField("_run", PRIV)!;
var fClock = tMon.GetField("_clock", PRIV)!;
var fRecoveryLine = tMon.GetField("_recoveryLine", PRIV)!;
var fAssessFrom = tMon.GetField("_assessFrom", PRIV)!;
var fAssessAfter = tMon.GetField("_assessAfter", PRIV)!;
var mAssess = tMon.GetMethod("AssessLastRecovery", PRIV)!;

long ClockMs() => ((Stopwatch)fClock.GetValue(mon)!).ElapsedMilliseconds;
List<DateTime> Recoveries() => (List<DateTime>)fRecoveries.GetValue(mon)!;

// Um evento REAL: o motor sinalizou, o Kit não estava atrasado e não era arranque.
void RealGlitch(double lostMs = 120, bool audible = true)
{
    ulong frames = (ulong)Math.Max(1, (int)(lostMs * 48));
    mReport.Invoke(mon, new object[] { true, false, lostMs, frames, audible ? 0.35 : 0.0, false, false, lostMs });
}

void ClearAll()
{
    mon.Clear();
    fProvoked.SetValue(mon, 0L);   // janela de provocado encerrada
}

// ── FASE 1 ────────────────────────────────────────────────────────────────
Console.WriteLine($"Harness de robustez de áudio — admin: {(isAdmin ? "SIM" : "não")}");

Head("1. TABELA DE DECISÃO — ShouldAutoRecover (pura)");
var now = DateTime.Now;
IReadOnlyList<DateTime> At(params double[] secAgo) => secAgo.Select(s => now.AddSeconds(-s)).ToList();

var r = AudioGlitchMonitor.ShouldAutoRecover(At(5, 30), now, default, 0, 0);
Check("2 estalos confirmados em 90 s → recupera", r.Should, r.Reason);

r = AudioGlitchMonitor.ShouldAutoRecover(At(5), now, default, 0, 0);
Check("1 estalo isolado → NÃO recupera", !r.Should && r.Reason.Contains("não justifica"), r.Reason);

r = AudioGlitchMonitor.ShouldAutoRecover(At(5, 30), now, now.AddSeconds(-30), 0, 0);
Check("cooldown de 60 s (reset há 30 s) → NÃO recupera", !r.Should && r.Reason.Contains("cooldown"), r.Reason);

r = AudioGlitchMonitor.ShouldAutoRecover(At(5, 30), now, now.AddSeconds(-61), 0, 0);
Check("cooldown vencido (reset há 61 s) → recupera", r.Should, r.Reason);

r = AudioGlitchMonitor.ShouldAutoRecover(At(5, 30), now, now.AddSeconds(-300), 3, 0);
Check("3 recuperações na hora → NÃO recupera", !r.Should && r.Reason.Contains("limite de 3"), r.Reason);

r = AudioGlitchMonitor.ShouldAutoRecover(At(5, 30), now, now.AddSeconds(-300), 2, 0);
Check("2 recuperações na hora → ainda recupera", r.Should, r.Reason);

r = AudioGlitchMonitor.ShouldAutoRecover(At(5, 30), now, default, 0, 1);
Check("reset do próprio Kit em andamento → NÃO recupera", !r.Should && r.Reason.Contains("reset do próprio Kit"), r.Reason);

r = AudioGlitchMonitor.ShouldAutoRecover(At(5, 30), now, default, 3, 1);
Check("provocado pendente tem precedência sobre o limite", !r.Should && r.Reason.Contains("reset do próprio Kit"), r.Reason);

r = AudioGlitchMonitor.ShouldAutoRecover(At(5, 89), now, default, 0, 0);
Check("2 estalos (5 s e 89 s) dentro da janela → recupera", r.Should, r.Reason);

r = AudioGlitchMonitor.ShouldAutoRecover(At(5, 91), now, default, 0, 0);
Check("2 estalos, um fora da janela (91 s) → NÃO recupera", !r.Should && r.Reason.Contains("não justifica"), r.Reason);

r = AudioGlitchMonitor.ShouldAutoRecover(Array.Empty<DateTime>(), now, default, 0, 0);
Check("sem eventos → NÃO recupera", !r.Should, r.Reason);

Head("2. CLASSIFICAÇÃO — Classify (pura)");
Check("flag do motor, Kit em dia → CONFIRMADO",
    AudioGlitchMonitor.Classify(true, false, false, false, 120, 5760).Kind == "CONFIRMADO");
Check("flag do motor + Kit atrasado → SUSPEITO",
    AudioGlitchMonitor.Classify(true, false, true, false, 120, 5760).Kind == "SUSPEITO");
Check("flag do motor + arranque de stream → SUSPEITO",
    AudioGlitchMonitor.Classify(true, false, false, true, 120, 5760).Kind == "SUSPEITO");
Check("erro de timestamp, Kit em dia → SINALIZADO",
    AudioGlitchMonitor.Classify(false, true, false, false, 0, 0).Kind == "SINALIZADO");
Check("erro de timestamp + Kit atrasado → SUSPEITO (rebaixado)",
    AudioGlitchMonitor.Classify(false, true, true, false, 0, 0).Kind == "SUSPEITO");
Check("salto sem flag → SUSPEITO (indício, não prova)",
    AudioGlitchMonitor.Classify(false, false, false, false, 80, 3840).Kind == "SUSPEITO");

Head("3. EVENTO PROVOCADO NÃO ENTRA NAS ESTATÍSTICAS (código real)");
ClearAll();
RealGlitch();
int g1 = mon.GlitchCount, c1 = mon.AudibleConfirmedCount;
Check("estalo real conta em GlitchCount/AudibleConfirmedCount", g1 == 1 && c1 == 1, $"count={g1}/{c1}");

fProvoked.SetValue(mon, ClockMs() + 10_000);   // simula a janela pós-reset
RealGlitch();
var list = mon.Glitches.ToList();
var last = list[^1];
Check("dentro da janela o evento sai como PROVOCADO", last.Kind == "PROVOCADO" && last.ProvokedByKit, last.Kind);
Check("provocado NÃO aumenta GlitchCount", mon.GlitchCount == g1, $"count={mon.GlitchCount}");
Check("provocado NÃO aumenta AudibleConfirmedCount", mon.AudibleConfirmedCount == c1, $"count={mon.AudibleConfirmedCount}");
Check("provocado continua VISÍVEL na lista (transparência)", list.Count == 2);

ClearAll();
RealGlitch();
Check("fora da janela o mesmo estalo volta a contar", mon.GlitchCount == 1, $"count={mon.GlitchCount}");

Head("4. UMA RECUPERAÇÃO NÃO DISPARA OUTRA (código real)");
ClearAll();
bool oldAuto = mon.AutoRecover;
mon.AutoRecover = true;
fRun.SetValue(mon, true);
int before = mon.RecoveryCount;

// (a) só existe evento PROVOCADO no histórico → não pode haver recuperação nova
fProvoked.SetValue(mon, ClockMs() + 10_000);
RealGlitch();
fProvoked.SetValue(mon, 0L);          // janela já encerrada; o evento continua marcado
mMaybe.Invoke(mon, null);
Check("histórico só com provocado → nenhuma recuperação disparada", mon.RecoveryCount == before,
    $"recoveries={mon.RecoveryCount} texto=\"{mon.LastRecoveryText}\"");

// (b) com um reset em andamento, a decisão é adiada (não empilha reset em cima de reset)
fProvoked.SetValue(mon, ClockMs() + 10_000);
RealGlitch();                          // provocado
RealGlitch();                          // provocado
fProvoked.SetValue(mon, ClockMs() + 10_000);   // ainda dentro da janela
mMaybe.Invoke(mon, null);
Check("reset em andamento → recuperação adiada (sem empilhar)", mon.RecoveryCount == before,
    $"recoveries={mon.RecoveryCount} texto=\"{mon.LastRecoveryText}\"");

// (c) 1 estalo real isolado também não justifica mexer no motor
ClearAll();
RealGlitch();
mMaybe.Invoke(mon, null);
Check("1 estalo real isolado → nenhuma recuperação", mon.RecoveryCount == before,
    $"recoveries={mon.RecoveryCount} texto=\"{mon.LastRecoveryText}\"");

Head("5. COOLDOWN E LIMITE POR HORA RESPEITADOS (código real, sem tocar no áudio)");
ClearAll();
var recs = Recoveries();
recs.Clear();
recs.Add(DateTime.Now.AddSeconds(-10));      // um reset há 10 s (cooldown ativo)
RealGlitch(); RealGlitch();                  // 2 estalos reais
mMaybe.Invoke(mon, null);
Check("cooldown ativo → nenhuma recuperação nova", mon.RecoveryCount == 1, $"recoveries={mon.RecoveryCount}");

mon.Clear();                                 // o botão "Limpar" da UI
mMaybe.Invoke(mon, null);
Check("'Limpar' não zera o cooldown (não dá para burlar limpando a lista)", mon.RecoveryCount == 1,
    $"recoveries={mon.RecoveryCount}");

recs = Recoveries();
recs.Clear();
recs.Add(DateTime.Now.AddSeconds(-300));
recs.Add(DateTime.Now.AddSeconds(-240));
recs.Add(DateTime.Now.AddSeconds(-180));     // 3 na última hora
ClearAll();
RealGlitch(); RealGlitch();                  // estalos reais CONTINUAM
mMaybe.Invoke(mon, null);
Check("limite de 3/hora atingido → recuperação fica pausada", mon.RecoveryCount == 3,
    $"recoveries={mon.RecoveryCount} texto=\"{mon.LastRecoveryText}\"");
Check("com o limite atingido o diagnóstico continua (eventos seguem registrados)", mon.GlitchCount == 2,
    $"count={mon.GlitchCount}");

// Único caso da fase 1 que executa um reset DE VERDADE: as recuperações antigas
// (>1 h) não contam para o limite, então o critério fecha e o Kit realmente
// suspende/retoma o motor de áudio. É o "teste real de recuperação".
recs.Clear();
recs.Add(DateTime.Now.AddHours(-2));         // fora da janela de 1 h
ClearAll();
RealGlitch(); RealGlitch();
int recBeforeReal = mon.RecoveryCount;
mMaybe.Invoke(mon, null);
Check("recuperações antigas (>1 h) não contam para o limite → reset real executado",
    mon.RecoveryCount == recBeforeReal + 1,
    $"recoveries={mon.RecoveryCount} texto=\"{mon.LastRecoveryText}\"");
Check("o reset real também confirma o motor sincronizado",
    mon.LastRecoveryText.Contains("sincronizado de volta"), mon.LastRecoveryText);

Head("6. PID RECICLADO — identidade do processo (código real)");
var self = GetIdentity(Environment.ProcessId, "harness");
Check("identidade do processo atual é conhecida", self.Known, $"pid={self.Pid}");
var realStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();
double diffMs = Math.Abs((new DateTime(self.StartUtcTicks, DateTimeKind.Utc) - realStart).TotalMilliseconds);
Check("hora de criação bate com o processo real (prova o sinal do FILETIME)",
    diffMs < 1000, $"diferença={diffMs:F0} ms");

Check("mesmo processo, mesma identidade → SameProcess = true", SameProcess(self, out _));

var mesmoNumeroOutroProcesso = new ProcessIdentity(self.Pid, self.StartUtcTicks - 60_000_000, "audiodg");
Check("MESMO PID com hora de criação diferente (reciclado) → recusado",
    !SameProcess(mesmoNumeroOutroProcesso, out var why) && why.Contains("OUTRO processo"), why);

Check("identidade desconhecida → não bloqueia (falha-aberta documentada)",
    SameProcess(new ProcessIdentity(self.Pid, 0, "audiodg"), out _));

Head("7. INTEGRIDADE DO ESTADO APÓS AS VALIDAÇÕES");
Check("GlitchCount == eventos não provocados da lista",
    mon.Glitches.Count(g => !g.ProvokedByKit) == mon.GlitchCount, mon.Glitches.Count(g => !g.ProvokedByKit).ToString());
Check("AudibleConfirmedCount == CONFIRMADOS não provocados",
    mon.Glitches.Count(g => g.Kind == "CONFIRMADO" && !g.ProvokedByKit) == mon.AudibleConfirmedCount);
Check("nenhum evento provocado escapou com Kind=CONFIRMADO",
    mon.Glitches.All(g => !g.ProvokedByKit || g.Kind == "PROVOCADO"));

// ── FASE 2 ────────────────────────────────────────────────────────────────
if (phase1Only)
{
    Console.WriteLine($"\n(fase 1 apenas — {pass} passaram, {fail} falharam)");
    return fail == 0 ? 0 : 1;
}

Head("8. RECUPERAÇÃO REAL (suspende/retoma o audiodg de verdade)");
if (!isAdmin)
{
    Console.WriteLine("  SKIP  sem privilégio de administrador — a fase 2 precisa de elevação.");
    Console.WriteLine($"\nResumo: {pass} passaram, {fail} falharam (fase 1)");
    return fail == 0 ? 0 : 1;
}

ClearAll();
Recoveries().Clear();
mon.AutoRecover = true;
fRun.SetValue(mon, true);

var audiodg = Process.GetProcessesByName("audiodg").FirstOrDefault();
if (audiodg == null)
{
    Console.WriteLine("  SKIP  audiodg não está rodando (sem áudio ativo agora).");
}
else
{
    int pidBefore = audiodg.Id;
    RealGlitch(150);
    RealGlitch(150);
    int recBefore = mon.RecoveryCount;
    var sw = Stopwatch.StartNew();
    mMaybe.Invoke(mon, null);
    sw.Stop();
    Console.WriteLine($"  ... reset real levou {sw.ElapsedMilliseconds} ms · texto: \"{mon.LastRecoveryText}\"");
    Check("recuperação REAL foi executada", mon.RecoveryCount == recBefore + 1, $"recoveries={mon.RecoveryCount}");
    Check("mensagem confirma o reset do motor", mon.LastRecoveryText.Contains("sincronizado de volta"),
        mon.LastRecoveryText);
    var after = Process.GetProcessesByName("audiodg").FirstOrDefault();
    Check("o motor de áudio voltou a rodar (processo vivo)", after != null, "audiodg sumiu");
    if (after != null)
        Check("mesmo processo do motor (nada foi reiniciado/morto)", after.Id == pidBefore,
            $"antes={pidBefore} depois={after.Id}");

    // cooldown logo depois do reset real
    RealGlitch(90);
    RealGlitch(90);
    mMaybe.Invoke(mon, null);
    Check("logo após o reset real → cooldown impede um segundo reset", mon.RecoveryCount == recBefore + 1,
        $"recoveries={mon.RecoveryCount}");

    // limite por hora sobre a recuperação real.
    // Três resets DENTRO da última hora (o mais recente há 120 s: fora do cooldown),
    // para que o ÚNICO motivo do bloqueio seja o limite por hora.
    var recs2 = Recoveries();
    recs2.Clear();
    recs2.Add(DateTime.Now.AddSeconds(-3000));
    recs2.Add(DateTime.Now.AddSeconds(-1800));
    recs2.Add(DateTime.Now.AddSeconds(-120));
    fProvoked.SetValue(mon, 0L);
    ClearAll();
    RealGlitch(80);
    RealGlitch(80);
    int rec3 = mon.RecoveryCount;
    mMaybe.Invoke(mon, null);
    Check("limite por hora bloqueia o 4º reset (mesmo com estalo real)", mon.RecoveryCount == rec3,
        $"recoveries={mon.RecoveryCount}");
    Check("com o limite atingido o Kit segue registrando os estalos",
        mon.Glitches.Count(g => g.Kind == "CONFIRMADO" && !g.ProvokedByKit) == 2, "eventos perdidos");
}

Head("9. OBSERVAÇÃO PÓS-RECUPERAÇÃO — eficaz vs sem efeito (código real)");
const string marcador = "--teste-- motor de áudio sincronizado de volta. ";
void Janela(TimeSpan fim)   // janela de observação artificialmente vencida
{
    fAssessFrom.SetValue(mon, DateTime.Now.AddSeconds(-120));
    fAssessAfter.SetValue(mon, DateTime.Now - fim);
}

// (a) janela ainda ABERTA → não conclui nada (não declara resolvido sem observar)
mon.Clear();
fRecoveryLine.SetValue(mon, marcador);
Janela(TimeSpan.FromSeconds(-30));            // termina daqui a 30 s
mAssess.Invoke(mon, null);
Check("janela de observação aberta → veredito não é inventado",
    !mon.LastRecoveryText.Contains("EFICAZ") && !mon.LastRecoveryText.Contains("NÃO resolv"), mon.LastRecoveryText);

// (b) janela vencida COM estalos reais novos → SEM EFEITO
mon.Clear();
Janela(TimeSpan.FromSeconds(1));
RealGlitch(); RealGlitch();
mAssess.Invoke(mon, null);
Check("estalos reais novos depois do reset → 'NÃO resolveu'",
    mon.LastRecoveryText.Contains("NÃO resolveu") && mon.LastRecoveryText.Contains("2 estalo"), mon.LastRecoveryText);

// (c) janela vencida SEM estalo real novo → EFICAZ
mon.Clear();
Janela(TimeSpan.FromSeconds(1));
mAssess.Invoke(mon, null);
Check("nenhum estalo real novo → 'EFICAZ'", mon.LastRecoveryText.Contains("EFICAZ"), mon.LastRecoveryText);

// (d) só eventos PROVOCADOS na janela → isso NÃO é "sem efeito"
fProvoked.SetValue(mon, ClockMs() + 10_000);
RealGlitch();                                  // sai como PROVOCADO (eco do próprio reset)
fProvoked.SetValue(mon, 0L);
Janela(TimeSpan.FromSeconds(1));
mAssess.Invoke(mon, null);
Check("eco do próprio reset na janela → ainda 'EFICAZ' (provocado não conta)",
    mon.LastRecoveryText.Contains("EFICAZ"), mon.LastRecoveryText);
Check("o eco continua fora de GlitchCount", mon.GlitchCount == 0, $"count={mon.GlitchCount}");

// (e) limpar a lista no meio da observação cancela — nunca vira "eficaz"
Janela(TimeSpan.FromSeconds(-30));            // janela aberta
mon.Clear();
mAssess.Invoke(mon, null);
Check("limpar no meio da observação cancela em vez de declarar eficaz",
    mon.LastRecoveryText.Contains("cancelada") && !mon.LastRecoveryText.Contains("EFICAZ"), mon.LastRecoveryText);

mon.AutoRecover = false;

Console.WriteLine($"\nResumo: {pass} passaram, {fail} falharam");
return fail == 0 ? 0 : 1;
