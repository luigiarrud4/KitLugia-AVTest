using System.Diagnostics;
using KitLugia.Core.UninstallTools;

// Harness: verifica a separacao entre o scan rapido (so registro) e o enriquecimento
// que toca disco, conforme implementado na AppsPage.
// 1) GetInstalledPrograms deve voltar em segundos e trazer todos os apps.
// 2) EnrichProgramMetadataFromDisk completa os campos ausentes (loc/pub/ver/date).
// Exit 0 somente se todas as verificacoes passarem.

var fails = new List<string>();
void Check(bool cond, string name, string detail = "")
{
    Console.WriteLine((cond ? "[PASS] " : "[FAIL] ") + name + (detail.Length > 0 ? " — " + detail : ""));
    if (!cond) fails.Add(name);
}

var sw = Stopwatch.StartNew();
var programs = RegistryProgramFactory.GetInstalledPrograms();
sw.Stop();

Check(programs.Count > 20, $"scan-fast: count={programs.Count}");
// Scan rapido = menos de 5 segundos (antes do split, o I/O por app tornava-o minutos)
Check(sw.Elapsed.TotalSeconds < 5, $"scan-fast: elapsed={sw.Elapsed.TotalMilliseconds:F0}ms (<5000ms)");

int withLoc = programs.Count(p => !string.IsNullOrWhiteSpace(p.InstallLocation));
int withPub = programs.Count(p => !string.IsNullOrWhiteSpace(p.Publisher));
int withVer = programs.Count(p => !string.IsNullOrWhiteSpace(p.DisplayVersion));
int withDate = programs.Count(p => !string.IsNullOrWhiteSpace(p.InstallDate));
Console.WriteLine($"  fields present at scan: location={withLoc}/{programs.Count} publisher={withPub}/{programs.Count} version={withVer}/{programs.Count} date={withDate}/{programs.Count}");

// Amostras: enriquecimento de disco nos 3 primeiros apps com metadados faltantes
var needEnrich = programs.Where(p => string.IsNullOrWhiteSpace(p.InstallLocation) ||
                                     string.IsNullOrWhiteSpace(p.DisplayVersion) ||
                                     string.IsNullOrWhiteSpace(p.Publisher)).Take(3).ToList();
foreach (var p in needEnrich)
{
    Console.WriteLine($"  enrich-sample: \"{p.DisplayName}\"");
    Console.WriteLine($"    before: loc=\"{p.InstallLocation}\" ver=\"{p.DisplayVersion}\" pub=\"{p.Publisher}\" date=\"{p.InstallDate}\"");
    RegistryProgramFactory.EnrichProgramMetadataFromDisk(p);
    Console.WriteLine($"    after : loc=\"{p.InstallLocation}\" ver=\"{p.DisplayVersion}\" pub=\"{p.Publisher}\" date=\"{p.InstallDate}\"");
}

// O enriquecimento de disco nao pode apagar campos ja presentes
var before = programs[0];
var snapshot = (loc: before.InstallLocation, pub: before.Publisher, ver: before.DisplayVersion);
RegistryProgramFactory.EnrichProgramMetadataFromDisk(before);
bool nonDestructive =
    (string.IsNullOrEmpty(snapshot.loc) || before.InstallLocation == snapshot.loc) &&
    (string.IsNullOrEmpty(snapshot.pub) || before.Publisher == snapshot.pub) &&
    (string.IsNullOrEmpty(snapshot.ver) || before.DisplayVersion == snapshot.ver);
Check(nonDestructive, "enrich: never clears existing metadata");

// Apps MSI (MsiExec.exe /X{GUID}): o resolvedor via msi.dll deve dar InstallLocation
// a uma fracao deles — no scan so-registro esses ficavam sem caminho para sempre.
static bool LooksLikeGuid(string? s) =>
    !string.IsNullOrWhiteSpace(s) && s.Trim().Length == 38 && s.Trim().StartsWith('{') && s.Trim().EndsWith('}');
var msiApps = programs.Where(p => (p.UninstallString ?? "").Contains("MsiExec", StringComparison.OrdinalIgnoreCase) ||
                                 LooksLikeGuid(p.RegistryKeyName)).ToList();
int msiWithLocBefore = msiApps.Count(p => !string.IsNullOrWhiteSpace(p.InstallLocation));
var msiSamples = msiApps.Where(p => string.IsNullOrWhiteSpace(p.InstallLocation)).Take(25).ToList();
foreach (var p in msiSamples) RegistryProgramFactory.EnrichProgramMetadataFromDisk(p);
int msiWithLocAfter = msiApps.Count(p => !string.IsNullOrWhiteSpace(p.InstallLocation));
Console.WriteLine($"  msi: {msiApps.Count} apps MSI | com location: antes={msiWithLocBefore} | samples enriquecidos: {msiSamples.Count} -> resolvidos no total={msiWithLocAfter}");
foreach (var p in msiSamples.Where(p => !string.IsNullOrWhiteSpace(p.InstallLocation)).Take(3))
    Console.WriteLine($"    msi-resolved: \"{p.DisplayName}\" -> {p.InstallLocation}");

