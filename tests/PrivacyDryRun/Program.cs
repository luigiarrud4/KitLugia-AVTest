// Teste READ-ONLY da PrivacyPage (OOShutUpManager):
// valida as 160 settings e SIMULA os presets SEM escrever nada no registro.
// - Estrutura: hive valida, ValueName presente, SafeValue/UnsafeValue definidos
// - Leitura real: Registry.GetValue (somente leitura) + IsPrivacySettingApplied
// - Servicos: consulta WMI StartMode (somente leitura) - servico existe?
// - Simulacao: quantas escritas cada preset / ApplyCustomSelection faria
using System.Diagnostics;
using Microsoft.Win32;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var settings = KitLugia.Core.OOShutUpManager.GetPrivacySettings();
Console.WriteLine($"=== PrivacyPage DRY RUN (nenhuma escrita no registro) ===");
Console.WriteLine($"Total de settings: {settings.Count}\n");

int errors = 0, warnings = 0;

// ---------- 1. Validacao estrutural ----------
foreach (var s in settings)
{
    if (s.IsService)
    {
        if (string.IsNullOrWhiteSpace(s.ServiceName))
        { Console.WriteLine($"[ERRO] '{s.Name}': IsService sem ServiceName"); errors++; }
        if (s.SafeValue == null || s.UnsafeValue == null)
        { Console.WriteLine($"[ERRO] Serviço '{s.ServiceName}': SafeValue/UnsafeValue nulos"); errors++; }
        continue;
    }
    if (string.IsNullOrWhiteSpace(s.RegistryPath) || string.IsNullOrWhiteSpace(s.ValueName))
    { Console.WriteLine($"[ERRO] '{s.Name}': RegistryPath/ValueName vazio"); errors++; continue; }
    if (!s.RegistryPath.StartsWith("HKEY_LOCAL_MACHINE") && !s.RegistryPath.StartsWith("HKEY_CURRENT_USER"))
    { Console.WriteLine($"[ERRO] '{s.Name}': hive inválida: {s.RegistryPath}"); errors++; continue; }
    if (s.SafeValue == null)
    { Console.WriteLine($"[AVISO] '{s.Name}': SafeValue nulo (apply gravaria 0)"); warnings++; }
}
Console.WriteLine($"[ESTRUTURA] {errors} erro(s), {warnings} aviso(s)\n");

// ---------- 2. Leitura real (read-only) ----------
int valueExists = 0, applied = 0, servicesFound = 0, servicesMissing = 0;
var sw = Stopwatch.StartNew();
foreach (var s in settings)
{
    if (s.IsService)
    {
        var mode = KitLugia.Core.SystemUtils.GetServiceStartMode(s.ServiceName!);
        if (mode == null)
        { Console.WriteLine($"[AVISO] Serviço '{s.ServiceName}' NÃO existe no sistema (sc config falharia)"); servicesMissing++; warnings++; }
        else
        {
            servicesFound++;
            bool prot = KitLugia.Core.OOShutUpManager.IsPrivacySettingApplied(s);
            Console.WriteLine($"[SERVIÇO] {s.ServiceName}: StartMode={mode}, protegido={prot}");
        }
        continue;
    }
    var val = Registry.GetValue(s.RegistryPath, s.ValueName, null); // read-only
    if (val != null) valueExists++;
    if (KitLugia.Core.OOShutUpManager.IsPrivacySettingApplied(s)) applied++;
}
sw.Stop();
int regCount = settings.Count(s => !s.IsService);
Console.WriteLine($"\n[LEITURA] valores presentes no registro: {valueExists}/{regCount} (somente leitura)");
Console.WriteLine($"[ESTADO] {applied} configurações protegidas agora");
Console.WriteLine($"[SERVIÇOS] {servicesFound} encontrados, {servicesMissing} ausentes");
Console.WriteLine($"[PERF] IsPrivacySettingApplied x{settings.Count} em {sw.ElapsedMilliseconds}ms\n");

// ---------- 3. Amostra do que a escrita faria (valida comando, sem escrever) ----------
Console.WriteLine("[AMOSTRA] Comandos que ApplyPrivacySetting emitiria (simulado):");
foreach (var s in settings.Where(s => !s.IsService).Take(5))
{
    string hive = s.RegistryPath.StartsWith("HKEY_LOCAL_MACHINE") ? "HKLM" : "HKCU";
    string subKey = s.RegistryPath.Substring(s.RegistryPath.IndexOf('\\') + 1);
    Console.WriteLine($"  reg add \"{hive}\\{subKey}\" /v \"{s.ValueName}\" /t {(s.SafeValue is int ? "REG_DWORD" : "REG_SZ")} /d {s.SafeValue} /f");
}
foreach (var s in settings.Where(s => s.IsService).Take(3))
{
    int mode = Convert.ToInt32(s.SafeValue);
    string modeStr = mode == 4 ? "disabled" : (mode == 2 ? "auto" : "demand");
    Console.WriteLine($"  sc config \"{s.ServiceName}\" start= {modeStr}" + (mode == 4 ? $" + sc stop \"{s.ServiceName}\"" : ""));
}
Console.WriteLine();

// ---------- 4. Simulacao dos 3 presets (contagem, sem escrita) ----------
foreach (var lvl in new[] { KitLugia.Core.OOShutUpManager.PrivacyLevel.Recommended,
                            KitLugia.Core.OOShutUpManager.PrivacyLevel.Limited,
                            KitLugia.Core.OOShutUpManager.PrivacyLevel.NotRecommended })
{
    int n = settings.Count(s => (int)s.Level <= (int)lvl);
    Console.WriteLine($"[SIMULAÇÃO] Preset {lvl}: aplicaria {n} configurações");
}
Console.WriteLine($"[SIMULAÇÃO] ApplyCustomSelection (tudo marcado): {settings.Count} aplica, 0 reverte");
Console.WriteLine($"[SIMULAÇÃO] ApplyCustomSelection (nada marcado): 0 aplica, {settings.Count} reverte");

// ---------- 5. Simulacao dos handlers da UI ----------
var dangerous = settings.Count(s => s.Level == KitLugia.Core.OOShutUpManager.PrivacyLevel.NotRecommended);
Console.WriteLine($"[UI] Confirmacao de perigo dispararia com {dangerous} itens vermelhos");

Console.WriteLine();
if (errors == 0 && servicesMissing == 0)
    Console.WriteLine("=== RESULTADO: TODOS OS COMANDOS SÃO VÁLIDOS (leitura apenas, registro intocado) ===");
else if (errors == 0)
    Console.WriteLine($"=== RESULTADO: COMANDOS VÁLIDOS, mas {servicesMissing} serviço(s) ausente(s) no sistema ===");
else
    Console.WriteLine($"=== RESULTADO: {errors} ERRO(S) encontrados — veja acima ===");
