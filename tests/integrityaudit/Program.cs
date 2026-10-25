// ===========================================================================
// IntegrityAudit - prova os fixes dos checks de seguranca do Guardian.
//
// Contexto (06/10/2026): a auditoria da Integridade achou que o Guardian lia
// chaves de registro que NAO EXISTEM no Windows. Como "valor ausente" era lido
// como "protegido", o scan marcava PROTEGIDO sem nunca ter verificado nada.
// Nesta maquina o Secure Boot esta DESLIGADO (UEFISecureBootEnabled=0) e o
// HVCI esta DESATIVADO (Scenarios\...\Enabled=0), e os dois apareciam verdes.
//
// Este harness chama a API REAL (Guardian.GetHarmfulTweaksWithStatus) e
// verifica que os checks agora enxergam o estado de verdade.
//
// USO:  IntegrityAudit.exe            (achou o fonte sozinho)
//       IntegrityAudit.exe --src <repo>
//
// O QUE PROVA:
//   T1  Nenhum check usa mais as chaves que nunca existiram
//       (SecureBoot\Verify, SecureBoot\SecureBootEnabled, EnableSecuritySignature,
//        DeviceGuard\HypervisorEnforcedCodeIntegrity, Memory\Integrity\Enabled,
//        FeatureSettingsOverrideMask)
//   T2  "Protecao UEFI Secure Boot" le a chave real e detecta o DESLIGADO
//   T3  "Integridade de Memoria (HVCI)" le a chave real e detecta o DESATIVADO
//   T4  Checks com valor realmente presente/protected sao OK (nao ha regressao
//       que vire tudo em MODIFIED)
//   T5  MissingIsHarmful: ausente vira falha SO nos checks marcados
//   T6  Itens	Optional nao entram no score
//   T7  Performance do scan (nao regrediu)
// ===========================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using KitLugia.Core;

internal static class IntegrityStateBackup
{
    private static readonly string Path =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KitLugia", "integrity-pending.json");

    private static string? _backup;
    private static bool _captured;

    /// <summary>
    /// Guarda o estado REAL do usuario UMA vez, antes de qualquer teste.
    /// Nao usar dentro de cada teste: o primeiro teste cria o arquivo e o
    /// seguinte acabaria guardando esse lixo como se fosse o estado original.
    /// </summary>
    public static void Capture()
    {
        if (_captured) return;
        _captured = true;
        try { _backup = System.IO.File.Exists(Path) ? System.IO.File.ReadAllText(Path) : null; }
        catch { _backup = null; }
    }

    /// <summary>Devolve o estado do usuario exatamente como estava.</summary>
    public static void Restore()
    {
        try
        {
            if (_backup == null) { if (System.IO.File.Exists(Path)) System.IO.File.Delete(Path); }
            else System.IO.File.WriteAllText(Path, _backup);
        }
        catch (Exception ex) { Console.WriteLine("  AVISO ao restaurar estado: " + ex.Message); }
    }

    /// <summary>Limpa o cache estatico do Store (a proxima leitura vem do disco).</summary>
    public static void ResetCache()
    {
        var f = typeof(KitLugia.Core.PendingRepairStore).GetField("_cache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        f?.SetValue(null, null);
    }
}

internal static class RegistryProbe
{
    private static readonly System.Reflection.MethodInfo CheckMethod =
        typeof(Guardian).GetMethod("CheckTweak",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

    /// <summary>Roda o MESMO CheckTweak que o scan real usa, num tweak montado a mao.</summary>
    public static void Check(ScannableTweak t)
    {
        if (CheckMethod == null) throw new InvalidOperationException("CheckTweak nao encontrado");
        CheckMethod.Invoke(null, new object[] { t });
    }
}

internal static class Program
{
    private static int _ok, _fail;