// --- Detalhe por app (rodar com --detalhe) ---
if (args.Contains("--detalhe"))
{
    string[] alvos = { "RustDesk", "Vestktop", "LM Studio", "RiseupVPN", "LDPlayer",
                       "Roblox Player", "Roblox Studio", "Razer Synapse", "Ubisoft Connect",
                       "ASUSTek", "Update for" };
    foreach (var nome in alvos)
    {
        foreach (var p in programs.Where(x => x.DisplayName.Contains(nome, StringComparison.OrdinalIgnoreCase)).Take(2))
        {
            Console.WriteLine($"--- {p.DisplayName}");
            Console.WriteLine($"    loc='{p.InstallLocation}' icon='{p.DisplayIcon}'");
            Console.WriteLine($"    uninstall='{p.UninstallString}'");
            Console.WriteLine($"    ver='{p.DisplayVersion}' pub='{p.Publisher}' date='{p.InstallDate}' size='{p.EstimatedSize}'");
            Console.WriteLine($"    key='{p.RegistryKeyName}'");
            string? exe = RegistryProgramFactory.ExtractExecutablePath(p.UninstallString);
            Console.WriteLine($"    exeDoUninstall='{exe}' existe={(!string.IsNullOrEmpty(exe) && File.Exists(exe))}");
        }
    }
}

// --- Completude: enriquece TODOS os apps e mede o que continua faltando ---
var swAll = Stopwatch.StartNew();
foreach (var p in programs) RegistryProgramFactory.EnrichProgramMetadataFromDisk(p);
swAll.Stop();

int semLoc = programs.Count(p => string.IsNullOrWhiteSpace(p.InstallLocation));
int semDate = programs.Count(p => string.IsNullOrWhiteSpace(p.InstallDate));
int semVer = programs.Count(p => string.IsNullOrWhiteSpace(p.DisplayVersion));
int semPub = programs.Count(p => string.IsNullOrWhiteSpace(p.Publisher));
Console.WriteLine($"  APOS enrich de TODOS ({swAll.ElapsedMilliseconds}ms): semLoc={semLoc} semDate={semDate} semVer={semVer} semPub={semPub} (de {programs.Count})");

// Data ausente: a pasta de instalacao existe? (CreationTime e a fonte de data)
var semData = programs.Where(p => string.IsNullOrWhiteSpace(p.InstallDate)).ToList();
int semDataComPasta = semData.Count(p => !string.IsNullOrWhiteSpace(p.InstallLocation) && Directory.Exists(p.InstallLocation));
int semDataSemPasta = semData.Count - semDataComPasta;
Console.WriteLine($"    sem data: {semDataComPasta} TEM pasta de instalacao ( CreationTime deveria preencher ) | {semDataSemPasta} sem pasta");

// Data presente mas nao parseavel (ex.: 'Tue Aug 18 15:08:04 2026')
int dataNaoParseavelGenerico = programs.Count(p => !string.IsNullOrWhiteSpace(p.InstallDate) &&
    !DateTime.TryParse(p.InstallDate, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _));
Console.WriteLine($"    data presente mas NAO parseavel pelo TryParse generico: {dataNaoParseavelGenerico}");
foreach (var p in programs.Where(p => !string.IsNullOrWhiteSpace(p.InstallDate) &&
    !DateTime.TryParse(p.InstallDate, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)).Take(5))
    Console.WriteLine($"      ex: '{p.InstallDate}'  ({p.DisplayName})");

// Sem local: ainda tem icone/uninstall que daria a pasta?
var semLocApps = programs.Where(p => string.IsNullOrWhiteSpace(p.InstallLocation)).ToList();
int semLocComIcon = semLocApps.Count(p => !string.IsNullOrWhiteSpace(p.DisplayIcon));
int semLocComUninstall = semLocApps.Count(p => !string.IsNullOrWhiteSpace(p.UninstallString));
Console.WriteLine($"    sem local: {semLocApps.Count} | com DisplayIcon={semLocComIcon} | com UninstallString={semLocComUninstall}");
foreach (var p in semLocApps.Take(6))
    Console.WriteLine($"      ex: '{p.DisplayName}' icon='{p.DisplayIcon}' uninstall='{p.UninstallString}'");

// --- FASE FINAL DE COMPLETUDE (o que a AppsPage faz na 3a fase) ---
var swFinal = Stopwatch.StartNew();
int resolvidosFinal = 0;
foreach (var p in programs.Where(p => string.IsNullOrWhiteSpace(p.InstallLocation)).ToList())
{
    if (RegistryProgramFactory.TryResolveMissingInstallLocation(p))
    {
        resolvidosFinal++;
        RegistryProgramFactory.EnrichProgramMetadataFromDisk(p);
    }
}
swFinal.Stop();

