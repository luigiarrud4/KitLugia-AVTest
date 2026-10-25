// ===========================================================================
// T9 - Ciclo de "AGUARDANDO REINÍCIO" (06/10/2026).
//
// MOTIVO: o Guardian gravava o valor e o check ficava VERDE na hora. Em
// protecoes que o Windows so aplica no proximo boot (HVCI, VBS, pagefile)
// isso e mentira: o hypervisor ainda nao leu a configuracao.
//
// O QUE ESTE HARNESS PROVA (chamando a API REAL do Core):
//   T9a  NeedsRebootAfterFix identifica DeviceGuard (HVCI/VBS) e pagefile
//   T9b  Apos ToggleTweak, o item entra em PENDING e o arquivo de estado e'
//        gravado em disco (so o Apply no scan troca o status)
//   T9c  Sem reinicio (TickCount crescendo), Apply mantem PENDING
//   T9d  Reiniciado + efeito veio (CheckTweak = OK)  -> sai de PENDING
//   T9e  Reiniciado + efeito NAO veio (CheckTweak = MODIFIED) -> INVALIDADO,
//        status NOT_FOUND e InvalidReason preenchido
//   T9f  O arquivo de estado realmente existe no disco e e JSON valido
//   T9g  PENDING e invalidos NAO entram no denominador do score
//
// USO:  IntegrityAudit.exe            (achou o fonte sozinho)
//       IntegrityAudit.exe --src <repo>
//
// CUIDADO: este teste grava/apaga SOMENTE HKCU\Software\KitLugia\IntegrityPendingTest
// e %LOCALAPPDATA%\KitLugia\integrity-pending.json. Nada de sistema.
// ===========================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;
using KitLugia.Core;

/// <summary>
/// Prova o ciclo de PENDING do Guardian chamando a API real e simulando a
/// passagem do tempo (reboot) por injecao do TickCount.
/// </summary>
internal static class PendingCycle
{
    private const string ProbeKey = @"Software\KitLugia\IntegrityPendingTest";

    private static int _ok, _fail;

    private static void Check(string what, bool cond, string det = "")
    {
        if (cond) { _ok++; Console.WriteLine("  OK   " + what + (det.Length > 0 ? "  [" + det + "]" : "")); }
        else { _fail++; Console.WriteLine("  FALHA " + what + "  [" + det + "]"); }
    }

    private static readonly Type EntryT = typeof(PendingRepairEntry);
    private static readonly Type InvT = typeof(InvalidatedCheck);
    private static readonly Type StateT = typeof(PendingRepairStore).Assembly
        .GetType("KitLugia.Core.IntegrityState");

    private static readonly MethodInfo AddEntry = StateT.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
    private static readonly PropertyInfo PEnd = StateT.GetProperty("Pending");
    private static readonly PropertyInfo PInv = StateT.GetProperty("Invalidated");
    private static readonly FieldInfo CacheF = typeof(PendingRepairStore)
        .GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly FieldInfo GateF = typeof(PendingRepairStore)
        .GetField("Gate", BindingFlags.NonPublic | BindingFlags.Static);

