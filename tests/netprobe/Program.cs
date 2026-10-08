using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using KitLugia.Core;
using Microsoft.Win32;

// ===========================================================================
// netprobe - VERIFICACAO read-only dos metodos Core usados pela NetworkPage
// ===========================================================================
// Motivo: a NetworkPage foi corrigida em 06/10/2026 (UI + metodos). Este
// harness prova, contra ground truth, que a nova implementacao entrega os
// MESMOS dados do metodo antigo (PowerShell Get-NetAdapter) sem o custo de
// 2 spawns de powershell.exe por adaptador a cada tick de 3s.
//
// Regras:
//   - APENAS leituras. Metodos destrutivos (CleanNetworkSafe/Full,
//     ResetEthernetSettings, Optimize/Revert, SetAdapterState, spoof de MAC)
//     NAO sao executados aqui - alterariam a maquina de teste.
//   - Ground truth = Get-NetAdapter (a fonte que o codigo ANTIGO usava).
//   - Exit code 0 = todas as verificacoes passaram.
// ===========================================================================

internal static class Program
{
    private static int _pass;
    private static int _fail;

    private static void Ok(string msg) { _pass++; Console.WriteLine("[OK]    " + msg); }
    private static void Falha(string msg) { _fail++; Console.WriteLine("[FALHA] " + msg); }
    private static void Info(string msg) { Console.WriteLine("[....]  " + msg); }
    private static void Check(bool cond, string msg) { if (cond) Ok(msg); else Falha(msg); }

    private static string Clean(string mac) =>
        (mac ?? "").Trim().Replace("-", "").Replace(":", "").Replace(" ", "").ToUpperInvariant();

