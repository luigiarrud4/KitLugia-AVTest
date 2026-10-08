using System;
using System.Reflection;
using System.Threading;
using System.Windows;
using KitLugia.GUI.Services;
using Microsoft.Win32;

// ===========================================================================
// gbrescue - REGRESSION TEST do resgate do tweak GLOBAL de rede
// ===========================================================================
// O BUG (corrigido nesta sessao):
//   O RescueNetworkBoostOnStartup tinha um gate `if (!GamePriorityEnabled)`.
//   Parecia proteger o boost ativo, mas criava prejuizo PERMANENTE:
//     1. o Kit morre com o boost de rede aplicado -> SystemResponsiveness = 10
//     2. o utilizador abre o Kit COM o GameBoost ligado
//     3. o gate impedia o resgate
//     4. o ApplyNetworkBoostV3 gravava _origSystemResponsiveness = 10 (o ORFAO)
//     5. dai em diante cada revert "restaurava" 10 -> o 20 (default do Windows)
//        perdia-se PARA SEMPRE
//
// ESTE TESTE e' o que distingue o codigo antigo do novo:
//   - com GamePriority = 1 (boost ligado), o resgate TEM de funcionar
//   - o codigo antigo nao fazia nada nesse caso -> este teste falhava
//
// SEGURANCA: o teste grava GamePriority=1 e SystemResponsiveness=10 para
// reproduzir o cenario, e RESTAURA TUDO no fim (bloco finally), para a maquina
// e o Kit do utilizador ficarem como estavam.

internal static class Program
{
    static int _falhas = 0;
    static void Check(string nome, bool ok, string det)
    {
        if (!ok) { _falhas++; Console.WriteLine($"  [FALHA] {nome}: {det}"); }
        else Console.WriteLine($"  [OK  ] {nome}: {det}");
    }
    static void Info(string nome, string det) => Console.WriteLine($"  [....] {nome}: {det}");

    const string SYS_PROFILE = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    const string TRAY = @"Software\KitLugia\TraySettings";
    const int DEFAULT_WINDOWS = 20;   // default documentado do SystemResponsiveness

    static object LerHklm(string nome)
    {
        using var k = Registry.LocalMachine.OpenSubKey(SYS_PROFILE);
        return k?.GetValue(nome);
    }

    static void EscreverHklm(string nome, object valor)
    {
        using var k = Registry.LocalMachine.OpenSubKey(SYS_PROFILE, true);
        if (k == null) return;
        if (valor == null) k.DeleteValue(nome, false);
        else k.SetValue(nome, valor, RegistryValueKind.DWord);
    }

    static object LerGamePriority()
    {
        using var k = Registry.CurrentUser.OpenSubKey(TRAY);
        return k?.GetValue("GamePriority");
    }

    static void EscreverGamePriority(object valor)
    {
        using var k = Registry.CurrentUser.OpenSubKey(TRAY, true);
        if (k == null) return;
        if (valor == null) k.DeleteValue("GamePriority", false);
        else k.SetValue("GamePriority", valor, RegistryValueKind.DWord);
    }

    static void InvocarResgate()
    {
        var m = typeof(TrayIconService).GetMethod("RescueNetworkBoostOnStartup",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        m.Invoke(null, null);
    }

    [STAThread]
    public static int Main(string[] args)
    {
        int exit = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (s, e) =>
        {
            try { exit = Cenario(); }
            catch (Exception ex) { Console.WriteLine("FALHA NO HARNESS: " + ex); exit = 2; }
            Console.Out.Flush();
            app.Shutdown(exit);
            var killer = new Timer(_ => Environment.Exit(exit), null, 5000, Timeout.Infinite);
        };
        app.Run();
        return exit;
    }

    static int Cenario()
    {
        Console.WriteLine("=== gbrescue - resgate do tweak global de rede ===");

        // --- guarda o estado original (para restaurar sempre) ---
        object sepOrig = LerHklm("SystemResponsiveness");
        object netOrig = LerHklm("NetworkThrottlingIndex");
        object gpOrig = LerGamePriority();
        Info("estado guardado",
             $"SystemResponsiveness={sepOrig ?? "(ausente)"} NetworkThrottlingIndex={netOrig ?? "(ausente)"} GamePriority={gpOrig ?? "(ausente)"}");

        try
        {
            // ---------- T1: o caso que estava QUEBRADO (GamePriority = 1) ----------
            Console.WriteLine();
            Console.WriteLine("--- T1: orfao + GameBoost LIGADO (o cenario do bug) ---");
            EscreverGamePriority(1);
            EscreverHklm("SystemResponsiveness", 10);   // o valor que o boost grava
            EscreverHklm("NetworkThrottlingIndex", 10);

            InvocarResgate();

            int? sep = LerHklm("SystemResponsiveness") as int?;
            object net = LerHklm("NetworkThrottlingIndex");

            Console.WriteLine($"    depois do resgate: SystemResponsiveness={sep?.ToString() ?? "(ausente)"} " +
                              $"NetworkThrottlingIndex={net ?? "(ausente)"}");
            Check("SystemResponsiveness voltou ao default do Windows (20), MESMO com o boost ligado",
                  sep == DEFAULT_WINDOWS, $"valor={sep?.ToString() ?? "(ausente)"} (esperado 20)");
            Check("NetworkThrottlingIndex orfao foi removido", net == null, $"valor={net ?? "(ausente)"}");

            // ---------- T2: o caso que sempre funcionou (GamePriority = 0) ----------
            Console.WriteLine();
            Console.WriteLine("--- T2: orfao + GameBoost DESLIGADO (caso que ja funcionava) ---");
            EscreverGamePriority(0);
            EscreverHklm("SystemResponsiveness", 10);
            EscreverHklm("NetworkThrottlingIndex", 10);

            InvocarResgate();

            sep = LerHklm("SystemResponsiveness") as int?;
            Check("SystemResponsiveness voltou a 20", sep == DEFAULT_WINDOWS, $"valor={sep?.ToString() ?? "(ausente)"}");

            // ---------- T3: nao toca em quem nao e' orfao ----------
            Console.WriteLine();
            Console.WriteLine("--- T3: valor LEGITIMO (nao e' 10) nao pode ser mexido ---");
            EscreverHklm("SystemResponsiveness", 30);   // um valor que nao e' o nosso
            InvocarResgate();
            sep = LerHklm("SystemResponsiveness") as int?;
            Check("um valor que nao e' o nosso fica intacto", sep == 30, $"valor={sep?.ToString() ?? "(ausente)"} (esperado 30)");
        }
        finally
        {
            // --- restaura SEMPRE, mesmo se algo falhar ---
            EscreverGamePriority(gpOrig);
            EscreverHklm("SystemResponsiveness", sepOrig);
            EscreverHklm("NetworkThrottlingIndex", netOrig);
            Console.WriteLine();
            Info("restaurado",
                 $"SystemResponsiveness={LerHklm("SystemResponsiveness") ?? "(ausente)"} " +
                 $"NetworkThrottlingIndex={LerHklm("NetworkThrottlingIndex") ?? "(ausente)"} " +
                 $"GamePriority={LerGamePriority() ?? "(ausente)"}");
        }

        Console.WriteLine(_falhas == 0 ? "\nTODOS OS TESTES PASSARAM" : $"\n{_falhas} VERIFICACAO(OES) FALHARAM");
        return _falhas;
    }
}
