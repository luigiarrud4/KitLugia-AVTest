using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using KitLugia.GUI.Services;
using Microsoft.Win32;

// ===========================================================================
// gblimited - MATRIZ "ELEVADO vs NAO ELEVADO" (item 3.3 do checklist)
// ===========================================================================
// Todos os testes anteriores correram numa shell ELEVADA. Mas o Kit pode ser
// aberto SEM elevacao (basta o utilizador clicar sem "Executar como
// administrador") - e ai' metade das suas escritas passa a ser negada pelo
// Windows. A pergunta que este teste responde:
//
//   "quando o Windows nega uma escrita, o motor degrada com elegancia ou
//    deixa estado a meio?"
//
// Nao basta "nao rebentar". Tem de sair daqui a saber:
//   1. o boost de rede falha LIMPO (registo intacto, estado interno intacto)
//   2. o revert e o resgate nao rebentam nem escrevem lixo
//   3. o que NAO precisa de admin (prioridade de processos proprios, ficheiro
//      de resgate em %LocalAppData%) continua a funcionar
//   4. e uma limitacao HONESTA: o tweak global de rede so' e' reversivel com
//      elevacao - sem ela, o resgate nao consegue corrigir um orfao
//
// COMO ESTE TESTE E' CORRIDO SEM ELEVACAO:
//   o processo-pai e' elevado, portanto tem de ser lancado por fora. O runner
//   usa uma Scheduled Task com -RunLevel Limited (token normal do utilizador).
//   Se este exe arrancar elevado, o teste ABORTA - correr elevado nao prova
//   nada acerca da linha "nao elevado" da matriz.
//
// USO: GbLimited.exe [--out <ficheiro>]
// ===========================================================================

internal static class Program
{
    static int _falhas = 0;
    static void Check(string nome, bool ok, string det)
    {
        if (!ok) { _falhas++; Escrever($"  [FALHA] {nome}: {det}"); }
        else Escrever($"  [OK  ] {nome}: {det}");
    }
    static void Info(string nome, string det) => Escrever($"  [....] {nome}: {det}");

    static TextWriter _tee;
    static void Escrever(string s) { Console.WriteLine(s); try { _tee?.WriteLine(s); } catch { } }

    const string SYS_PROFILE = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    const BindingFlags PRIV = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    static string RescueFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KitLugia", "boost_rescue.txt");

    static object LerResp()
    {
        using var k = Registry.LocalMachine.OpenSubKey(SYS_PROFILE);
        return k?.GetValue("SystemResponsiveness");
    }
    static object LerNet()
    {
        using var k = Registry.LocalMachine.OpenSubKey(SYS_PROFILE);
        return k?.GetValue("NetworkThrottlingIndex");
    }

    static uint Prio(uint pid)
    {
        var p = Process.GetProcessById((int)pid);
        try { return (uint)p.PriorityClass; }
        finally { p.Dispose(); }
    }

    // As classes de prioridade do Windows sao BITMASKS, nao uma escala:
    // BelowNormal=0x4000 (16384) e" numericamente MAIOR que High=0x80 (128),
    // mas HIERARQUICAMENTE menor. Comparar os valores crus da' resultados
    // errados (foi o que fez este teste falhar na 1a versao).
    static int Rank(ProcessPriorityClass c) => c switch
    {
        ProcessPriorityClass.Idle => 0,
        ProcessPriorityClass.BelowNormal => 1,
        ProcessPriorityClass.Normal => 2,
        ProcessPriorityClass.AboveNormal => 3,
        ProcessPriorityClass.High => 4,
        ProcessPriorityClass.RealTime => 5,
        _ => 9
    };
    // Rank de um PID (le a classe atual do processo).
    static int Rank(uint pid)
    {
        var p = Process.GetProcessById((int)pid);
        try { return Rank(p.PriorityClass); }
        finally { p.Dispose(); }
    }

    // Rank de um VALOR de prioridade ja' lido (nao confundir com Rank(pid)!).
    static int RankValor(uint valor) => Rank((ProcessPriorityClass)valor);

    [STAThread]
    public static int Main(string[] args)
    {
        string outFile = null;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--out") outFile = args[i + 1];

        // O transcript vai para ficheiro porque uma Scheduled Task nao devolve stdout.
        StreamWriter file = null;
        if (outFile != null)
        {
            try { file = new StreamWriter(outFile, false, new UTF8Encoding(false)) { AutoFlush = true }; }
            catch { }
        }
        _tee = file;

        int exit = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (s, e) =>
        {
            try { exit = Cenario(); }
            catch (Exception ex) { Escrever("FALHA NO HARNESS: " + ex); exit = 2; }
            try { file?.Flush(); } catch { }
            app.Shutdown(exit);
            var killer = new Timer(_ => Environment.Exit(exit), null, 6000, Timeout.Infinite);
        };
        app.Run();
        try { file?.Dispose(); } catch { }
        return exit;
    }

