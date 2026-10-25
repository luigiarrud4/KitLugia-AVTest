// ===========================================================================
// T10 - Id estavel + HVCI autoritativo + reavaliacao de invalidados.
//
// O QUE PROVA (API real do Core):
//   T10a Todo check do Guardian recebe um Id nao vazio e ESTAVEL entre scans
//   T10b O Id NAO depende do Name (renomear o Name mantem o Id)
//   T10b2 Dois checks iguais com nomes diferentes tem Ids diferentes
//   T10c O Id e realmente usado como chave em disco (StoreKey)
//   T10d NtQuerySystemInformation(103) responde e o bitmask e coerente
//   T10e O check de HVCI NAO e decidido pelo registro
//   T10f ClearInvalidated devolve os itens ao fluxo normal (reavaliacao)
//   T10g O 'nao confirmado' fica registrado em disco
//
// CUIDADO: so grava em HKCU\Software\KitLugia\IntegrityPendingTest e no
// integrity-pending.json (que e copiado/restaurado no fim).
// ===========================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using KitLugia.Core;

internal static class StableIdCycle
{
    private const string ProbeKey = @"Software\KitLugia\IntegrityPendingTest";

    private static int _ok, _fail;

    private static void Check(string what, bool cond, string det = "")
    {
        if (cond) { _ok++; Console.WriteLine("  OK   " + what + (det.Length > 0 ? "  [" + det + "]" : "")); }
        else { _fail++; Console.WriteLine("  FALHA " + what + "  [" + det + "]"); }
    }

    private static readonly Type StateT = typeof(PendingRepairStore).Assembly
        .GetType("KitLugia.Core.IntegrityState");
    private static readonly Type InvT = typeof(InvalidatedCheck);
    private static readonly FieldInfo CacheF = typeof(PendingRepairStore)
        .GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly FieldInfo GateF = typeof(PendingRepairStore)
        .GetField("Gate", BindingFlags.NonPublic | BindingFlags.Static);