int semLocFinal = programs.Count(p => string.IsNullOrWhiteSpace(p.InstallLocation));
int semDateFinal = programs.Count(p => string.IsNullOrWhiteSpace(p.InstallDate));
Console.WriteLine($"  FASE FINAL ({swFinal.ElapsedMilliseconds}ms): local recuperado={resolvidosFinal} | semLoc={semLocFinal} semDate={semDateFinal}");
foreach (var p in programs.Where(p => !string.IsNullOrWhiteSpace(p.InstallLocation) &&
                                     !string.IsNullOrWhiteSpace(p.InstallDate) &&
                                     p.InstallLocation.Contains("Program Files", StringComparison.OrdinalIgnoreCase)).Take(3))
    Console.WriteLine($"      ex recuperado: '{p.DisplayName}' -> {p.InstallLocation} date={p.InstallDate}");

// Parser de data IGUAL ao da UI (yyyyMMdd etc.) — mede o que a coluna de meta mostra.
static bool UiParseDate(string? raw, out DateTime d)
{
    d = default;
    if (string.IsNullOrWhiteSpace(raw)) return false;
    raw = raw.Trim();
    string[] fmts = { "yyyyMMdd", "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "dd-MM-yyyy", "MM/dd/yyyy", "yyyyMMddHHmmss",
                       "ddd MMM dd HH:mm:ss yyyy", "ddd MMM d HH:mm:ss yyyy", "yyyy-MM-ddTHH:mm:ss",
                       "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-dd HH:mm:ss", "MM/dd/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm:ss" };
    if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AllowWhiteSpaces, out d)) return true;
    if (DateTime.TryParse(raw, System.Globalization.CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.AllowWhiteSpaces, out d)) return true;
    foreach (var f in fmts)
        if (DateTime.TryParseExact(raw, f, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out d)) return true;
    return false;
}
int dataNaoParseavel = programs.Count(p => !string.IsNullOrWhiteSpace(p.InstallDate) && !UiParseDate(p.InstallDate, out _));
int dataPresente = programs.Count(p => !string.IsNullOrWhiteSpace(p.InstallDate));
Console.WriteLine($"    data presente={dataPresente} | NAO parseavel pela UI={dataNaoParseavel}");
foreach (var p in programs.Where(p => !string.IsNullOrWhiteSpace(p.InstallDate) && !UiParseDate(p.InstallDate, out _)).Take(5))
    Console.WriteLine($"      ex nao-parseavel: '{p.InstallDate}'  ({p.DisplayName})");

// Apps citados nas prints do usuario: conferidos DEPOIS de todo o pipeline.
string[] alvosPrint = { "RustDesk", "Vestktop", "LM Studio", "RiseupVPN", "LDPlayer",
                       "Razer Synapse", "Roblox Player", "Roblox Studio", "Ubisoft Connect" };
Console.WriteLine("  --- apps das prints, apos pipeline completo ---");
foreach (var nome in alvosPrint)
{
    foreach (var p in programs.Where(x => x.DisplayName.Contains(nome, StringComparison.OrdinalIgnoreCase)).Take(2))
    {
        string dt = p.InstallDate;
        string dataFmt = UiParseDate(p.InstallDate, out var dParsed) ? dParsed.ToString("dd/MM/yyyy") : "(sem data)";
        Console.WriteLine($"    {p.DisplayName,-38} loc='{p.InstallLocation}' data={dataFmt} ver='{p.DisplayVersion}'");
    }
}

// Debug do matching da fase final: por que "Node.js" nao casa com "nodejs"?
foreach (var p in programs.Where(x => x.DisplayName.Contains("Node.js", StringComparison.OrdinalIgnoreCase)))
    Console.WriteLine($"  debug real: '{p.DisplayName}' loc='{p.InstallLocation}' uninstall='{p.UninstallString}'");

var teste = new RegistryProgram
{
    DisplayName = "Node.js",
    UninstallString = "MsiExec.exe /I{16ACB226-F825-4982-A438-4E2A4D52BC44}",
    RegistryKeyName = "{16ACB226-F825-4982-A438-4E2A4D52BC44}"
};
Console.WriteLine($"  debug: antes loc='{teste.InstallLocation}'");
bool okNode = RegistryProgramFactory.TryResolveMissingInstallLocation(teste);
Console.WriteLine($"  debug: Node.js -> resolvido={okNode} loc='{teste.InstallLocation}'");

var teste2 = new RegistryProgram { DisplayName = "GitHub CLI" };
bool okGh = RegistryProgramFactory.TryResolveMissingInstallLocation(teste2);
Console.WriteLine($"  debug: GitHub CLI -> resolvido={okGh} loc='{teste2.InstallLocation}'");

Console.WriteLine();
Console.WriteLine(fails.Count == 0 ? "SAIDA: 0 (all checks passed)" : $"SAIDA: {fails.Count} FAIL: " + string.Join("; ", fails));
return fails.Count == 0 ? 0 : 1;