    static int Cenario()
    {
        Escrever("=== gblimited - motor GameBoost SEM elevacao ===");

        var ident = System.Security.Principal.WindowsIdentity.GetCurrent();
        bool elevado = new System.Security.Principal.WindowsPrincipal(ident)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

        Escrever($"  build={Environment.OSVersion.Version.Build} utilizador={ident.Name} elevado={elevado}");

        if (elevado)
        {
            Escrever("");
            Escrever("  ABORTADO: este exe correu ELEVADO. A linha \"nao elevado\" da matriz nao");
            Escrever("  fica provada assim. Lancar via Scheduled Task com -RunLevel Limited.");
            Check("correu SEM elevacao", false, "elevado=True (o teste e' invalido nesta forma)");
            return 3;
        }

        var respAntes = LerResp();
        var netAntes = LerNet();
        Info("registo antes", $"SystemResponsiveness={respAntes ?? "(ausente)"} NetworkThrottlingIndex={netAntes ?? "(ausente)"}");

        var svc = new TrayIconService();
        var fApplied = typeof(TrayIconService).GetField("_networkBoostApplied",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var fOrigResp = typeof(TrayIconService).GetField("_origSystemResponsiveness",
            BindingFlags.NonPublic | BindingFlags.Instance);

        // ---------- 1. boost de rede: tem de FALHAR LIMPO ----------
        Escrever("");
        Escrever("--- 1. boost global de rede (HKLM) sem elevacao ---");
        var mApplyNet = typeof(TrayIconService).GetMethod("ApplyNetworkBoostV3",
            BindingFlags.NonPublic | BindingFlags.Instance);
        bool rebentou = false;
        try { mApplyNet.Invoke(svc, null); }
        catch (Exception ex) { rebentou = true; Escrever("    excecao: " + ex.InnerException?.Message ?? ex.Message); }

        Check("ApplyNetworkBoostV3 nao propagou excecao", !rebentou, rebentou ? "lancou" : "silencioso");
        Check("registo ficou INTACTO (o Windows negou a escrita)",
              Equals(LerResp(), respAntes) && Equals(LerNet(), netAntes),
              $"resp={LerResp() ?? "(ausente)"} net={LerNet() ?? "(ausente)"}");

        bool applied = fApplied != null && (bool)(fApplied.GetValue(svc) ?? false);
        object origResp = fOrigResp?.GetValue(svc);
        Check("estado interno NAO ficou \"aplicado a meio\"",
              !applied && origResp == null,
              $"_networkBoostApplied={applied} _origSystemResponsiveness={origResp ?? "null"}");

        // ---------- 2. revert e resgate: nao rebentam ----------
        Escrever("");
        Escrever("--- 2. revert e resgate sem elevacao ---");
        var mRevertNet = typeof(TrayIconService).GetMethod("RevertNetworkBoost",
            BindingFlags.NonPublic | BindingFlags.Instance);
        rebentou = false;
        try { mRevertNet.Invoke(svc, null); }
        catch (Exception ex) { rebentou = true; Escrever("    excecao: " + (ex.InnerException?.Message ?? ex.Message)); }
        Check("RevertNetworkBoost nao propagou excecao", !rebentou, rebentou ? "lancou" : "silencioso");
        Check("registo continua intacto depois do revert",
              Equals(LerResp(), respAntes), $"resp={LerResp() ?? "(ausente)"}");

        var mRescue = typeof(TrayIconService).GetMethod("RescueNetworkBoostOnStartup", PRIV);
        rebentou = false;
        try { mRescue.Invoke(null, null); }
        catch (Exception ex) { rebentou = true; Escrever("    excecao: " + (ex.InnerException?.Message ?? ex.Message)); }
        Check("RescueNetworkBoostOnStartup nao propagou excecao", !rebentou, rebentou ? "lancou" : "silencioso");

        // Limitacao HONESTA: sem elevacao o resgate nao consegue escrever no HKLM.
        bool respIgual = Equals(LerResp(), respAntes);
        Info("LIMITACAO ASSUMIDA: sem elevacao o resgate nao corrige um orfao no HKLM",
             respAntes is int r && r == 10
                 ? (respIgual ? "o valor 10 continua (nao foi corrigido) - so' o Kit elevado o corrige"
                              : "foi corrigido?!")
                 : $"o valor atual ({respAntes ?? "(ausente)"}) nao e' um orfao 10, nao ha nada a corrigir");

        // ---------- 3. o que NAO precisa de admin ----------
        Escrever("");
        Escrever("--- 3. o que continua a funcionar sem elevacao ---");

        // 3a. ficheiro de resgate em %LocalAppData% (escrita do utilizador)
        var fOriginais = typeof(TrayIconService).GetField("_originalPriorities",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var mSave = typeof(TrayIconService).GetMethod("SaveCrashRescue",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var mClear = typeof(TrayIconService).GetMethod("ClearCrashRescueFile",
            BindingFlags.NonPublic | BindingFlags.Static);
        var originais = (IDictionary)fOriginais.GetValue(svc);

        bool tinhaFicheiro = File.Exists(RescueFile);
        string conteudoAntes = tinhaFicheiro ? File.ReadAllText(RescueFile) : null;

        var alvo = Process.Start(new ProcessStartInfo("charmap.exe") { UseShellExecute = false });
        if (alvo == null) { Escrever("  nao consegui arrancar o charmap"); return 2; }
        Thread.Sleep(1500);
        uint pid = (uint)alvo.Id;

        // O Task Scheduler corre as tarefas a BELOW_NORMAL por omissao, e o filho
        // herda isso. Normalizamos para Normal para a medicao ser comparavel com
        // a corrida elevada (onde o charmap nasce Normal).
        uint prioBruta = Prio(pid);
        if (Rank(pid) != 2)
        {
            try { alvo.PriorityClass = ProcessPriorityClass.Normal; Thread.Sleep(200); }
            catch (Exception ex) { Escrever("    aviso: nao consegui normalizar a prioridade: " + ex.Message); }
        }
        uint prioInicial = Prio(pid);
        Info("prioridade do alvo normalizada", $"{prioBruta} -> {prioInicial} (rank {Rank(pid)}) ");

        try { originais[pid] = (ProcessPriorityClass)prioInicial; } catch { }
        mSave.Invoke(svc, new object[] { true });
        Thread.Sleep(150);
        bool ficheiroEscrito = File.Exists(RescueFile);
        Check("SaveCrashRescue escreveu em %LocalAppData% sem elevacao",
              ficheiroEscrito, ficheiroEscrito ? $"sim ({new FileInfo(RescueFile).Length} bytes)" : "NAO");

        // 3b. boost/revert de um processo proprio (o caso real de uso)
        var mPromote = typeof(TrayIconService).GetMethod("PromoteBoost",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var mModern = typeof(TrayIconService).GetMethod("ApplyBoostModern",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var nivelFoco = mModern.GetParameters()[1].ParameterType;
        object valorFoco = Enum.Parse(nivelFoco, "Foco");

        try { mPromote.Invoke(svc, new object[] { pid, "charmap" }); } catch { }
        try { mModern.Invoke(svc, new object[] { pid, valorFoco }); } catch { }
        Thread.Sleep(200);
        uint prioBoost = Prio(pid);

        svc.RevertAllBoostTargets();
        Thread.Sleep(300);
        uint prioRevert = Prio(pid);

        Info("prioridade: inicial -> boost -> revert",
             $"{prioInicial} -> {prioBoost} -> {prioRevert} " +
             $"(rank {RankValor(prioInicial)} -> {RankValor(prioBoost)} -> {RankValor(prioRevert)})");
        Check("o boost deu resultado sem elevacao (processo proprio)",
              RankValor(prioBoost) > RankValor(prioInicial),
              $"rank {RankValor(prioInicial)} -> {RankValor(prioBoost)} ({prioInicial} -> {prioBoost})");
        Check("o revert devolveu a prioridade original",
              prioRevert == prioInicial, $"{prioInicial} -> {prioRevert}");

        // ---------- limpeza ----------
        try { svc.ShutdownGameBoost(); } catch { }
        try { alvo.Kill(); } catch { }
        Thread.Sleep(300);
        foreach (var p in Process.GetProcessesByName("charmap")) { try { p.Kill(); } catch { } }

        // devolve o ficheiro de resgate ao que era (o Kit do utilizador pode estar a correr)
        try
        {
            if (tinhaFicheiro) File.WriteAllText(RescueFile, conteudoAntes);
            else if (File.Exists(RescueFile)) File.Delete(RescueFile);
        }
        catch { }

        Escrever("");
        Escrever(_falhas == 0 ? "TODOS OS TESTES PASSARAM" : $"{_falhas} VERIFICACAO(OES) FALHARAM");
        return _falhas;
    }
}