    // Ground truth: o MESMO Get-NetAdapter que o codigo antigo chamava (1x por
    // adaptador). Aqui no teste e' aceitavel - aqui nao roda no tick de 3s.
    private static Dictionary<string, (string Mac, bool Up)> GetNetAdapterTruth()
    {
        var map = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var output = SystemUtils.RunExternalProcess("powershell",
                "-NoProfile -Command \"Get-NetAdapter | ForEach-Object { $_.Name + '|' + $_.MacAddress + '|' + $_.Status }\"",
                hidden: true);
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('|');
                if (parts.Length < 3) continue;
                map[parts[0].Trim()] = (Clean(parts[1]), parts[2].Trim().Equals("Up", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex) { Info("ground truth indisponivel: " + ex.Message); }
        return map;
    }

    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== netprobe: verificacao dos metodos da NetworkPage (read-only) ===");
        Console.WriteLine("[NOTA]  Metodos destrutivos NAO executados (limpeza/reset/spoof).");
        Console.WriteLine();

        var truth = GetNetAdapterTruth();
        Info($"Get-NetAdapter (ground truth): {truth.Count} interfaces");

        // ------------------------------------------------------------------
        // T1 - ListPhysicalAdapters: timing (antes: 2 PowerShell por adaptador)
        // ------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("-- T1 ListPhysicalAdapters (timing / sem PowerShell por adaptador)");
        var sw = Stopwatch.StartNew();
        var list1 = AdapterManager.ListPhysicalAdapters();
        sw.Stop();
        var t1 = sw.ElapsedMilliseconds;
        sw.Restart();
        var list2 = AdapterManager.ListPhysicalAdapters();
        sw.Stop();
        var t2 = sw.ElapsedMilliseconds;

        Info($"{list1.Count} adaptadores fisicos; 1a chamada={t1}ms, 2a chamada={t2}ms");
        Check(list1.Count > 0, "ListPhysicalAdapters encontrou adaptadores fisicos");
        // Codigo antigo: 2 spawns (~1s cada) por adaptador por chamada -> >= 2*N segundos
        // em N adaptadores. Limite generoso de 3s prova que nao ha spawn por adaptador.
        Check(t2 < 3000, $"refresh < 3s sem spawn de PowerShell por adaptador ({t2}ms)");

        // ------------------------------------------------------------------
        // T2 - Equivalencia com Get-NetAdapter (metodo antigo)
        // ------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("-- T2 equivalencia vs Get-NetAdapter (MAC ao vivo + Status)");
        int matched = 0, mismatches = 0;
        foreach (var adp in list2)
        {
            if (!truth.TryGetValue(adp.ConnectionName, out var ps))
            {
                Info($"sem equivalente no Get-NetAdapter: '{adp.ConnectionName}' (nome/mapeamento)");
                continue;
            }
            matched++;

            if (!string.IsNullOrEmpty(adp.CurrentMac))
            {
                if (Clean(adp.CurrentMac) != ps.Mac)
                {
                    mismatches++;
                    Falha($"MAC diferente para '{adp.ConnectionName}': .NET={adp.CurrentMac} vs PS={ps.Mac}");
                }
            }
            else
            {
                Info($"MAC ao vivo vazio para '{adp.ConnectionName}' (adaptador desabilitada?); fallback registro aplicado");
            }

            if (adp.IsUp != ps.Up)
            {
                mismatches++;
                Falha($"IsUp diferente para '{adp.ConnectionName}': .NET={adp.IsUp} vs PS(Status Up)={ps.Up}");
            }
        }
        Check(matched > 0, $"pelo menos 1 adaptador comparavel com o metodo antigo ({matched})");
        if (matched > 0 && mismatches == 0)
            Ok($"todos os {matched} adaptadores identicos ao Get-NetAdapter (MAC + Status)");

        Check(list2.All(a => !string.IsNullOrEmpty(a.Description) && !string.IsNullOrEmpty(a.ConnectionName)),
            "Description/ConnectionName preenchidos em todos");

        // ------------------------------------------------------------------
        // T3 - GetCurrentMac
        // ------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("-- T3 GetCurrentMac (.NET, sem PowerShell)");
        foreach (var adp in list2)
        {
            var mac = AdapterManager.GetCurrentMac(adp.ConnectionName);
            if (truth.TryGetValue(adp.ConnectionName, out var ps) && !string.IsNullOrEmpty(ps.Mac))
                Check(mac == ps.Mac || string.IsNullOrEmpty(mac),
                    $"GetCurrentMac('{adp.ConnectionName}') = {mac} (PS={ps.Mac})");
            else
                Info($"GetCurrentMac('{adp.ConnectionName}') = {mac} (sem ground truth)");
        }

        // ------------------------------------------------------------------
        // T4 - GetPermanentMac: tabela unica + cache
        // ------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("-- T4 GetPermanentMac (tabela unica PS + cache por adaptador)");
        foreach (var adp in list2.Take(3))
        {
            sw.Restart();
            var p1 = AdapterManager.GetPermanentMac(adp.Id, adp.NetCfgInstanceId, adp.ConnectionName);
            sw.Stop();
            var ta = sw.ElapsedMilliseconds;
            sw.Restart();
            var p2 = AdapterManager.GetPermanentMac(adp.Id, adp.NetCfgInstanceId, adp.ConnectionName);
            sw.Stop();
            var tb = sw.ElapsedMilliseconds;

            Info($"'{adp.ConnectionName}': 1a={ta}ms 2a={tb}ms valor={p2}");
            Check(p1 == p2, $"'{adp.ConnectionName}': cache coerente entre chamadas");
            if (p2.Length == 12)
                Check(tb <= ta + 5, $"'{adp.ConnectionName}': 2a chamada nao mais lenta que a 1a (cache)");
            else
                Info($"'{adp.ConnectionName}': PermanentAddress indisponivel (aceitavel: registry sem a chave)");
        }

        // ------------------------------------------------------------------
        // T5 - GetAdapterWithHighestUsage (.NET puro vs PowerShell+CSV antigo)
        // ------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("-- T5 GetAdapterWithHighestUsage (.NET puro, sem PowerShell/CSV)");
        sw.Restart();
        var (hiName, hiType, hiDesc, hiGuid) = Toolbox.GetAdapterWithHighestUsage();
        sw.Stop();
        Info($"'{hiName}' ({hiType}) '{hiDesc}' guid='{hiGuid}' em {sw.ElapsedMilliseconds}ms");
        Check(sw.ElapsedMilliseconds < 2000, $"deteccao < 2s ({sw.ElapsedMilliseconds}ms)");
        if (hiName != "Desconhecido" && hiName != "Erro")
        {
            Check(NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == hiName),
                $"adaptador '{hiName}' existe nas interfaces do sistema");
            Check(hiType == "Ethernet" || hiType == "WiFi", $"tipo valido: {hiType}");
        }
        else
        {
            Info("sem adaptador com gateway (aceitavel em rede isolada)");
        }

