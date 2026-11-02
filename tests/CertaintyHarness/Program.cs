// Harness SOMENTE-LEITURA: valida as regras de "100% de certeza" que a nova
// FailedUninstallWindow usa para filtrar pastas/arquivos/registro pós-falha,
// com os valores reais do caso DuoDesk desta máquina. Não apaga nada.
using System.Text.RegularExpressions;
using KitLugia.Core;

static string StripVersion(string name) =>
    Regex.Replace(name.Trim(), @"\s+\d+(\.\d+)*\s*$", "").Trim();

static bool RegistroCerteza(string displayName, string installLocation, string ownRegPath, string path)
{
    if (string.IsNullOrWhiteSpace(path)) return false;
    if (!string.IsNullOrEmpty(ownRegPath) && path.Equals(ownRegPath, StringComparison.OrdinalIgnoreCase))
        return true;
    if (DeepUninstaller.ClassifyRegistrySafety(displayName, installLocation, path) == CleanupSafety.Safe)
        return true;
    var clean = StripVersion(displayName);
    foreach (var s in path.Split('\\'))
        if (s.Equals(clean, StringComparison.OrdinalIgnoreCase) || s.Equals(displayName, StringComparison.OrdinalIgnoreCase))
            return true;
    return false;
}

// Dados reais do registro nesta máquina (HKLM Uninstall\68800e7e-8dec-5142-ae5c-6cb4a1b6c5d1)
string displayName = "DuoDesk 2.5.0";
string installLoc = @"C:\Program Files\DuoDesk";          // derivado do UninstallString pelo kit
string ownReg = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\68800e7e-8dec-5142-ae5c-6cb4a1b6c5d1";
string clean = StripVersion(displayName);
Console.WriteLine($"displayName='{displayName}' cleanName='{clean}' installLoc='{installLoc}'");
Console.WriteLine();

int pass = 0, fail = 0;
void Check(string label, bool got, bool want)
{
    bool ok = got == want;
    if (ok) pass++; else fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {label}: got={got} want={want}");
}

Console.WriteLine("== ARQUIVOS/PASTAS (AutoCleanup.ConfiancaDe >= 100) ==");
int C(string p)
{
    int a = AutoCleanup.ConfiancaDe(displayName, installLoc, p);
    int b = clean != displayName ? Math.Max(a, AutoCleanup.ConfiancaDe(clean, installLoc, p)) : a;
    Console.WriteLine($"   ConfiancaDe('{p}') = {b} (bruto {a})");
    return b;
}
Check("pasta de instalação (raiz)", C(@"C:\Program Files\DuoDesk") >= 100, true);
Check("exe dentro da instalação", C(@"C:\Program Files\DuoDesk\Uninstall DuoDesk.exe") >= 100, true);
Check("subpasta da instalação", C(@"C:\Program Files\DuoDesk\bin\duodesk.exe") >= 100, true);
Check("AppData com nome exato (clean)", C(Environment.ExpandEnvironmentVariables(@"%AppData%\DuoDesk\settings.json")) >= 100, true);
Check("LocalAppData com nome exato (clean)", C(Environment.ExpandEnvironmentVariables(@"%LocalAppData%\DuoDesk\cache")) >= 100, true);
Check("arquivo solto em Program Files (NÃO é resíduo)", C(@"C:\Program Files\outro.dll") >= 100, false);
Check("pasta de OUTRO app", C(@"C:\Program Files\OutroApp\app.exe") >= 100, false);
Check("System32", C(@"C:\Windows\System32\duodesk.sys") >= 100, false);
Check("AppData de outro app", C(Environment.ExpandEnvironmentVariables(@"%AppData%\OutroApp\x.json")) >= 100, false);

Console.WriteLine();
Console.WriteLine("== REGISTRO (RegistroCerteza da janela) ==");
Check("chave Uninstall própria do app", RegistroCerteza(displayName, installLoc, ownReg, ownReg), true);
Check("HKCU\\Software\\DuoDesk (nome exato)", RegistroCerteza(displayName, installLoc, ownReg, @"HKEY_CURRENT_USER\Software\DuoDesk"), true);
Check("HKLM\\SOFTWARE\\DuoDesk (nome exato)", RegistroCerteza(displayName, installLoc, ownReg, @"HKEY_LOCAL_MACHINE\SOFTWARE\DuoDesk"), true);
Check("HKLM WOW6432Node\\DuoDesk", RegistroCerteza(displayName, installLoc, ownReg, @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\DuoDesk"), true);
Check("chave GUID de outro app (não deve ser 100%)", RegistroCerteza(displayName, installLoc, ownReg, @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), false);
Check("HKCU\\Software\\Microsoft (sistema)", RegistroCerteza(displayName, installLoc, ownReg, @"HKEY_CURRENT_USER\Software\Microsoft"), false);
Check("Uninstall de outro app com GUID", RegistroCerteza(displayName, installLoc, ownReg, @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OutroApp"), false);

Console.WriteLine();
Console.WriteLine($"RESULTADO: {pass} PASS / {fail} FAIL");
return fail == 0 ? 0 : 1;
