using System.Reflection;
using KitLugia.Core;

// Harness de verificação (somente leitura): chama os métodos privados do
// DeepUninstaller via reflexão com os dados REAIS do DuoDesk 2.5.0 (lidos do
// registro desta máquina) e imprime o comando silencioso gerado.
// NÃO executa nenhum desinstalador.

internal static class Program
{
    private const string DuoDeskUninstall = "\"C:\\Program Files\\DuoDesk\\Uninstall DuoDesk.exe\" /allusers";
    private const string DuoDeskQuiet = "\"C:\\Program Files\\DuoDesk\\Uninstall DuoDesk.exe\" /allusers /S";

    private static void Main()
    {
        var t = typeof(DeepUninstaller);
        const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Static;

        var genQuiet = (string)t.GetMethod("GenerateQuietUninstallString", F)!
                            .Invoke(null, new object?[] { DuoDeskUninstall, "" })!;
        var genFromQuiet = (string)t.GetMethod("GenerateQuietUninstallString", F)!
                                .Invoke(null, new object?[] { DuoDeskQuiet, "" })!;
        var looksSilent = (Func<string, bool>)(s => (bool)t.GetMethod("LooksSilent", F)!
                                                    .Invoke(null, new object?[] { s })!);
        var interpret = t.GetMethod("InterpretExitCode", F)!;
        var parse = t.GetMethod("ParseCommandLine", F)!;

        Console.WriteLine("== DuoDesk 2.5.0 (dados reais do registro desta maquina) ==");
        Console.WriteLine($"UninstallString      : {DuoDeskUninstall}");
        Console.WriteLine($"QuietUninstallString : {DuoDeskQuiet}");
        Console.WriteLine($"LooksSilent(quiet)   : {looksSilent(DuoDeskQuiet)}   <-- engine prefere esta");
        Console.WriteLine($"LooksSilent(plain)   : {looksSilent(DuoDeskUninstall)}");
        Console.WriteLine($"GenerateQuiet(plain) : {genQuiet}");
        Console.WriteLine($"GenerateQuiet(quiet) : {genFromQuiet}");

        var (fn, fa) = ((string, string))parse.Invoke(null, new object?[] { genQuiet })!;
        Console.WriteLine($"ParseCommandLine     : file='{fn}' args='{fa}'");
        string exeName = Path.GetFileNameWithoutExtension(fn).ToLowerInvariant();
        bool exeLooksUninstaller = exeName.Contains("uninstall") || exeName.Contains("unins");
        Console.WriteLine($"exe parece uninstaller: {exeLooksUninstaller}  -> silentCapable = true (10 min, nunca morto)");

        Console.WriteLine();
        Console.WriteLine("== InterpretExitCode ==");
        foreach (var code in new[] { 0, 3010, 1605, 1602, 1, 5 })
        {
            var r = new DeepUninstaller.UninstallResult();
            bool ok = (bool)interpret.Invoke(null, new object?[] { code, DuoDeskQuiet, r })!;
            Console.WriteLine($"  code {code,5} -> success={ok,-5} errors=[{string.Join(";", r.Errors)}]");
        }

        Console.WriteLine();
        Console.WriteLine("== Sanity MSI ==");
        string msi = "MsiExec.exe /I{68800E7E-8DEC-5142-AE5C-6CB4A1B6C5D1}";
        string msiQuiet = (string)t.GetMethod("GenerateQuietUninstallString", F)!.Invoke(null, new object?[] { msi, "" })!;
        Console.WriteLine($"  GenerateQuiet(msi)  : {msiQuiet}");
        Console.WriteLine($"  LooksSilent(msi)    : {looksSilent(msiQuiet)}");

        Console.WriteLine();
        Console.WriteLine("== Sanity NSIS (unins000) ==");
        string nsis = "\"C:\\Program Files\\Foo\\unins000.exe\"";
        Console.WriteLine($"  GenerateQuiet(nsis) : {t.GetMethod("GenerateQuietUninstallString", F)!.Invoke(null, new object?[] { nsis, "" })}");
    }
}