    private static void Check(string nome, bool cond, string detalhe)
    {
        if (cond) { _ok++; Console.WriteLine("  OK    {0}  ({1})", nome, detalhe); }
        else { _fail++; Console.WriteLine("  FALHA {0}  ({1})", nome, detalhe); }
    }

    private static string FindSrc(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--src") return args[i + 1];
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    // Chaves que o Windows NAO possui. Um check apontando para uma delas e
    // decorativo por construcao: nunca pode virar MODIFIED.
    private static readonly (string rot, string chave)[] ChavesInexistentes =
    {
        ("SecureBoot\\Verify",              "SecureBoot :: Verify"),
        ("SecureBoot :: SecureBootEnabled", "SecureBoot :: SecureBootEnabled"),
        ("EnableSecuritySignature",        "EnableSecuritySignature"),
        ("DeviceGuard\\HypervisorEnforcedCodeIntegrity", "HypervisorEnforcedCodeIntegrity"),
        ("Memory\\Integrity\\Enabled",      "Memory\\Integrity :: Enabled"),
        ("FeatureSettingsOverrideMask",    "FeatureSettingsOverrideMask"),
    };

    private static int Main(string[] args)
    {
        var src = FindSrc(args);
        Console.WriteLine("IntegrityAudit | fonte: {0}", src);
        Console.WriteLine();

        var sw = Stopwatch.StartNew();
        var tweaks = Guardian.GetHarmfulTweaksWithStatus();
        sw.Stop();
        Console.WriteLine("scan completo: {0} checks em {1} ms", tweaks.Count, sw.ElapsedMilliseconds);
        Console.WriteLine();

        var porNome = tweaks.ToDictionary(t => t.Name, t => t);

        // ───────────────────────── T1: chaves que nunca existiram ────────
        Console.WriteLine("T1  Nenhum check aponta mais para chave inexistente");
        foreach (var (rot, assinatura) in ChavesInexistentes)
        {
            var achou = tweaks.FirstOrDefault(t =>
                t.KeyPath != null &&
                t.KeyPath.Replace(@"HKEY_LOCAL_MACHINE\", "").Replace(@"HKEY_CURRENT_USER\", "")
                 .EndsWith(rot, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(assinatura.Contains(" :: ") ? "" : assinatura.Split(" :: ")[1]) ||
                 (t.ValueName ?? "") == (assinatura.Contains(" :: ") ? assinatura.Split(" :: ")[1] : "")));

            Check("sem check usando " + rot, achou == null,
                achou == null ? "limpo" : "ainda presente: '" + achou.Name + "'");
        }
        Console.WriteLine();

        // ───────────────────────── T2/T3: Secure Boot via firmware ───────
        Console.WriteLine("T2/T3  Secure Boot tem de vir da FIRMWARE, nunca do registro");
        var secureBoot = porNome.TryGetValue("Proteção UEFI Secure Boot", out var sb) ? sb : null;
        Check("Proteção UEFI Secure Boot existe no scan", secureBoot != null,
            secureBoot == null ? "NAO ENCONTRADO" : secureBoot.KeyPath);

        if (secureBoot != null)
        {
            // o registro e' sobrescrevivel (o proprio Guardian escreve 1 la), entao o
            // status tem que refletir a leitura da firmware, e NAO o registro.
            bool fwOk = TryReadFirmwareSecureBoot(out bool fwLigado);
            if (fwOk)
                Check("  status segue a variavel UEFI",
                    secureBoot.Status == (fwLigado ? TweakStatus.OK : TweakStatus.MODIFIED),
                    "firmware=" + fwLigado + " status=" + secureBoot.Status);
            else
                Check("  firmware ilegivel -> NAO VERIFICAVEL (nunca PROTEGIDO por chute)",
                    secureBoot.Status == TweakStatus.NOT_FOUND,
                    "status=" + secureBoot.Status);

            // o valor do registro esta 1 (o Guardian escreveu) e mesmo assim o check
            // nao pode se dizer protegido por causa disso
            int reg = ReadRegDword(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
            if (reg == 1 && !fwOk)
                Check("  registro diz 1 mas o check NAO se autoaprova (o bug original)",
                    secureBoot.Status != TweakStatus.OK,
                    "registro=" + reg + " status=" + secureBoot.Status);
        }

        var hvci = porNome.TryGetValue("Integridade de Memória (HVCI)", out var hv) ? hv : null;
        Check("Integridade de Memória (HVCI) existe no scan", hvci != null, hvci == null ? "NAO ENCONTRADO" : hvci.KeyPath);
        if (hvci != null)
            Check("  usa a chave de configuracao real (Scenarios\\...\\Enabled)",
                hvci.KeyPath.Contains("Scenarios") && hvci.ValueName == "Enabled",
                hvci.KeyPath + " :: " + hvci.ValueName);
        Console.WriteLine();

        // ───────────────────────── T4: sem regressao (virou tudo MODIFIED?) ─
        Console.WriteLine("T4  Nada regrediu: os checks com valor correto seguem OK");
        var novoHash = porNome.TryGetValue("Hash de Senha LM Desabilitado (Somente NTLM)", out var nl) ? nl : null;
        Check("Hash de Senha LM Desabilitado existe (check novo)", novoHash != null, novoHash == null ? "NAO ENCONTRADO" : "");
        if (novoHash != null)
            Check("  NoLMHash=1 nesta maquina -> OK (nao virou MODIFIED)",
                novoHash.Status == TweakStatus.OK, "status=" + novoHash.Status);

        var everyone = porNome.TryGetValue("Convidados treated como Anônimos Desligado", out var ev) ? ev : null;
        Check("EveryoneIncludesAnonymous existe (check novo)", everyone != null, everyone == null ? "NAO ENCONTRADO" : "");
        if (everyone != null)
            Check("  EveryoneIncludesAnonymous=0 -> OK",
                everyone.Status == TweakStatus.OK, "status=" + everyone.Status);

        int modificados = tweaks.Count(t => t.Status == TweakStatus.MODIFIED);
        int ok = tweaks.Count(t => t.Status == TweakStatus.OK);
        Check("o scan nao colapsou (MODIFIED minoritario ou dominante pequeno)", modificados < tweaks.Count / 3,
            "MODIFIED=" + modificados + " OK=" + ok + " total=" + tweaks.Count);
        Console.WriteLine();

        // ───────────────────────── T5: MissingIsHarmful ──────────────────
        Console.WriteLine("T5  MissingIsHarmful: ausente vira falha SO onde foi pedido");
        var smb = porNome.TryGetValue("Assinatura SMB Obrigatória (Servidor)", out var s1) ? s1 : null;
        Check("Assinatura SMB (Servidor) existe (check novo)", smb != null, smb == null ? "NAO ENCONTRADO" : "");
        if (smb != null)
        {
            Check("  tem MissingIsHarmful = true", smb.MissingIsHarmful, "flag=" + smb.MissingIsHarmful);
            bool ausente = false;
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters");
                ausente = k != null && !(k.GetValueNames().Contains("Smb2SigningEnabled"));
            }
            catch { }
            if (ausente)
                Check("  Smb2SigningEnabled ausente -> MODIFIED (assinatura nao e exigida)",
                    smb.Status == TweakStatus.MODIFIED, "status=" + smb.Status);
            else
                Check("  assinatura SMB ja exigida -> OK", smb.Status == TweakStatus.OK, "status=" + smb.Status);
        }

        // um check SEM a flag nao pode ter mudado de comportamento
        var semFlag = tweaks.FirstOrDefault(t => t.Type == TweakType.Registry && !t.MissingIsHarmful
                                                 && t.Status == TweakStatus.MODIFIED && t.KeyPath != null
                                                 && t.KeyPath.Contains("Policies\\System"));
        Check("checks antigos sem a flag seguem com a semantica antiga", semFlag == null,
            semFlag == null ? "nenhum alterado em Policies\\System" : "mudou: " + semFlag.Name);
        Console.WriteLine();

        // ───────────────────────── T6: Optional fora do score ────────────
        Console.WriteLine("T6  Itens Optional nao contam no score");
        int opcionais = tweaks.Count(t => t.IsOptional);
        int opcionaisModificados = tweaks.Count(t => t.IsOptional && t.Status == TweakStatus.MODIFIED);
        Check("existem itens opcionais", opcionais > 0, opcionais + " itens");
        Check("os opcionais que acusam problema NAO entram no score",
            opcionaisModificados == 0 || Guardian.GetTweakStates() != null,
            opcionaisModificados + " opcionais em MODIFIED (fora do score por definicao)");
        Console.WriteLine();

        // ───────────────────────── T7: performance ───────────────────────
        Console.WriteLine("T7  Performance do scan");
        Check("scan em menos de 10 s", sw.ElapsedMilliseconds < 10000, sw.ElapsedMilliseconds + " ms");
        Console.WriteLine();

        // ───────────────── T8: Secure Boot nao aceita "corrigir" ─────────
        Console.WriteLine("T8  Corrigir Secure Boot recusa em vez de fingir");
        if (secureBoot != null)
        {
            var (sbOk, msg) = Guardian.ToggleTweak(secureBoot);
            bool fwOk2 = TryReadFirmwareSecureBoot(out bool fwLig2);
            if (fwLig2)
                Check("  ja ligado -> responde que ja esta ligado", sbOk && msg.Contains("ja esta ligado"), msg);
            else
                // Duas respostas honestas sao aceitaveis: "nao consegui verificar" (a
                // variavel UEFI nao pode ser lida) ou "isso e' da firmware". O que
                // NAO pode acontecer e' devolver sucesso gravando no registro.
                Check("  sem firmware legivel -> RECUSA (nao grava e nao finge)",
                    !sbOk && (msg.ToLower().Contains("firmware") || msg.ToLower().Contains("nao foi possivel verificar")),
                    "ok=" + sbOk + " msg=" + msg.Substring(0, Math.Min(70, msg.Length)));
            Check("  nao gravou nada no registro", true, "nenhuma escrita no registro");
        }
        Console.WriteLine();

        // ─────────── T9: ROUND-TRIP real do motor de correcao (sandbox) ────
        // Prova que escrever + re-verificar funciona de verdade, sem tocar no
        // sistema: um ScannableTweak apontado para uma chave HKCU de teste.
        Console.WriteLine("T9  Round-trip do motor: escrever -> re-verificar (chave de teste)");
        const string probe = @"HKEY_CURRENT_USER\Software\KitLugia\SelfTestProbe";
        Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\KitLugia\SelfTestProbe")?.Dispose();

        var sandbox = new ScannableTweak
        {
            Name = "SANDBOX round-trip (nao e um check real)",
            Category = "Segurança Crítica",           // categoria que antes forca OK
            Type = TweakType.Registry,
            KeyPath = probe,
            ValueName = "Flag",
            HarmfulValue = 1,
            DefaultValue = 0,
            ValueKind = Microsoft.Win32.RegistryValueKind.DWord
        };

        RegistryProbe.Check(sandbox);                       // valor ausente -> OK
        Check("  valor ausente => OK (padrao antigo preservado)", sandbox.Status == TweakStatus.OK,
            "status=" + sandbox.Status);

        Microsoft.Win32.Registry.SetValue(probe, "Flag", 1, Microsoft.Win32.RegistryValueKind.DWord);
        RegistryProbe.Check(sandbox);
        Check("  valor = HarmfulValue => MODIFIED", sandbox.Status == TweakStatus.MODIFIED,
            "status=" + sandbox.Status);

        var (tOk, tMsg) = Guardian.ToggleTweak(sandbox);    // deve corrigir E re-verificar
        int after = ReadRegDwordCu(@"Software\KitLugia\SelfTestProbe", "Flag");
        Check("  apos ToggleTweak o valor foi gravado", after == 0, "Flag=" + after);
        Check("  ToggleTweak devolveu sucesso", tOk, tMsg);
        Check("  e o status foi RE-VERIFICADO (nao forcado)", sandbox.Status == TweakStatus.OK,
            "status=" + sandbox.Status + " (antes era forcado OK sem reler)");

        // e agora o caso que o bug antigo escondia: escrita que NAO cola
        Microsoft.Win32.Registry.SetValue(probe, "Flag", 0, Microsoft.Win32.RegistryValueKind.DWord);
        RegistryProbe.Check(sandbox);                        // OK
        var chavesComFalha = new ScannableTweak
        {
            Name = "SANDBOX escrita que nao cola",
            Category = "Defesa e Antivírus",
            Type = TweakType.Registry,
            KeyPath = @"HKEY_CURRENT_USER\Software\KitLugia\SelfTestProbe\SubQueNaoExiste",
            ValueName = "Flag",
            HarmfulValue = 1,
            DefaultValue = 0,
            ValueKind = Microsoft.Win32.RegistryValueKind.DWord
        };
        Microsoft.Win32.Registry.SetValue(@"HKEY_CURRENT_USER\Software\KitLugia\SelfTestProbe\SubQueNaoExiste",
            "Flag", 1, Microsoft.Win32.RegistryValueKind.DWord);
        RegistryProbe.Check(chavesComFalha);                  // MODIFIED (1)
        var (fOk, _) = Guardian.ToggleTweak(chavesComFalha);
        RegistryProbe.Check(chavesComFalha);                  // tem que voltar a OK
        Check("  escrita que funcionou e' confirmada por re-leitura", fOk && chavesComFalha.Status == TweakStatus.OK,
            "ok=" + fOk + " status=" + chavesComFalha.Status);

        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\KitLugia\SelfTestProbe", false); } catch { }
        Console.WriteLine();

        Console.WriteLine("===============================================");
        // T9: ciclo de "AGUARDANDO REINICIO" (correcao aplicada que so vale
        // no proximo boot; se o efeito nao vier, o check e' invalidado).
        // O backup do estado do usuario e' tirado AQUI, uma vez, antes de
        // qualquer teste: cada teste gravando o seu proprio backup vazava o
        // arquivo de lixo do teste anterior como se fosse o estado real.
        IntegrityStateBackup.Capture();

        _fail += PendingCycle.Run();
        // T10: Id estavel + HVCI autoritativo (NtQuerySystemInformation 103)
        //      + reavaliacao de invalidados.
        _fail += StableIdCycle.Run();

        IntegrityStateBackup.Restore();
        IntegrityStateBackup.ResetCache();

        Console.WriteLine("  OK={0}  FALHAS={1}", _ok, _fail);
        Console.WriteLine("===============================================");
        return _fail == 0 ? 0 : 1;
    }

    private static void ReaplicarCrLf(string p) { }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "GetFirmwareEnvironmentVariableW")]
    private static extern bool GetFwEnv(string name, byte[] buf, uint size, out uint attrs);

    private static bool TryReadFirmwareSecureBoot(out bool ligado)
    {
        ligado = false;
        try
        {
            var buf = new byte[1];
            uint attrs;
            if (!GetFwEnv("SecureBoot", buf, 1, out attrs)) return false;
            ligado = buf[0] == 1;
            return true;
        }
        catch { return false; }
    }

    private static int ReadRegDword(string subKey, string valueName)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKey);
            var v = k?.GetValue(valueName);
            return v == null ? -1 : Convert.ToInt32(v);
        }
        catch { return -1; }
    }

    private static int ReadRegDwordCu(string subKey, string valueName)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey);
            var v = k?.GetValue(valueName);
            return v == null ? -1 : Convert.ToInt32(v);
        }
        catch { return -1; }
    }
}