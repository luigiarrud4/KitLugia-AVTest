// ===========================================================================
// T11 - INVARIANTES DO CATALOGO (auditoria de 29/10/2026).
//
// POR QUE ESTE TESTE EXISTE: o catalogo da Integridade tinha 463 checks e 248
// deles nao podiam verificar o que o nome prometia — valor de registro
// inventado (`Corrupted`, `OrphanedEntries`, `TooManyDisabled`, `SMARTEnabled`),
// chave que nao existe em Windows nenhum (`SystemProtection\Enable`,
// `ExploitProtection\Enabled`, `Servicing\Version`) ou sentinela que o proprio
// `CheckTweak` nao interpreta (`EMPTY_OR_WRONG`, `EXPLORER_OVERRIDDEN`,
// `EMPTY`, `LOADED`). Como "valor ausente" e' lido como OK, esses itens
// apareciam PROTEGIDOS sem nunca ter verificado nada.
//
// Este ciclo trava a correcao: qualquer check novo que volte a usar valor
// fabricado, sentinela morta, alvo inexistente, harm==default (o "corrigir"
// gravaria o proprio valor perigoso) ou contradicao de Id faz o harness falhar.
//
// NAO TOCA NO SISTEMA: so le o catalogo em memoria.
// ===========================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using KitLugia.Core;
using Microsoft.Win32;

internal static class CatalogAudit
{
    private static int _ok, _fail;

    /// <summary>Quantos checks passaram (o resumo final soma todas as fases).</summary>
    public static int LastOk { get; private set; }

    private static void Check(string what, bool cond, string det = "")
    {
        if (cond) { _ok++; Console.WriteLine("  OK   " + what + (det.Length > 0 ? "  [" + det + "]" : "")); }
        else { _fail++; Console.WriteLine("  FALHA " + what + "  [" + det + "]"); }
    }

    /// <summary>
    /// Sentinelas que o `CheckTweak` NAO interpreta: cairiam na comparacao
    /// literal de string e o check nunca acusaria nada. Os itens de PATH sao
    /// excecao legitima — ali o HarmfulValue e' um SELETOR de problema lido por
    /// `CheckPathProblems` (INCOMPLETE/DUPLICATE/MISSING/DEV_JUNK/VULNERABLE/
    /// CORRUPTED).
    /// </summary>
    private static readonly string[] SentinelasMortas =
    {
        "EMPTY_OR_WRONG", "EXPLORER_OVERRIDDEN", "EXISTS_OR_EMPTY", "LOADED", "EMPTY", "CORRUPT", "FAKE"
    };

    /// <summary>Nomes de valor que nao existem em Windows nenhum (valores fabricados).</summary>
    private static readonly string[] ValoresFabricados =
    {
        "Corrupted", "CorruptedHandlers", "OrphanedEntries", "OrphanedComponents", "OrphanedRefs",
        "TooManyDisabled", "InvalidEntries", "StaleEntries", "RegistryPermissions", "CorruptedProfile",
        "FailedPolicyCount", "AutoStartFailed", "OrphanedHandles", "CorruptCount"
    };

    private static bool EhItemDePath(ScannableTweak t)
        => t.Name.Contains("PATH", StringComparison.OrdinalIgnoreCase) && t.ValueName == "Path";

    private static string Str(object? v) => v?.ToString() ?? "";

    /// <summary>Chave de POLITICA: o Windows so' a cria quando a GPO e' aplicada.</summary>
    private static bool EhCaminhoDePolitica(string keyPath)
        => keyPath.Contains("\\POLICIES\\", StringComparison.OrdinalIgnoreCase)
           || keyPath.EndsWith("\\POLICIES", StringComparison.OrdinalIgnoreCase);

    /// <summary>Chave de servico/driver: ausente quando o componente nao esta instalado.</summary>
    private static bool EhCaminhoDeServico(string keyPath)
        => keyPath.IndexOf("\\CurrentControlSet\\Services\\", StringComparison.OrdinalIgnoreCase) >= 0;

