using System.IO.Compression;
using System.Reflection;

namespace KitLugia.Core;

/// <summary>
/// Catálogo de OUIs (Organizationally Unique Identifiers) extraído do TMAC v6
/// (Technitium MAC Address Changer) — arquivo oui.db do próprio app.
///
/// MECANISMO DO TMAC (confirmado via engenharia reversa do TMAC.exe):
///   1. TMAC carrega uma base de vendors OUI (funções VB6: LoadVendors, ParseOUI,
///      GetVendor, classe NICVendor).
///   2. Gera o MAC em 2 etapas usando VB6 Rnd() (rtcRandomize + rtcRandomNext):
///      Etapa 1 — rtcRandomize (seed) → rtcRandomNext → índice aleatório na
///                array de vendors OUI (validação com vbaUbound; se inválido, repete).
///      Etapa 2 — rtcRandomNext novamente → 3 octetos aleatórios.
///   3. Combina: OUI do vendor (3 bytes) + 3 octetos aleatórios = MAC (6 bytes).
///   4. O bit 0x02 (locally administered) pode ser setado no 1º octeto.
///
/// Este catálogo reproduz esse mecanismo: escolhe um OUI REAL de vendor
/// (não um prefixo genérico) e sorteia apenas os 3 octetos livres.
/// </summary>
public sealed class OuiCatalog
{
    /// <summary>Um vendor OUI: nome do fabricante + seus prefixos de 3 bytes.</summary>
    public sealed record Vendor(string Name, IReadOnlyList<string> OuiPrefixes)
    {
        public override string ToString() => Name;
    }

    private const string ResourceName = "KitLugia.Core.Resources.oui-vendors.txt.gz";

