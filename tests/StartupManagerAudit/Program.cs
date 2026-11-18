// StartupManagerAudit — SOMENTE-LEITURA: imprime a lista de inicialização que a ServicesPage
// mostra, com os campos novos (Fabricante / caminho resolvido / rótulo curto / data de alteração).
// Serve para conferir, sem abrir a GUI, o que o enriquecimento e a leitura de StartupApproved
// produziram nesta máquina. Não altera nada (nenhuma escrita de registro).
//
// Uso: dotnet run --project tests/StartupManagerAudit -c Release
using KitLugia.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var sw = System.Diagnostics.Stopwatch.StartNew();
var fast = StartupManager.GetStartupAppsFast();
Console.WriteLine($"[FAST] {fast.Count} itens em {sw.ElapsedMilliseconds} ms");
sw.Restart();

var all = StartupManager.GetStartupAppsWithDetails(true);
Console.WriteLine($"[FULL] {all.Count} itens em {sw.ElapsedMilliseconds} ms\n");

int comFabricante = all.Count(a => !string.IsNullOrWhiteSpace(a.Publisher));
int comCaminho = all.Count(a => !string.IsNullOrWhiteSpace(a.FilePath));
int comData = all.Count(a => a.StateChangeDate.HasValue);
int desabilitados = all.Count(a => a.Status == StartupStatus.Disabled);
int comIcone = all.Count(a => !string.IsNullOrWhiteSpace(a.IconFilePath));
int iconePorBusca = all.Count(a => !string.IsNullOrWhiteSpace(a.IconFilePath)
                                    && string.IsNullOrWhiteSpace(a.FilePath));

Console.WriteLine($"fabricante preenchido : {comFabricante}/{all.Count}");
Console.WriteLine($"caminho resolvido     : {comCaminho}/{all.Count}");
Console.WriteLine($"data de alteração     : {comData}/{all.Count}");
Console.WriteLine($"desabilitados         : {desabilitados}/{all.Count}");
Console.WriteLine($"arquivo de ícone      : {comIcone}/{all.Count} (por busca na pasta: {iconePorBusca})\n");

// Itens sem caminho resolvido mas COM candidata de ícone: prova o fallback "procura na pasta".
Console.WriteLine("Icone diferente do binario do item (launcher -> exe do app):");
foreach (var a in all.Where(x => !string.IsNullOrWhiteSpace(x.FilePath) && !string.IsNullOrWhiteSpace(x.IconFilePath)
                            && !x.IconFilePath.Equals(x.FilePath, StringComparison.OrdinalIgnoreCase)).Take(15))
    Console.WriteLine($"  {Trunc(a.Name, 22),-22} item={Trunc(Path.GetFileName(a.FilePath), 26),-26} icone={Path.GetFileName(a.IconFilePath)}");
Console.WriteLine();
Console.WriteLine("Discord e afins (comando -> item -> icone):");
foreach (var a in all.Where(x => x.Name.Contains("Discord", StringComparison.OrdinalIgnoreCase)))
    Console.WriteLine($"  {a.Name} | cmd={a.FullCommand} | item={a.FilePath} | icone={a.IconFilePath}");
Console.WriteLine();
Console.WriteLine("Opera/Raycast (comando -> item -> icone):");
foreach (var a in all.Where(x => x.Name.Contains("Opera", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("Raycast", StringComparison.OrdinalIgnoreCase)))
    Console.WriteLine($"  {a.Name} | item={a.FilePath} | icone={a.IconFilePath}");
Console.WriteLine();
Console.WriteLine("cute-borders / HERMES:");
foreach (var a in all.Where(x => x.Name.Contains("cute-borders", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("HERMES", StringComparison.OrdinalIgnoreCase)))
    Console.WriteLine($"  {a.Name} | item={a.FilePath} | icone={a.IconFilePath}");
Console.WriteLine();
Console.WriteLine("Itens com caminho mas SEM arquivo de ícone:");
foreach (var a in all.Where(x => !string.IsNullOrWhiteSpace(x.FilePath) && string.IsNullOrWhiteSpace(x.IconFilePath)).Take(10))
    Console.WriteLine($"  {Trunc(a.Name, 24),-24} | {a.FilePath}");
Console.WriteLine("Sem ícone nenhum:");
foreach (var a in all.Where(x => string.IsNullOrWhiteSpace(x.IconFilePath)).Take(20))
    Console.WriteLine($"  {Trunc(a.Name, 24),-24} | {Trunc(a.LocationShort, 16),-16} | {Trunc(a.FullCommand, 60)}");
Console.WriteLine();
foreach (var a in all.Where(x => !string.IsNullOrWhiteSpace(x.IconFilePath) && string.IsNullOrWhiteSpace(x.FilePath)).Take(15))
    Console.WriteLine($"  {Trunc(a.Name, 26),-26} -> {a.IconFilePath}");
Console.WriteLine();

Console.WriteLine("Sem caminho resolvido (comando cru):");
foreach (var a in all.Where(x => string.IsNullOrWhiteSpace(x.FilePath)).Take(20))
    Console.WriteLine($"  {Trunc(a.Name, 24),-24} | {Trunc(a.LocationShort, 18),-18} | {a.FullCommand}");
Console.WriteLine();

Console.WriteLine($"{"STATUS",-14} {"ORIGEM",-22} {"FABRICANTE",-34} NOME");
Console.WriteLine(new string('-', 140));
Console.WriteLine(new string('-', 140));
foreach (var a in all)
{
    string status = a.Status.ToString();
    string origem = Trunc(a.LocationShort, 22);
    string fab = Trunc(string.IsNullOrWhiteSpace(a.Publisher) ? "-" : a.Publisher, 34);
    Console.WriteLine($"{status,-14} {origem,-22} {fab,-34} {a.Name}");
    if (!string.IsNullOrWhiteSpace(a.Publisher) && string.IsNullOrWhiteSpace(a.FilePath))
        Console.WriteLine($"{"",-14} {"",-22} {"",-34}   (sem caminho resolvido: {a.FullCommand})");
}

// Linhas com data de alteração: prova de que o FILETIME do StartupApproved foi lido.
var comDataList = all.Where(a => a.StateChangeDate.HasValue).Take(8).ToList();
if (comDataList.Count > 0)
{
    Console.WriteLine("\nÚltimas alterações de estado lidas do StartupApproved:");
    foreach (var a in comDataList)
        Console.WriteLine($"  {a.StateChangeDate:dd/MM/yyyy HH:mm}  {a.Status,-10}  {a.Name}");
}

static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