    private static RegistryKey? RaizDe(string keyPath)
    {
        if (keyPath.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase)
            || keyPath.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase)) return Registry.CurrentUser;
        if (keyPath.StartsWith("HKEY_CLASSES_ROOT", StringComparison.OrdinalIgnoreCase)
            || keyPath.StartsWith("HKCR", StringComparison.OrdinalIgnoreCase)) return Registry.ClassesRoot;
        if (keyPath.StartsWith("HKEY_USERS", StringComparison.OrdinalIgnoreCase)
            || keyPath.StartsWith("HKU", StringComparison.OrdinalIgnoreCase)) return Registry.Users;
        return Registry.LocalMachine;
    }

    private static string SubCaminho(string keyPath)
    {
        int i = keyPath.IndexOf('\\');
        return i < 0 ? "" : keyPath[(i + 1)..];
    }

    private static bool RegistroExiste(string keyPath)
    {
        try
        {
            using var raiz = RaizDe(keyPath);
            using var k = raiz?.OpenSubKey(SubCaminho(keyPath));
            return k != null;
        }
        catch { return false; }
    }

    private static HashSet<string> LerServicosInstalados()
    {
        var nomes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services != null)
                foreach (var n in services.GetSubKeyNames()) nomes.Add(n);
        }
        catch { }
        return nomes;
    }

    /// <summary>
    /// Token TUDO_MAIUSCULO com letra: formato de sentinela, nao de valor real.
    /// Numero puro (bitmask como `67108863`) e' valor legitimo e nao entra.
    /// </summary>
    private static bool CaraDeSentinela(string v)
    {
        if (v.Length < 4) return false;
        bool temLetra = false;
        foreach (var c in v)
        {
            if (!(char.IsUpper(c) || char.IsDigit(c) || c == '_')) return false;
            if (char.IsUpper(c)) temLetra = true;
        }
        return temLetra;
    }

    /// <summary>
    /// Nomes de servico REAIS do Windows que este build nao instalou. Nao e'
    /// lista de conveniencia: cada entrada precisa de um motivo verificavel, e
    /// um nome novo que nao exista na maquina e nao esteja aqui REPROVA (foi
    /// assim que `BTHSSVC`, `PushNotificationsUserService` e `SyncHost`
    /// entraram e viraram NOT_FOUND eterno).
    /// </summary>
    private static readonly Dictionary<string, string> ServicosReaisNaoInstalados =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["wudfsvc"] = "Windows Driver Foundation (servico real); nesta maquina o wudfsvc.dll nao esta instalado",
        };

    /// <summary>Nome que denuncia contador/estado inventado: *Count, *Entries, *Status, *Handlers...</summary>
    private static readonly string[] SufixosDeContador =
    {
        "Count", "Entries", "Handlers", "Components", "Refs", "Size", "Status", "Problems", "Errors", "Flags"
    };

    private static bool NomeDeValorSuspeito(string valueName)
        => SufixosDeContador.Any(s => valueName.EndsWith(s, StringComparison.OrdinalIgnoreCase))
           || ValoresFabricados.Contains(valueName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// O valor existe no alvo do proprio check? Se sim, nao ha' o que suspeitar
    /// (o item le um valor real, mesmo com nome de contador).
    /// </summary>
    private static bool ValorExisteEmAlgumLugar(List<ScannableTweak> regs, string valueName, string keyPath)
    {
        try
        {
            using var raiz = RaizDe(keyPath);
            using var k = raiz?.OpenSubKey(SubCaminho(keyPath));
            if (k != null && k.GetValueNames().Any(n => string.Equals(n, valueName, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        catch { }
        return false;
    }

    public static int Run()
    {
        Console.WriteLine();
        Console.WriteLine("--- T11: invariantes do catalogo (auditoria 29/10) ---");

        var defs = Guardian.GetAllTweaksDefinition();
        Check("T11a o catalogo tem checks", defs.Count > 100, defs.Count + " checks");

        // ── T11b: campos obrigatorios ─────────────────────────────────────
        var semNome = defs.Where(t => string.IsNullOrWhiteSpace(t.Name)).ToList();
        Check("T11b todo check tem Nome", semNome.Count == 0, semNome.Count + " sem nome");

        var semCategoria = defs.Where(t => string.IsNullOrWhiteSpace(t.Category)).ToList();
        Check("T11b todo check tem Categoria",
            semCategoria.Count == 0, semCategoria.Count + " sem categoria");

        var semDescricao = defs.Where(t => string.IsNullOrWhiteSpace(t.Description)).ToList();
        Check("T11b todo check tem Descricao (o botao de info mostra ela)",
            semDescricao.Count == 0, semDescricao.Count + " sem descricao");

        var nomesRepetidos = defs.GroupBy(t => t.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Check("T11b nenhum Name repetido (a lista nao pode ter item duplicado)",
            nomesRepetidos.Count == 0,
            nomesRepetidos.Count == 0 ? "limpo" : string.Join(", ", nomesRepetidos.Take(3)));

        // ── T11c: alvo do check de registro ───────────────────────────────
        var regSemAlvo = defs.Where(t => t.Type == TweakType.Registry && string.IsNullOrWhiteSpace(t.KeyPath)).ToList();
        Check("T11c todo check de Registro tem KeyPath", regSemAlvo.Count == 0,
            regSemAlvo.Count == 0 ? "limpo" : string.Join(", ", regSemAlvo.Select(t => t.Name).Take(3)));

        string[] hives = { "HKEY_LOCAL_MACHINE", "HKEY_CURRENT_USER", "HKEY_CLASSES_ROOT", "HKEY_USERS", "HKLM", "HKCU", "HKCR", "HKU" };
        var hiveInvalido = defs
            .Where(t => !string.IsNullOrWhiteSpace(t.KeyPath))
            .Where(t => !hives.Any(h => t.KeyPath!.StartsWith(h, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Check("T11c todo KeyPath comeca por um hive valido", hiveInvalido.Count == 0,
            hiveInvalido.Count == 0 ? "limpo" : string.Join(", ", hiveInvalido.Select(t => t.Name).Take(3)));

        var regSemValor = defs
            .Where(t => t.Type == TweakType.Registry && !EhItemDePath(t))
            .Where(t => string.IsNullOrWhiteSpace(t.ValueName))
            .ToList();
        Check("T11c check de Registro sem nome de valor so' nos itens de PATH", regSemValor.Count == 0,
            regSemValor.Count == 0 ? "limpo" : string.Join(", ", regSemValor.Select(t => t.Name).Take(3)));

        // ── T11d: sentinelas mortas e valores fabricados ───────────────────
        var comSentinela = defs
            .Where(t => t.HarmfulValue != null)
            .Where(t => SentinelasMortas.Contains(Str(t.HarmfulValue), StringComparer.OrdinalIgnoreCase))
            .Where(t => !EhItemDePath(t))
            .ToList();
        Check("T11d nenhum HarmfulValue usa sentinela que o CheckTweak nao interpreta",
            comSentinela.Count == 0,
            comSentinela.Count == 0 ? "limpo" : string.Join(", ", comSentinela.Select(t => t.Name).Take(3)));

        var comValorFabricado = defs
            .Where(t => t.ValueName != null &&
                        ValoresFabricados.Contains(t.ValueName, StringComparer.OrdinalIgnoreCase))
            .ToList();
        Check("T11d nenhum check le valor fabricado (Corrupted/Orphaned/TooManyDisabled/...)",
            comValorFabricado.Count == 0,
            comValorFabricado.Count == 0 ? "limpo" : string.Join(", ", comValorFabricado.Select(t => t.Name).Take(3)));

        // ── T11e: harm == default tornaria o "corrigir" inutil/perigoso ────
        var harmIgualDefault = defs
            .Where(t => t.Type == TweakType.Registry && !EhItemDePath(t))
            .Where(t => t.HarmfulValue != null && t.DefaultValue != null)
            .Where(t => Str(t.HarmfulValue) == Str(t.DefaultValue))
            .ToList();
        Check("T11e HarmfulValue nunca e' igual ao DefaultValue (o reparo gravaria o valor perigoso)",
            harmIgualDefault.Count == 0,
            harmIgualDefault.Count == 0 ? "limpo" : string.Join(", ", harmIgualDefault.Select(t => t.Name).Take(3)));

        // ── T11f: servicos ────────────────────────────────────────────────
        string[] modos = { "Boot", "System", "Auto", "Manual", "Disabled" };
        var svcSemNome = defs
            .Where(t => t.Type == TweakType.Service && string.IsNullOrWhiteSpace(t.ServiceName))
            .ToList();
        Check("T11f todo check de Servico tem ServiceName", svcSemNome.Count == 0,
            svcSemNome.Count + " sem nome de servico");

        var svcModoInvalido = defs
            .Where(t => t.Type == TweakType.Service)
            .Where(t => !modos.Contains(t.HarmfulStartMode ?? "", StringComparer.OrdinalIgnoreCase))
            .ToList();
        Check("T11f todo Servico tem HarmfulStartMode conhecido", svcModoInvalido.Count == 0,
            svcModoInvalido.Count == 0 ? "limpo" : string.Join(", ", svcModoInvalido.Select(t => t.ServiceName).Take(3)));

        var svcMesmoModo = defs
            .Where(t => t.Type == TweakType.Service)
            .Where(t => !string.IsNullOrWhiteSpace(t.DefaultStartMode))
            .Where(t => string.Equals(t.HarmfulStartMode, t.DefaultStartMode, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Check("T11f HarmfulStartMode != DefaultStartMode (restaurar tem que mudar algo)",
            svcMesmoModo.Count == 0,
            svcMesmoModo.Count == 0 ? "limpo" : string.Join(", ", svcMesmoModo.Select(t => t.ServiceName).Take(3)));

        // ── T11g: BCD ─────────────────────────────────────────────────────
        var bcdIncompleto = defs
            .Where(t => t.Type == TweakType.Bcd)
            .Where(t => string.IsNullOrWhiteSpace(t.ValueName) || string.IsNullOrWhiteSpace(Str(t.DefaultValue))
                        || t.HarmfulValue == null)
            .ToList();
        Check("T11g todo check de BCD tem parametro, valor perigoso e valor de restauracao",
            bcdIncompleto.Count == 0,
            bcdIncompleto.Count == 0 ? "limpo" : string.Join(", ", bcdIncompleto.Select(t => t.Name).Take(3)));

        // ── T11h: contradicao de Id entre checks NAO opcionais ────────────
        // Dois checks do mesmo tipo mirando o MESMO alvo com HarmfulValue oposto
        // e sem a flag IsOptional nunca deixariam o score chegar a 100%.
        // O PATH e' o caso legitimo de compartilhar alvo: 6 checks leem
        // Environment\Path com SELETORES diferentes (INCOMPLETE/DUPLICATE/
        // MISSING/DEV_JUNK/VULNERABLE/CORRUPTED). Nao e' contradicao.
        var contradicoes = defs
            .Where(t => !t.IsOptional && !EhItemDePath(t))
            .Where(t => t.Type != TweakType.Service && t.Type != TweakType.Bcd)
            .GroupBy(t => new { t.Type, t.KeyPath, t.ValueName })
            .Where(g => g.Count() > 1)
            .Where(g => g.Select(t => Str(t.HarmfulValue)).Distinct().Count() > 1)
            .ToList();
        Check("T11h nenhum alvo tem dois checks nao-opcionais com HarmfulValue oposto",
            contradicoes.Count == 0,
            contradicoes.Count == 0
                ? "limpo"
                : string.Join(" | ", contradicoes.Select(g =>
                    g.Key.ValueName + " (" + string.Join(", ", g.Select(t => t.Name + "=" + Str(t.HarmfulValue))) + ")")));

        // ── T11k: o alvo do Registro existe DE VERDADE ────────────────────
        // Este e' o teste que pega a familia de chaves que nao existe em
        // Windows nenhum: `SystemProtection\Enable`, `ExploitProtection\Enabled`,
        // `Servicing\Version`, `Battery\ChargeLevelLimit`,
        // `Processor\PerformanceBoostMode`. Como "valor ausente" e' lido como
        // OK, o item aparecia PROTEGIDO sem nunca ter verificado nada — entao
        // a unica prova possivel e' abrir o caminho no registro real.
        //
        // Duas familias sao legitimas e NAO reprovam:
        //   (a) politica: o Windows so cria a chave quando a GPO e' aplicada
        //       (`...\Policies\...`);
        //   (b) componente que o build nao instala (servico/driver ausente, o
        //       caso do driver SMB1 removido no 11 24H2) — contado a parte.
        var regs = defs.Where(t => t.Type == TweakType.Registry && !string.IsNullOrWhiteSpace(t.KeyPath)).ToList();
        var semChave = regs.Where(t => !RegistroExiste(t.KeyPath!)).ToList();
        var semChavePolitica = semChave.Where(t => EhCaminhoDePolitica(t.KeyPath!)).ToList();
        var semChaveServico = semChave.Where(t => !EhCaminhoDePolitica(t.KeyPath!) && EhCaminhoDeServico(t.KeyPath!)).ToList();
        var semChaveInvalida = semChave
            .Where(t => !EhCaminhoDePolitica(t.KeyPath!) && !EhCaminhoDeServico(t.KeyPath!))
            .ToList();

        Check("T11k nenhum check de Registro aponta para chave inexistente fora de politica/servico",
            semChaveInvalida.Count == 0,
            semChaveInvalida.Count == 0
                ? regs.Count + " chaves conferidas no registro real"
                : string.Join(" | ", semChaveInvalida.Select(t => t.Name + " -> " + t.KeyPath).Take(4)));

        Check("T11k chave de politica ausente e' legitima (nasce quando a GPO e' aplicada)",
            true, semChavePolitica.Count + " politica(s) ainda nao aplicada(s)");

        // ── T11l: servico previsto x servico instalado ────────────────────
        // Nome de servico errado vira NOT_FOUND eterno (`BTHSSVC`,
        // `PushNotificationsUserService`, `SyncHost` eram inventados).
        var servicosInstalados = LerServicosInstalados();
        var svcs = defs.Where(t => t.Type == TweakType.Service && !string.IsNullOrWhiteSpace(t.ServiceName)).ToList();
        var svcAusente = svcs
            .Where(t => !servicosInstalados.Contains(t.ServiceName!.Trim()))
            .ToList();
        var svcFantasma = svcAusente
            .Where(t => !ServicosReaisNaoInstalados.ContainsKey(t.ServiceName!.Trim()))
            .ToList();
        Check("T11l todo ServiceName do catalogo existe na maquina ou e' servico real documentado",
            svcFantasma.Count == 0,
            svcFantasma.Count == 0
                ? svcs.Count + " servicos conferidos (" + svcAusente.Count + " real(is) nao instalado(s) aqui)"
                : string.Join(", ", svcFantasma.Select(t => t.ServiceName).Distinct().Take(4)));

        // ── T11m: valor com cara de sentinela que o motor nao interpreta ──
        // O `CheckTweak` compara HarmfulValue literalmente com o que esta no
        // registro (so' os itens de PATH usam seletor). Um token TUDO_MAIUSCULO
        // como `EMPTY_OR_WRONG`, `EXPLORER_OVERRIDDEN`, `LOADED` nunca casa com
        // valor real: o check fica mudo para sempre. Aqui qualquer token nesse
        // formato reprova, mesmo que seja um nome novo que ninguem colocou na
        // lista negra.
        var caraDeSentinela = defs
            .Where(t => t.Type == TweakType.Registry && !EhItemDePath(t))
            .Where(t => t.HarmfulValue != null && CaraDeSentinela(Str(t.HarmfulValue)))
            .ToList();
        Check("T11m nenhum HarmfulValue tem formato de sentinela (TOKEN_TUDO_MAIUSCULO)",
            caraDeSentinela.Count == 0,
            caraDeSentinela.Count == 0
                ? "limpo"
                : string.Join(", ", caraDeSentinela.Select(t => t.Name + "=" + Str(t.HarmfulValue)).Take(4)));

        // ── T11n: valor fabricado fora da lista negra (novo nome inventado) ─
        // Se o valor tem cara de contador/estado (Count/Size/Status/Entries) e
        // NAO existe em NENHUMA chave deste Windows, ele nao pode ser lido de
        // forma confiavel. Reprova para forcar revisao humana antes de entrar.
        var valoresSuspeitos = defs
            .Where(t => t.Type == TweakType.Registry && !EhItemDePath(t))
            .Where(t => !string.IsNullOrWhiteSpace(t.ValueName))
            .Where(t => NomeDeValorSuspeito(t.ValueName!))
            .Where(t => !ValorExisteEmAlgumLugar(regs, t.ValueName!, t.KeyPath!))
            .ToList();
        Check("T11n nenhum check le valor com nome de contador que nao existe na maquina",
            valoresSuspeitos.Count == 0,
            valoresSuspeitos.Count == 0
                ? "limpo"
                : string.Join(", ", valoresSuspeitos.Select(t => t.Name + "=" + t.ValueName).Take(4)));

        // ── T11i: o scan real devolve estado utilizavel ────────────────────
        var scan = Guardian.GetHarmfulTweaksWithStatus();
        Check("T11i o scan devolve a mesma quantidade de checks do catalogo",
            scan.Count == defs.Count, scan.Count + " vs " + defs.Count);

        var emErro = scan.Where(t => t.Status == TweakStatus.ERROR).ToList();
        Check("T11i nenhum check fica em ERROR (leitura quebrada = a pagina nao sabe nada)",
            emErro.Count == 0,
            emErro.Count == 0 ? "limpo" : string.Join(", ", emErro.Select(t => t.Name).Take(3)));

        var semId = scan.Where(t => string.IsNullOrEmpty(t.Id)).ToList();
        Check("T11i todo check do scan tem Id estavel", semId.Count == 0, semId.Count + " sem Id");

        int modificados = scan.Count(t => t.Status == TweakStatus.MODIFIED);
        Check("T11i o scan nao colapsou em MODIFIED (catalogo nao virou alarme eterno)",
            modificados < scan.Count / 3, "MODIFIED=" + modificados + "/" + scan.Count);

        var statusValidos = new[] { TweakStatus.OK, TweakStatus.MODIFIED, TweakStatus.NOT_FOUND, TweakStatus.PENDING, TweakStatus.ERROR };
        var statusEstranho = scan.Where(t => !statusValidos.Contains(t.Status)).ToList();
        Check("T11i todo status do scan esta entre os que a pagina sabe pintar",
            statusEstranho.Count == 0, statusEstranho.Count + " fora da lista");

        // ── T11j: os itens que a auditoria corrigiu continuam corretos ─────
        var llmnr = scan.FirstOrDefault(t => t.Name.Contains("LLMNR"));
        Check("T11j LLMNR usa a politica real (DNSClient/EnableMulticast) e trata LIGADO como ruim",
            llmnr != null && llmnr.ValueName == "EnableMulticast"
                         && llmnr.KeyPath != null && llmnr.KeyPath.EndsWith("DNSClient")
                         && Convert.ToInt32(llmnr.HarmfulValue) == 1,
            llmnr == null ? "ausente" : llmnr.KeyPath + " :: " + llmnr.ValueName + " harm=" + llmnr.HarmfulValue);

        var blocklist = scan.FirstOrDefault(t => t.Name.Contains("Bloqueio de Driver"));
        Check("T11j blocklist de driver le CI\\Config\\VulnerableDriverBlocklistEnable",
            blocklist != null && blocklist.ValueName == "VulnerableDriverBlocklistEnable"
                              && blocklist.KeyPath != null && blocklist.KeyPath.EndsWith("CI\\Config"),
            blocklist == null ? "ausente" : blocklist.KeyPath + " :: " + blocklist.ValueName);

        var proxiesRemovidos = new[]
        {
            "Gerenciador de Tarefas Lento", "Windows Servicing Stack", "Repositório de Drivers",
            "Registry Hive Corrompido", "Entradas de Desinstalação Órfãs", "Shell Extensions Desativadas em Massa",
            "Processos Órfãos", "S.M.A.R.T. Monitoring", "Windows Recovery Environment",
            "Notificação de Logon (AppInit_DLLs)", "Boot Configuration Data (BCD) Corrompido"
        };
        var voltaram = defs.Where(t => proxiesRemovidos.Any(p => t.Name.Contains(p, StringComparison.OrdinalIgnoreCase))).ToList();
        Check("T11j nenhum proxy de diagnostico inventado voltou ao catalogo",
            voltaram.Count == 0,
            voltaram.Count == 0 ? "limpo" : string.Join(", ", voltaram.Select(t => t.Name).Take(3)));

        Console.WriteLine("  T11: OK={0} FALHAS={1}", _ok, _fail);
        LastOk = _ok;
        return _fail;
    }
}