    private static readonly Lazy<IReadOnlyList<Vendor>> _lazyVendors = new(LoadVendors, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Todos os vendors do catálogo (16.294 no oui.db do TMAC v6).</summary>
    public static IReadOnlyList<Vendor> Vendors => _lazyVendors.Value;

    /// <summary>Prefixo OUI real sorteado aleatoriamente (formato "AABBCC").</summary>
    public static string RandomOuiPrefix() => RandomOuiPrefix(Random.Shared);

    /// <summary>
    /// Sorteia um prefixo OUI real usando o RNG fornecido (permite teste determinístico).
    /// </summary>
    public static string RandomOuiPrefix(Random rng)
    {
        var vendors = Vendors;
        if (vendors.Count == 0) return "001A2B"; // fallback genérico
        var vendor = vendors[rng.Next(vendors.Count)];
        if (vendor.OuiPrefixes.Count == 0) return "001A2B";
        return vendor.OuiPrefixes[rng.Next(vendor.OuiPrefixes.Count)];
    }

    /// <summary>
    /// Resultado da geração: o MAC final + o OUI base sorteado + o vendor dono dele.
    /// </summary>
    public sealed record GeneratedMac(string Mac, string OuiPrefix, string VendorName);

    /// <summary>
    /// Gera um MAC no estilo TMAC: OUI real de vendor + 3 octetos aleatórios,
    /// devolvendo também o OUI base e o vendor (para exibir na UI).
    /// </summary>
    public static GeneratedMac GenerateWithDetails(Random? rng = null, bool use02Prefix = true)
    {
        rng ??= Random.Shared;
        var vendors = Vendors;
        if (vendors.Count == 0)
        {
            var fallback = GenerateMac(rng, use02Prefix);
            return new GeneratedMac(fallback, "001A2B", "(catálogo indisponível)");
        }

        var vendor = vendors[rng.Next(vendors.Count)];
        var oui = vendor.OuiPrefixes.Count > 0
            ? vendor.OuiPrefixes[rng.Next(vendor.OuiPrefixes.Count)]
            : "001A2B";

        var mac = new byte[6];
        for (int i = 0; i < 3; i++)
            mac[i] = Convert.ToByte(oui.Substring(i * 2, 2), 16);
        rng.NextBytes(mac.AsSpan(3, 3));

        if (use02Prefix)
            mac[0] = 0x02;
        else
            mac[0] &= 0xFE;

        return new GeneratedMac(Convert.ToHexString(mac), oui, vendor.Name);
    }

    /// <summary>
    /// Gera um MAC no estilo TMAC: OUI real de vendor + 3 octetos aleatórios.
    /// </summary>
    /// <param name="use02Prefix">
    /// Quando true, o 1º octeto vira 0x02 (locally administered) — obrigatório para
    /// WiFi na maioria dos drivers, e é o equivalente ao modo "-r02" do TMAC.
    /// Quando false, mantém o OUI do vendor intacto (equivalente a "-r" do TMAC).
    /// </param>
    public static string GenerateMac(Random? rng = null, bool use02Prefix = true)
    {
        rng ??= Random.Shared;
        var oui = RandomOuiPrefix(rng);
        var mac = new byte[6];

        // 3 primeiros bytes = OUI real do vendor (selecionado da base)
        for (int i = 0; i < 3; i++)
            mac[i] = Convert.ToByte(oui.Substring(i * 2, 2), 16);

        // 3 últimos bytes = aleatórios (etapa 2 do TMAC)
        rng.NextBytes(mac.AsSpan(3, 3));

        if (use02Prefix)
        {
            // Modo "-r02" do TMAC: primeiro octeto EXATAMENTE 0x02
            // (unicast + locally administered). Os bytes 2-3 do OUI do vendor
            // sao preservados, os 3 ultimos sao aleatorios.
            mac[0] = 0x02;
        }
        else
        {
            // Modo "-r" do TMAC: mantem o OUI do vendor intacto, so garante unicast.
            mac[0] &= 0xFE;
        }

        return Convert.ToHexString(mac);
    }

    /// <summary>
    /// Procura o vendor dono de um MAC (pelo OUI) — equivalente ao GetVendor do TMAC.
    /// </summary>
    public static string? FindVendor(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var clean = mac.Replace(":", "").Replace("-", "").Replace(" ", "").ToUpperInvariant();
        if (clean.Length < 6) return null;
        var prefix = clean[..6];
        foreach (var v in Vendors)
            foreach (var o in v.OuiPrefixes)
                if (o == prefix)
                    return v.Name;
        return null;
    }

    /// <summary>
    /// Filtra vendors por nome (case-insensitive). Retorna no máximo <paramref name="max"/>.
    /// </summary>
    public static List<Vendor> Search(string? query, int max = 300)
    {
        var list = new List<Vendor>();
        if (string.IsNullOrWhiteSpace(query))
        {
            foreach (var v in Vendors)
            {
                list.Add(v);
                if (list.Count >= max) break;
            }
            return list;
        }

        var q = query.Trim();
        foreach (var v in Vendors)
        {
            if (v.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                list.Add(v);
            if (list.Count >= max) break;
        }
        return list;
    }

    private static IReadOnlyList<Vendor> LoadVendors()
    {
        var result = new List<Vendor>(17000);
        try
        {
            var asm = typeof(OuiCatalog).Assembly;
            var names = asm.GetManifestResourceNames();
            var resourceName = names.FirstOrDefault(n =>
                n.Equals(ResourceName, StringComparison.OrdinalIgnoreCase))
                ?? names.FirstOrDefault(n => n.EndsWith("oui-vendors.txt.gz", StringComparison.OrdinalIgnoreCase));
            if (resourceName == null) return result;

            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null) return result;

            using var gz = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new StreamReader(gz, System.Text.Encoding.UTF8);

            while (reader.ReadLine() is { } line)
            {
                if (line.Length < 3) continue;
                var tab = line.IndexOf('\t');
                if (tab <= 0 || tab >= line.Length - 1) continue;

                var name = line[..tab];
                var ouiList = line[(tab + 1)..]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Where(o => o.Length == 6)
                    .ToArray();

                if (ouiList.Length == 0) continue;
                result.Add(new Vendor(name, ouiList));
            }
        }
        catch
        {
            // Falha ao carregar o catálogo não pode derrubar a página: devolve vazio
            // e o chamador cai no prefixo genérico.
        }

        return result;
    }
}
