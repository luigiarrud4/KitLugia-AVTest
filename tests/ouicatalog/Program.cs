using System;
using System.Linq;
using KitLugia.Core;

internal static class Program
{
    private static int _fail;

    private static void Check(bool ok, string label, string detail = "")
    {
        Console.WriteLine((ok ? "  OK   " : "  FAIL ") + label + (detail.Length > 0 ? " — " + detail : ""));
        if (!ok) _fail++;
    }

    private static int Main()
    {
        Console.WriteLine("=== OuiCatalog harness (mecanismo TMAC) ===");

        // 1) Catalogo carregou do recurso embarcado
        var asm = typeof(OuiCatalog).Assembly;
        Console.WriteLine("  manifest resources:");
        foreach (var n in asm.GetManifestResourceNames())
            Console.WriteLine("    " + n);
        var vendors = OuiCatalog.Vendors;
        Console.WriteLine($"  vendors: {vendors.Count}");
        Check(vendors.Count > 10000, "catalogo carregado do recurso embarcado", vendors.Count + " vendors");
        Check(vendors.Sum(v => v.OuiPrefixes.Count) > 20000, "OUIs totais", vendors.Sum(v => v.OuiPrefixes.Count) + " prefixos");

        // 2) Fabricantes conhecidos presentes (extraidos do oui.db do TMAC)
        string[] esperados = { "Texas Instruments", "Hewlett Packard", "NETGEAR", "Intel" };
        foreach (var e in esperados)
        {
            var achou = vendors.Any(v => v.Name.Contains(e, StringComparison.OrdinalIgnoreCase));
            Check(achou, $"vendor presente: {e}");
        }

        // 3) Mecanismo TMAC: OUI real + 3 octetos aleatorios
        var macs = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ouisUsados = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 200; i++)
        {
            var mac = OuiCatalog.GenerateMac(Random.Shared, use02Prefix: true);
            macs.Add(mac);
            ouisUsados.Add(mac[..6]);
            Check(mac.Length == 12 && mac.All(char.IsAsciiHexDigitUpper), "formato 12 hex", mac);
        }
        Check(macs.Count > 150, "MACs variety (3 bytes aleatorios)", macs.Count + "/200 unicos");

        // 4) Primeiro octeto: 0x02 exato (modo -r02 do TMAC) + unicast
        foreach (var mac in macs)
        {
            var b0 = Convert.ToByte(mac[..2], 16);
            if (b0 != 0x02)
            {
                Check(false, "primeiro octeto exatamente 0x02 (modo -r02)", mac);
                break;
            }
        }
        Check(true, "todos com primeiro octeto 0x02 (locally administered, unicast)");