        // ------------------------------------------------------------------
        // T6 - Leitores de status vs registro bruto (valida fixes)
        // ------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("-- T6 leitores de status vs registro bruto");

        // Fix 1: IsNetworkThrottlingDisabled (bug: retornava true para qualquer valor != 10)
        var rawNt = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
            "NetworkThrottlingIndex", 10);
        long vNt = Convert.ToInt64(rawNt);
        bool expectThrottlingDisabled = vNt == -1 || vNt == 0xFFFFFFFF;
        Info($"NetworkThrottlingIndex = {vNt} (esperado desativado: {expectThrottlingDisabled})");
        Check(SystemTweaks.IsNetworkThrottlingDisabled() == expectThrottlingDisabled,
            "IsNetworkThrottlingDisabled coerente com o valor real do registro");

        // Fix 2: IsInterruptModerationDisabled (class key = local real do driver)
        bool classHasZero = false;
        try
        {
            using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var ck = lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}");
            foreach (var sub in ck?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                if (!int.TryParse(sub, out _)) continue;
                using var ak = ck.OpenSubKey(sub);
                if (ak?.GetValue("*InterruptModeration")?.ToString() == "0") { classHasZero = true; break; }
            }
        }
        catch { /* leitura falhou -> false */ }
        bool legacyHasZero = false;
        try
        {
            using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var tk = lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters");
            using var ik = tk?.OpenSubKey("Interfaces");
            foreach (var sub in ik?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var sk = ik.OpenSubKey(sub);
                if (sk?.GetValue("*InterruptModeration")?.ToString() == "0") { legacyHasZero = true; break; }
            }
        }
        catch { /* leitura falhou -> false */ }
        Info($"InterruptModeration '0': classKey={classHasZero}, legado(Tcpip\\Interfaces)={legacyHasZero}");
        Check(Toolbox.IsInterruptModerationDisabled() == (classHasZero || legacyHasZero),
            "IsInterruptModerationDisabled cobre class key + legado");

        // Leitores restantes: apenas sanidade (nao lancam excecao)
        try
        {
            Info($"CTCP={Toolbox.IsCTCPConfigured()} RSS={Toolbox.IsRSSEnabled()} " +
                 $"TaskOffload={Toolbox.IsTaskOffloadEnabled()} NagleOff={Toolbox.IsNagleAlgorithmDisabled()} " +
                 $"TcpRegistry={Toolbox.IsTcpRegistryTweaksApplied()}");
            Ok("demais leitores de status executaram sem excecao");
        }
        catch (Exception ex) { Falha("leitor de status lancou excecao: " + ex.Message); }

        // ------------------------------------------------------------------
        // T7 - DNS: GetActiveDnsInfo + providers do benchmark
        // ------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("-- T7 DNS (GetActiveDnsInfo + lista do benchmark)");
        try
        {
            var dns = Toolbox.GetActiveDnsInfo();
            Info($"DNS ativo: {dns.Provider} ({dns.DnsIp})");
            Check(!string.IsNullOrEmpty(dns.Provider), "GetActiveDnsInfo retornou provedor");
        }
        catch (Exception ex) { Falha("GetActiveDnsInfo lancou excecao: " + ex.Message); }

        try
        {
            var providers = DnsBenchmark.GetDefaultProviders();
            Check(providers.Count >= 20, $"lista de provedores do benchmark ({providers.Count})");

            // Benchmark em 2 provedores (rede real): tem de terminar com estado definido
            var amostra = providers.Take(2).ToList();
            var benchTask = DnsBenchmark.BenchmarkAsync(amostra);
            var finished = benchTask.Wait(TimeSpan.FromSeconds(45));
            Check(finished, "BenchmarkAsync terminou em 45s");
            if (finished)
            {
                var res = benchTask.Result;
                Check(res.All(p => p.TestState != DnsTestState.Testing),
                    "estados finais definidos (Done/Failed), spinner nao fica preso em Testing");
                foreach (var p in res)
                    Info($"{p.Name}: resolve={p.ResolveMs}ms icmp={p.LatencyMs}ms state={p.TestState} via={p.MeasuredVia}");
            }
        }
        catch (Exception ex) { Falha("benchmark lancou excecao: " + ex.Message); }

        // ------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine($"=== RESUMO: {_pass} ok, {_fail} falha(s) ===");
        return _fail == 0 ? 0 : 1;
    }
}