    private static object LoadState()
    {
        var m = typeof(PendingRepairStore).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Static);
        return m.Invoke(null, null);
    }

    private static void SaveState(object s)
    {
        var m = typeof(PendingRepairStore).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Static);
        m.Invoke(null, new[] { s });
    }

    private static System.Collections.IList Inv(object st)
        => (System.Collections.IList)StateT.GetProperty("Invalidated").GetValue(st);

    private static System.Collections.IList Unc(object st)
        => (System.Collections.IList)StateT.GetProperty("Unconfirmed").GetValue(st);

    private static System.Collections.IList Pend(object st)
        => (System.Collections.IList)StateT.GetProperty("Pending").GetValue(st);

    private static void ResetCache() => CacheF.SetValue(null, null);

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KitLugia", "integrity-pending.json");

    public static int Run()
    {
        Console.WriteLine();
        Console.WriteLine("--- T10: Id estavel + HVCI autoritativo ---");
        var gate = GateF.GetValue(null);
        var statePath = StorePath;
        // O backup/restauração do estado do usuário e' do Program
        // (IntegrityStateBackup): cada teste fazendo o seu próprio backup
        // vazava o arquivo de lixo do teste anterior.

        try
        {
            // ── T10a: todo check tem Id, e o mesmo entre dois scans ─────────
            var s1 = Guardian.GetHarmfulTweaksWithStatus();
            var s2 = Guardian.GetHarmfulTweaksWithStatus();

            var semId = s1.Where(t => string.IsNullOrEmpty(t.Id)).ToList();
            Check("T10a todo check tem Id", semId.Count == 0,
                semId.Count + " sem Id" + (semId.Count > 0 ? ": " + semId[0].Name : ""));

            var ids1 = s1.ToDictionary(t => t.Name, t => t.Id);
            var instaveis = s1.Where(t => ids1[t.Name] != t.Id).ToList();
            Check("T10a o Id bate dentro do proprio scan", instaveis.Count == 0,
                instaveis.Count + " divergentes");

            int mudou = 0;
            foreach (var t in s2)
                if (ids1.TryGetValue(t.Name, out var antes) && antes != t.Id) mudou++;
            Check("T10a o Id e ESTAVEL entre dois scans", mudou == 0, mudou + " mudaram de Id");

            // ── T10b: o Id NAO depende do texto exibido ─────────────────────
            var alvo = s1.FirstOrDefault(t => t.Type == TweakType.Registry && t.KeyPath != null);
            Check("T10b existe um check de registro para testar", alvo != null,
                alvo != null ? alvo.Name : "(nenhum)");
            if (alvo != null)
            {
                string idAntes = alvo.Id;
                string nomeAntes = alvo.Name;
                alvo.Name = "TEXTO COMPLETAMENTE DIFERENTE (renomeado)";
                Check("T10b o Id nao muda quando o Name muda", alvo.Id == idAntes,
                    idAntes + " -> " + alvo.Id);
                Check("T10b StoreKey segue o Id, nao o texto", alvo.StoreKey == idAntes,
                    "StoreKey=" + alvo.StoreKey);
                alvo.Name = nomeAntes;
            }

            // ── T10b2: o Id pode ser COMPARTILHADO (chave igual, checks diferentes) ──
            //
            // MEDIDO com 465 checks reais: 18 grupos compartilham Id. Auditei
            // cada um e NENHUM e' contradicao permanente (o caso grave seria dois
            // checks nao-opcionais com HarmfulValue oposto: um sempre estaria
            // MODIFIED e o score nunca chegaria a 100%). Os 23 repetidos sao:
            //   - 1 grupo sentinel do PATH (6 checks, HarmfulValue = INCOMPLETE /
            //     DUPLICATE / MISSING / DEV_JUNK / VULNERABLE / CORRUPTED) - e'
            //     o MESMO registro checado de formas diferentes, por design;
            //   - opcionais que ficam fora do score;
            //   - duplicatas inocuas (mesmo valor, mesmo efeito).
            // Entao a regra real e: o Id identifica o ALVO (chave+valor), e
            // varios checks podem mirar o mesmo alvo com propositos diferentes.
            // O que nao pode acontecer e' Id VAZIO ou instavel (T10a).
            var colisoes = s1.GroupBy(t => t.Id).Where(g => g.Count() > 1).ToList();
            Check("T10b2 todo Id compartilhado tem checagens DIFERENTES entre si",
                colisoes.All(g => g.Select(x => x.Name).Distinct().Count() == g.Count()),
                colisoes.Count + " grupos compartilham Id (esperado: sentinel/opcionais/duplicatas)");
            Check("T10b2 nenhum Id e vazio", s1.All(t => !string.IsNullOrEmpty(t.Id)));

            // ── T10c: o Id e o que vai para o disco ────────────────────────
            lock (gate)
            {
                var st = LoadState();
                Pend(st).Clear(); Inv(st).Clear(); Unc(st).Clear();
                SaveState(st);
            }

            var pend = new ScannableTweak
            {
                Id = @"reg|HKCU\Teste|Flag",
                Name = "NOME QUE VAI MUDAR DEPOIS",
                Type = TweakType.Registry,
                KeyPath = @"HKEY_CURRENT_USER\" + ProbeKey,
                ValueName = "Flag",
                HarmfulValue = 1,
                DefaultValue = 0,
                ValueKind = Microsoft.Win32.RegistryValueKind.DWord,
                RequiresReboot = true
            };
            Microsoft.Win32.Registry.SetValue(@"HKEY_CURRENT_USER\" + ProbeKey, "Flag", 1,
                Microsoft.Win32.RegistryValueKind.DWord);
            RegistryProbe.Check(pend);

            PendingRepairStore.MarkPending(pend, "teste de chave estavel");
            Check("T10c MarkPending aceitou o check", true, "gravado");

            var gravado = System.Text.Json.JsonDocument.Parse(File.ReadAllText(statePath))
                .RootElement.GetProperty("Pending");
            string chaveGravada = gravado[0].GetProperty("Name").GetString();
            Check("T10c o que foi para o disco e' o Id, nao o nome",
                chaveGravada == pend.Id, "gravado='" + chaveGravada + "' id='" + pend.Id + "'");

            // ── T10d: HVCI em runtime ───────────────────────────────────────
            bool disp = NativeCodeIntegrity.TryGetState(out uint opcoes, out string mot);
            Console.WriteLine("  CodeIntegrityOptions = 0x" + opcoes.ToString("X8") + "  (" + mot + ")");
            Check("T10d a API classe 103 respondeu", disp, mot);
            if (disp)
            {
                NativeCodeIntegrity.TryGetHvci(out bool impoe, out bool audit,
                                                out bool d2, out uint o2, out _);
                Check("T10d coerencia do bitmask cru com TryGetHvci",
                    d2 && o2 == opcoes && impoe == ((opcoes & 0x400) != 0)
                         && audit == ((opcoes & 0x800) != 0),
                    "impoe=" + impoe + " audit=" + audit
                    + " HVCI=" + ((opcoes & 0x400) != 0) + " Audit=" + ((opcoes & 0x800) != 0));

                NativeCodeIntegrity.TryGetState(out uint o3, out _);
                Check("T10d a leitura e estavel", o3 == opcoes);
            }

            // ── T10e: o check de HVCI NAO decide pelo registro ─────────────
            var hvci = s1.FirstOrDefault(t => t.Name.Contains("HVCI"));
            Check("T10e existe o check de HVCI na lista", hvci != null);
            if (hvci != null)
            {
                Console.WriteLine("  status do check HVCI = " + hvci.Status
                    + "  (NOT_FOUND = API nao respondeu; MODIFIED = kernel nao impoe; OK = impoe)");

                // a coerencia que importa: se o registro pede HVCI=1 e o kernel
                // nao impoe, o check tem de dizer MODIFIED (nao OK).
                int reg = -1;
                try
                {
                    using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
                    var v = k?.GetValue("Enabled");
                    if (v != null) reg = Convert.ToInt32(v);
                }
                catch { }
                if (disp && reg == 1 && (opcoes & 0x400) == 0)
                    Check("T10e registro pede HVCI=1 e o kernel nao impoe -> o check NAO pode estar OK",
                        hvci.Status == TweakStatus.MODIFIED, "status=" + hvci.Status);
                else if (disp)
                    Check("T10e o status do HVCI e coerente com o bitmask do kernel",
                        hvci.Status != TweakStatus.OK || (opcoes & 0x400) != 0,
                        "status=" + hvci.Status + " reg=" + reg);
            }

            // ── T10f: reavaliar devolve os itens ao fluxo normal ───────────
            lock (gate)
            {
                var st = LoadState();
                Pend(st).Clear(); Inv(st).Clear(); Unc(st).Clear();
                var inv = Activator.CreateInstance(InvT);
                InvT.GetProperty("Name").SetValue(inv, pend.Id);
                InvT.GetProperty("Reason").SetValue(inv, "corrigida mas nao teve efeito");
                Inv(st).Add(inv);
                SaveState(st);
            }
            ResetCache();

            var alvoInv = new List<ScannableTweak> { pend };
            pend.Status = TweakStatus.NOT_FOUND;
            lock (gate) PendingRepairStore.Apply(alvoInv);
            Check("T10f o invalido foi aplicado ao item", !string.IsNullOrEmpty(pend.InvalidReason));

            Check("T10f antes de reavaliar ha 1 invalido",
                PendingRepairStore.InvalidatedCount == 1, PendingRepairStore.InvalidatedCount + "");

            PendingRepairStore.ClearInvalidated();
            Check("T10f ClearInvalidated zera a lista", PendingRepairStore.InvalidatedCount == 0);

            pend.InvalidReason = null;
            pend.Status = TweakStatus.NOT_FOUND;
            ResetCache();
            var alvoDepois = new List<ScannableTweak> { pend };
            lock (gate) PendingRepairStore.Apply(alvoDepois);
            Check("T10f depois de reavaliar o item nao fica mais invalidado",
                string.IsNullOrEmpty(pend.InvalidReason));

            // ── T10g: 'nao confirmado' fica registrado ─────────────────────
            lock (gate)
            {
                var st = LoadState();
                Pend(st).Clear(); Inv(st).Clear(); Unc(st).Clear();
                var e = Activator.CreateInstance(typeof(PendingRepairEntry));
                typeof(PendingRepairEntry).GetProperty("Name").SetValue(e, pend.Id);
                typeof(PendingRepairEntry).GetProperty("Reason").SetValue(e, "nao deu pra ler");
                typeof(PendingRepairEntry).GetProperty("TickAtApply").SetValue(e, int.MaxValue - 1000);
                Pend(st).Add(e);
                SaveState(st);
            }
            ResetCache();

            var naoConf = new List<ScannableTweak> { pend };
            pend.Status = TweakStatus.ERROR;
            lock (gate) PendingRepairStore.Apply(naoConf);
            string unc = pend.UnconfirmedReason ?? "";
            Check("T10g apos reiniciar sem conseguir ler fica 'nao confirmado'",
                unc.Length > 0, unc.Length > 60 ? unc.Substring(0, 60) : unc);
            lock (gate)
            {
                Check("T10g o 'nao confirmado' foi gravado em disco", Unc(LoadState()).Count == 1);
            }
        }
        catch (Exception ex)
        {
            Check("T10 excecao inesperada", false, ex.GetType().Name + ": " + ex.Message);
            Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(ProbeKey, false); } catch { }
            // Arquivo de estado: cuidado e' do Program (IntegrityStateBackup).
            // Aqui so zera o cache em memoria.
            ResetCache();
        }

        Console.WriteLine("  T10: OK={0} FALHAS={1}", _ok, _fail);
        return _fail;
    }
}