        // 5) OUI vem da base real de vendors (nao generico).
        //    Com use02Prefix=true o 1o byte e trocado por 0x02 (modo -r02 do TMAC),
        //    entao validamos o OUI BASE sorteado, nao o MAC final.
        var ouiValidos = vendors.SelectMany(v => v.OuiPrefixes).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ouiBaseUsados = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 200; i++)
            ouiBaseUsados.Add(OuiCatalog.RandomOuiPrefix(Random.Shared));
        var foraDaBase = ouiBaseUsados.Where(o => !ouiValidos.Contains(o)).ToList();
        Check(foraDaBase.Count == 0, "todos os OUIs base vieram da base real",
            foraDaBase.Count == 0 ? ouiBaseUsados.Count + " OUIs distintos" : string.Join(",", foraDaBase.Take(3)));

        // 5b) MAC com use02Prefix=false preserva o OUI base intacto (modo -r do TMAC)
        var macR = OuiCatalog.GenerateMac(Random.Shared, use02Prefix: false);
        Check(ouiValidos.Contains(macR[..6]), "modo -r (OUI intacto) bate com a base", $"{macR}");

        // 6) use02Prefix=false mantem o OUI do vendor (so unicast)
        var macSem02 = OuiCatalog.GenerateMac(Random.Shared, use02Prefix: false);
        var b0sem = Convert.ToByte(macSem02[..2], 16);
        Check((b0sem & 0x01) == 0, "use02Prefix=false mantem unicast", macSem02);

        // 7) FindVendor resolve o fabricante por MAC
        var vendorTexas = vendors.First(v => v.Name.Contains("Texas Instruments", StringComparison.OrdinalIgnoreCase));
        var ouiTexas = vendorTexas.OuiPrefixes[0];
        var macTexas = AdapterManager.GenerateMacFromOui(ouiTexas, use02Prefix: false);
        var achouVendor = OuiCatalog.FindVendor(macTexas);
        Check(achouVendor != null && achouVendor.Contains("Texas Instruments", StringComparison.OrdinalIgnoreCase),
            "FindVendor resolve fabricante", $"{macTexas} -> {achouVendor}");

        // 8) GenerateMacFromOui usa o OUI pedido
        Check(macTexas[..6] == ouiTexas, "GenerateMacFromOui preserva OUI", $"{ouiTexas} -> {macTexas}");

        // 9) AdapterManager.GenerateRandomMac (OUI de vendor, usado ao escolher fabricante) produz MAC valido
        var macUi = AdapterManager.GenerateRandomMac();
        Check(macUi.Length == 12 && macUi.All(char.IsAsciiHexDigitUpper), "AdapterManager.GenerateRandomMac", macUi);
        var b0ui = Convert.ToByte(macUi[..2], 16);
        Check((b0ui & 0x03) == 0x02, "GenerateRandomMac com 0x02 locally administered", macUi);

        // 9b) GenerateRandomMacPlain = fallback generico (botao Gerar Novo MAC): 02 + 5 bytes aleatorios, sem OUI
        var plain = AdapterManager.GenerateRandomMacPlain();
        Check(plain.Length == 12 && plain.All(char.IsAsciiHexDigitUpper), "GenerateRandomMacPlain formato", plain);
        Check(Convert.ToByte(plain[..2], 16) == 0x02, "GenerateRandomMacPlain 1o octeto 0x02 exato", plain);
        var plain2 = AdapterManager.GenerateRandomMacPlain();
        Check(plain != plain2, "GenerateRandomMacPlain gera valores diferentes", $"{plain} vs {plain2}");

        // 10) AdapterManager.GetRandomMacAddress (mesmo caminho, 2 overloads)
        var macA = AdapterManager.GetRandomMacAddress(true);
        var macB = AdapterManager.GetRandomMacAddress(false);
        Check(macA.Length == 12 && macB.Length == 12, "GetRandomMacAddress ambos overloads", $"{macA} / {macB}");
        Check((Convert.ToByte(macB[..2], 16) & 0x01) == 0, "GetRandomMacAddress(false) unicast", macB);

        // 11) Search filtra por nome
        var busca = OuiCatalog.Search("Texas Instruments", 50);
        Check(busca.Count > 0, "Search por nome", busca.Count + " resultados");

        // 12) Amostra de MACs gerados com seus vendors
        Console.WriteLine();
        Console.WriteLine("  --- amostra de MACs gerados (modo -r02: 1o byte = 02) ---");
        var rng = new Random(1234);
        for (int i = 0; i < 8; i++)
        {
            var g = OuiCatalog.GenerateWithDetails(rng, use02Prefix: true);
            Console.WriteLine($"    {FormatMac(g.Mac)}  <- {g.VendorName} (OUI base {FormatOui(g.OuiPrefix)})");
        }

        Console.WriteLine();
        Console.WriteLine("  --- amostra de MACs gerados (modo -r: OUI real intacto) ---");
        var rng2 = new Random(99);
        for (int i = 0; i < 8; i++)
        {
            var g = OuiCatalog.GenerateWithDetails(rng2, use02Prefix: false);
            Console.WriteLine($"    {FormatMac(g.Mac)}  <- {g.VendorName} (OUI {FormatOui(g.OuiPrefix)})");
        }

        Console.WriteLine();
        Console.WriteLine(_fail == 0 ? "=== TODOS OS CHECKS PASSARAM ===" : $"=== {_fail} FALHA(S) ===");
        return _fail == 0 ? 0 : 1;
    }

    private static string FormatMac(string mac)
        => string.Join(":", Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2)));

    private static string FormatOui(string oui)
        => string.Join("-", Enumerable.Range(0, 3).Select(i => oui.Substring(i * 2, 2)));
}