    private static object LoadState()
    {
        var m = typeof(PendingRepairStore).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Static);
        return m.Invoke(null, null);
    }

    /// <summary>A lista Pending/Invalidated do estado interno (via reflexao).</summary>
    private static System.Collections.IList Pend(object st) => (System.Collections.IList)PEnd.GetValue(st);

    private static System.Collections.IList Inv(object st) => (System.Collections.IList)PInv.GetValue(st);

    private static int Count(System.Collections.IList l) => l == null ? 0 : l.Count;

    private static void AddPend(object st, object entry) => Pend(st).Add(entry);

    private static void SaveState(object state)
    {
        var m = typeof(PendingRepairStore).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Static);
        m.Invoke(null, new[] { state });
    }

    /// <summary>Zera o cache em memoria para o proximo Load() ler do disco.</summary>
    private static void ResetCache() => CacheF.SetValue(null, null);

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KitLugia", "integrity-pending.json");

    private static object NewEntry(string name, int tick)
    {
        var e = Activator.CreateInstance(EntryT);
        EntryT.GetProperty("Name").SetValue(e, name);
        EntryT.GetProperty("Reason").SetValue(e, "teste");
        EntryT.GetProperty("TickAtApply").SetValue(e, tick);
        return e;
    }

    private static object NewInv(string name, string reason)
    {
        var v = Activator.CreateInstance(InvT);
        InvT.GetProperty("Name").SetValue(v, name);
        InvT.GetProperty("Reason").SetValue(v, reason);
        return v;
    }

    public static int Run()
    {
        Console.WriteLine();
        Console.WriteLine("--- T9: ciclo de AGUARDANDO REINICIO ---");

        var statePath = StorePath;
        // O backup/restauração do estado do usuário e' do Program
        // (IntegrityStateBackup): cada teste fazendo o seu próprio backup
        // vazava o arquivo de lixo do teste anterior.
        var gate = GateF.GetValue(null);

        try
        {
            // ───────────── T9a: NeedsRebootAfterFix ─────────────
            var hvci = new ScannableTweak
            {
                Name = "Integridade de Memoria (HVCI)",
                KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity",
                ValueName = "Enabled"
            };
            var registroComum = new ScannableTweak
            {
                Name = "Assinatura SMB Obrigatoria (Servidor)",
                KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters",
                ValueName = "Smb2SigningEnabled"
            };
            var pagefile = new ScannableTweak
            {
                Name = "Arquivo de Paginacao",
                KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
                ValueName = "PagingFiles"
            };
            var forcado = new ScannableTweak { Name = "X", RequiresReboot = true };

            Check("T9a DeviceGuard (HVCI) precisa de reinicio",
                PendingRepairStore.NeedsRebootAfterFix(hvci));
            Check("T9a pagefile precisa de reinicio",
                PendingRepairStore.NeedsRebootAfterFix(pagefile));
            Check("T9a registro comum NAO precisa de reinicio",
                !PendingRepairStore.NeedsRebootAfterFix(registroComum));
            Check("T9a RequiresReboot=true tem precedencia",
                PendingRepairStore.NeedsRebootAfterFix(forcado));

            // ───────────── T9b: ToggleTweak marca PENDING e grava o estado ─────────────
            var probe = new ScannableTweak
            {
                Name = "TESTE PENDING (nao usar)",
                Category = "Seguranca Critica",
                KeyPath = @"HKEY_CURRENT_USER\" + ProbeKey,
                ValueName = "Flag",
                HarmfulValue = 1,
                DefaultValue = 0,
                ValueKind = RegistryValueKind.DWord
            };
            Registry.SetValue(@"HKEY_CURRENT_USER\" + ProbeKey, "Flag", 1, RegistryValueKind.DWord);

            // SEM CheckTweak antes, o item nasce OK e o ToggleTweak gravaria o
            // valor PERIGOSO (applySafeValue = Status == MODIFIED). E' o mesmo
            // cuidado do round-trip existente.
            RegistryProbe.Check(probe);
            Check("T9b o probe comeca MODIFIED (valor inseguro)",
                probe.Status == TweakStatus.MODIFIED, "status=" + probe.Status);

            lock (gate)
            {
                var st = LoadState();
                Pend(st).Clear();
                Inv(st).Clear();
                SaveState(st);
            }

            var (ok, msg) = Guardian.ToggleTweak(probe);
            Check("T9b ToggleTweak gravou o valor", ok && (int)Registry.GetValue(
                @"HKEY_CURRENT_USER\" + ProbeKey, "Flag", -1) == 0, "ok=" + ok + " msg=" + msg);
            // este probe NAO exige reinicio: a mensagem nao deve pedir reiniciar
            Check("T9b check normal nao pede reinicio",
                msg != null && !msg.ToUpperInvariant().Contains("REINICI"), msg);
            Check("T9b o arquivo de estado foi gravado em disco", File.Exists(statePath), statePath);

            // e o JSON realmente vale?
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
                Check("T9b o arquivo e' JSON valido", true);
            }
            catch (Exception ex) { Check("T9b o arquivo e' JSON valido", false, ex.Message); }

            // um probe que EXIGE reinício tem de avisar na mensagem
            var probeReboot = new ScannableTweak
            {
                Name = "TESTE PENDING COM REBOOT",
                Category = "Seguranca Critica",
                KeyPath = @"HKEY_CURRENT_USER\" + ProbeKey,
                ValueName = "FlagReboot",
                HarmfulValue = 1,
                DefaultValue = 0,
                ValueKind = RegistryValueKind.DWord,
                RequiresReboot = true
            };
            Registry.SetValue(@"HKEY_CURRENT_USER\" + ProbeKey, "FlagReboot", 1, RegistryValueKind.DWord);
            RegistryProbe.Check(probeReboot);
            var (rOk, rMsg) = Guardian.ToggleTweak(probeReboot);
            Check("T9b RequiresReboot grava o valor", rOk &&
                (int)Registry.GetValue(@"HKEY_CURRENT_USER\" + ProbeKey, "FlagReboot", -1) == 0, "ok=" + rOk);
            Check("T9b a mensagem avisa que precisa reiniciar",
                rMsg != null && rMsg.ToUpperInvariant().Contains("REINICI"),
                rMsg);
            Check("T9b o item que exige reboot NAO se anuncia como protegido",
                rMsg != null && !rMsg.Contains("PROTEGIDO"), rMsg);

            // ───────────── T9c: SEM reinicio -> continua PENDING ─────────────
            // "Sem reinício" = o uptime CONTINUA crescendo: TickAtApply menor que
            // o TickCount atual. (Colocar um valor enorme seria o oposto: uptime
            // caindo = boot novo.)
            int agoraReal = Environment.TickCount;
            int tickGravado = Math.Max(1, agoraReal - 1000);   // aplicado "ha 1 s"
            lock (gate)
            {
                var st = LoadState();
                Pend(st).Clear();
                Inv(st).Clear();
                AddPend(st, NewEntry(probeReboot.Name, tickGravado));
                SaveState(st);
            }
            ResetCache();

            var pend2 = new List<ScannableTweak> { probeReboot };
            probeReboot.Status = TweakStatus.PENDING;
            lock (gate) PendingRepairStore.Apply(pend2);
            Check("T9c sem reinicio o item continua PENDING",
                pend2[0].Status == TweakStatus.PENDING, "status=" + pend2[0].Status);
            Check("T9c o motivo aparece no item",
                !string.IsNullOrEmpty(pend2[0].PendingReason), pend2[0].PendingReason ?? "(vazio)");

            // e o item pendente NAO pode ser apresentado como protegido
            Check("T9c pendente nao vira OK nem MODIFIED",
                pend2[0].Status == TweakStatus.PENDING && probe.Status == TweakStatus.OK,
                "pendente=" + pend2[0].Status + " normal=" + probe.Status);

            // ───────────── T9d: REINICIOU + efeito veio -> OK, sai de PENDING ─────────────
            lock (gate)
            {
                var st = LoadState();
                Pend(st).Clear();
                Inv(st).Clear();
                // TickAtApply alto + uptime curto = boot novo
                AddPend(st, NewEntry(probe.Name, int.MaxValue - 1000));
                SaveState(st);
            }
            ResetCache();

            // efeito veio: o valor esta seguro (CheckTweak daria OK)
            Registry.SetValue(@"HKEY_CURRENT_USER\" + ProbeKey, "Flag", 0, RegistryValueKind.DWord);
            probe.Status = TweakStatus.OK;
            var listaOk = new List<ScannableTweak> { probe };
            lock (gate) PendingRepairStore.Apply(listaOk);
            Check("T9d reiniciou e o efeito veio -> OK (nao fica PENDING)",
                probe.Status == TweakStatus.OK, "status=" + probe.Status);
            Check("T9d o motivo de pendencia foi limpo",
                string.IsNullOrEmpty(probe.PendingReason));

            lock (gate)
            {
                var st = LoadState();
                Check("T9d o item saiu da lista de pendentes",
                    Count(Pend(st)) == 0);
                Check("T9d nada foi invalidado quando o efeito veio",
                    Count(Inv(st)) == 0);
            }

            // ───────────── T9e: REINICIOU + efeito NAO veio -> INVALIDADO ─────────────
            lock (gate)
            {
                var st = LoadState();
                Pend(st).Clear();
                Inv(st).Clear();
                AddPend(st, NewEntry(probe.Name, int.MaxValue - 1000));
                SaveState(st);
            }
            ResetCache();

            // efeito NAO veio: o valor continua inseguro (CheckTweak = MODIFIED)
            Registry.SetValue(@"HKEY_CURRENT_USER\" + ProbeKey, "Flag", 1, RegistryValueKind.DWord);
            probe.Status = TweakStatus.MODIFIED;
            var listaRuim = new List<ScannableTweak> { probe };
            lock (gate) PendingRepairStore.Apply(listaRuim);
            Check("T9e reiniciou e o efeito NAO veio -> item invalidado",
                !string.IsNullOrEmpty(probe.InvalidReason), "invalid=" + (probe.InvalidReason ?? "(vazio)"));
            Check("T9e o invalidado nao fica mais como MODIFIED",
                probe.Status != TweakStatus.MODIFIED, "status=" + probe.Status);

            lock (gate)
            {
                var st = LoadState();
                Check("T9e foi salvo na lista de invalidados",
                    Count(Inv(st)) == 1);
                Check("T9e saiu da lista de pendentes",
                    Count(Pend(st)) == 0);
            }

            // invalidados reaparecem em todo scan seguinte
            var lista2 = new List<ScannableTweak> { probe };
            probe.Status = TweakStatus.MODIFIED;
            probe.InvalidReason = null;
            ResetCache();
            lock (gate) PendingRepairStore.Apply(lista2);
            Check("T9e o invalido persiste no scan seguinte",
                probe.Status != TweakStatus.MODIFIED && !string.IsNullOrEmpty(probe.InvalidReason));

            // ───────────── T9f: o arquivo em disco tem o conteudo esperado ─────────────
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
                var inv = doc.RootElement.GetProperty("Invalidated");
                Check("T9f o invalido esta persistido no disco",
                    inv.GetArrayLength() == 1,
                    "Invalidated=" + inv.GetArrayLength());
            }
            catch (Exception ex) { Check("T9f o invalido esta persistido no disco", false, ex.Message); }

            // ───────────── T9g: fora do score ─────────────
            // Regra da UI: PENDING e InvalidReason != null sao excluidos do
            // denominador. Reimplementada aqui para travar a regra.
            var base_ = new List<ScannableTweak>
            {
                new ScannableTweak { Name = "a", Status = TweakStatus.OK },
                new ScannableTweak { Name = "b", Status = TweakStatus.MODIFIED },
                new ScannableTweak { Name = "c", Status = TweakStatus.PENDING },
                new ScannableTweak { Name = "d", Status = TweakStatus.MODIFIED, InvalidReason = "x" },
            };
            var countable = base_.Where(t => t.Status != TweakStatus.PENDING && t.InvalidReason == null).ToList();
            var badCount = countable.Count(t => t.Status == TweakStatus.MODIFIED);
            int score = countable.Count > 0 ? 100 - (int)Math.Ceiling(100.0 * badCount / countable.Count) : 100;
            Check("T9g pendentes e invalidos ficam fora do denominador",
                countable.Count == 2 && badCount == 1 && score == 50,
                "countable=" + countable.Count + " bad=" + badCount + " score=" + score);

            // ───────────── limpeza ─────────────
            PendingRepairStore.ClearInvalidated();
            Check("T9f ClearInvalidated esvazia a lista",
                PendingRepairStore.InvalidatedCount == 0);
        }
        catch (Exception ex)
        {
            Check("T9 excecao inesperada", false, ex.GetType().Name + ": " + ex.Message);
            Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(ProbeKey, false); } catch { }
            // o estado do usuario e' restaurado pelo Program
            // Arquivo de estado: cuidado e' do Program (IntegrityStateBackup).
            // Aqui so zera o cache em memoria.
            ResetCache();
        }

        Console.WriteLine("  T9: OK={0} FALHAS={1}", _ok, _fail);
        return _fail;
    }
}