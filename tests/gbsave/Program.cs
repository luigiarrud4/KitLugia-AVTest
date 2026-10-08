using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using KitLugia.GUI.Services;

// ===========================================================================
// gbsave - MORTE A MEIO DA ESCRITA do boost_rescue.txt (item 3.2 do checklist)
// ===========================================================================
// O ficheiro de resgate e' o que salva a maquina quando o Kit e' morto com o
// boost aplicado. Se ele ficar TRUNCADO (meia linha, ultima linha cortada), o
// resgate do arranque seguinte le' lixo: pior caso, nao restaura nada e as
// prioridades ficam presas PARA SEMPRE.
//
// O produto usa escrita atomica (tmp + move). Este teste NAO acredita na
// afirmacao: mata o escritor a meio, centenas de vezes, e inspeciona o que
// sobrou no disco.
//
//   --writer : enche o estado de 20.000 entradas (ficheiro ~400 KB, escrita
//              lenta o suficiente para se' apanhar a meio), alterna dois
//              conteudos (para derrotar a deduplicacao, como um estado que muda
//              em producao) e escreve em loop pelos caminhos REAIS
//              (SaveCrashRescue) ate' ser morto.
//   (padrao) : mata o escritor em instantes aleatorios e VALIDA o ficheiro.
//
// O que conta como falha:
//   - qualquer linha malformada (prova de escrita parcial)
//   - ficheiro presente mas nao terminado em newline (prova de truncagem)
//   - bytes que nao sejam o formato boost|<pid>|<prio> / throttle|<pid>
//
// O que NAO e' falha: o ficheiro estar ausente. A escrita apaga o destino antes
// do move, portanto morrer nesse instante perde o resgate (mas nunca o corrompe).
// Contabilizamos isso a' parte, e a contagem de .tmp deixados para tras e' a
// prova de que o teste chegou mesmo a apanhar o escritor a meio.
// ===========================================================================

internal static class Program
{
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint code);

    static string RescueFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KitLugia", "boost_rescue.txt");

    const BindingFlags PRIV = BindingFlags.NonPublic | BindingFlags.Instance;

    static int _falhas = 0;
    static void Check(string nome, bool ok, string det)
    {
        if (!ok) { _falhas++; Console.WriteLine($"  [FALHA] {nome}: {det}"); }
        else Console.WriteLine($"  [OK  ] {nome}: {det}");
    }
    static void Info(string nome, string det) => Console.WriteLine($"  [....] {nome}: {det}");

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--writer")
        {
            // Modo escritor: sem WPF, sem mensagens. So' escreve.
            ModoEscritor();
            return 0;   // inalcancavel
        }

        int rounds = 40;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--rounds" && int.TryParse(args[i + 1], out int r)) rounds = r;

        int exit = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (s, e) =>
        {
            try { exit = Cenario(rounds); }
            catch (Exception ex) { Console.WriteLine("FALHA NO HARNESS: " + ex); exit = 2; }
            Console.Out.Flush();
            app.Shutdown(exit);
            var killer = new Timer(_ => Environment.Exit(exit), null, 5000, Timeout.Infinite);
        };
        app.Run();
        return exit;
    }

    // ---------------------------------------------------------------- escritor
    static void ModoEscritor()
    {
        var svc = new TrayIconService();
        var fOriginais = typeof(TrayIconService).GetField("_originalPriorities", PRIV)!;
        var originais = (IDictionary)fOriginais.GetValue(svc)!;
        var fThrottled = typeof(TrayIconService).GetField("_throttledProcesses", PRIV)!;
        var throttled = fThrottled.GetValue(svc)!;
        var addThr = throttled.GetType().GetMethod("Add")!;

        // 20.000 entradas: ficheiro grande o bastante para a escrita demorar e
        // ser apanhada a meio. Sao PIDs falsos - este processo nunca os vai
        // tocar, so' serve para dimensionar o ficheiro.
        for (uint pid = 900000; pid < 920000; pid++)
        {
            try { originais[pid] = ProcessPriorityClass.Normal; } catch { }
            try { addThr.Invoke(throttled, new object[] { pid }); } catch { }
        }

        // Marcador de prontidao: sem ele o matador podia disparar durante o
        // arranque do WPF (antes da 1a escrita) e a ronda nao provava nada.
        try { File.WriteAllText(RescueFile + ".ready", "1"); } catch { }

        var save = typeof(TrayIconService).GetMethod("SaveCrashRescue", PRIV)!;
        bool alterna = false;
        while (true)
        {
            // Alterna um PID entre as duas escritas: o conteudo muda, portanto a
            // deduplicacao deixa passar tudo - exatamente o que acontece em
            // producao quando processos entram e saem do boost.
            try { originais[930000u] = alterna ? ProcessPriorityClass.Normal : ProcessPriorityClass.High; }
            catch { }
            alterna = !alterna;
            try { save.Invoke(svc, new object[] { false }); } catch { }
        }
    }

    // ----------------------------------------------------------------- matador
    static int Cenario(int rounds)
    {
        Console.WriteLine($"=== gbsave - morte a meio da escrita do boost_rescue.txt ({rounds} rondas) ===");
        Console.WriteLine($"  ficheiro: {RescueFile}");

        string self = Assembly.GetEntryAssembly()!.Location;
        string exe = Path.Combine(Path.GetDirectoryName(self)!,
            Path.GetFileNameWithoutExtension(self) + ".exe");
        if (!File.Exists(exe)) exe = self;
        Info("escritor", exe);

        string tmp = RescueFile + ".tmp";

        int validos = 0, truncados = 0, ausentes = 0, tmpsDeixados = 0, rondasInvalidas = 0;
        var exemplosFalha = new List<string>();
        var rnd = new Random(20261005);

        for (int r = 0; r < rounds; r++)
        {
            try { if (File.Exists(RescueFile)) File.Delete(RescueFile); } catch { }
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }

            string ready = RescueFile + ".ready";
            var w = Process.Start(new ProcessStartInfo(exe, "--writer") { UseShellExecute = false })!;

            // So' comeca a contar o tempo quando o escritor diz que esta' pronto.
            var espera = Stopwatch.StartNew();
            while (!File.Exists(ready) && espera.ElapsedMilliseconds < 15000) Thread.Sleep(25);
            bool pronto = File.Exists(ready);
            try { if (File.Exists(ready)) File.Delete(ready); } catch { }
            if (!pronto) { rondasInvalidas++; try { w.Kill(); } catch { } continue; }

            Thread.Sleep(rnd.Next(30, 400));    // instante aleatorio dentro da escrita
            TerminateProcess(w.Handle, 0xDEAD);
            try { w.WaitForExit(3000); } catch { }

            bool temTmp = File.Exists(tmp);
            if (temTmp) tmpsDeixados++;

            if (!File.Exists(RescueFile)) { ausentes++; continue; }

            string conteudo;
            try { conteudo = File.ReadAllText(RescueFile); }
            catch (Exception ex) { truncados++; exemplosFalha.Add($"ronda {r}: ilegivel ({ex.GetType().Name})"); continue; }

            // Validacao: ficheiro vazio, sem newline final ou com linha fora do
            // formato = escrita parcial.
            string erro = null;
            if (conteudo.Length == 0) erro = "ficheiro vazio";
            else if (!conteudo.EndsWith("\n")) erro = $"nao termina em newline (ultimos 40: '{Fim(conteudo, 40)}')";
            else
            {
                foreach (var linha in conteudo.Split('\n'))
                {
                    if (linha.Length == 0) continue;   // ultimo split depois do \n
                    var p = linha.Split('|');
                    bool ok = (p.Length == 3 && p[0] == "boost" && uint.TryParse(p[1], out _) && int.TryParse(p[2], out _))
                           || (p.Length == 2 && p[0] == "throttle" && uint.TryParse(p[1], out _));
                    if (!ok) { erro = $"linha malformada: '{linha}'"; break; }
                }
            }

            if (erro == null) validos++;
            else
            {
                truncados++;
                if (exemplosFalha.Count < 6) exemplosFalha.Add($"ronda {r}: {erro}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("--- RESULTADO ---");
        Console.WriteLine($"  ficheiro valido depois do kill : {validos}/{rounds}");
        Console.WriteLine($"  TRUNCADO/corrompido           : {truncados}/{rounds}");
        Console.WriteLine($"  ausente (perdido no kill)     : {ausentes}/{rounds}");
        Console.WriteLine($"  rondas invalidas (nao arrancou): {rondasInvalidas}/{rounds}");
        Console.WriteLine($"  *.tmp deixados para tras      : {tmpsDeixados}/{rounds}  (prova de que o kill apanhou a escrita a meio)");
        foreach (var e in exemplosFalha) Console.WriteLine($"    {e}");
        Console.WriteLine();
        Info("tamanho tipico do ficheiro",
             File.Exists(RescueFile) ? $"{new FileInfo(RescueFile).Length} bytes" : "(ausente agora)");

        Console.WriteLine();
        int efetivas = validos + truncados;
        Check("NUNCA ficou truncado/corrompido",
              truncados == 0, $"{truncados} de {efetivas} escrita(s) efetiva(s)");
        Check("o teste chegou mesmo a apanhar a escrita a meio",
              tmpsDeixados > 0, $"{tmpsDeixados} *.tmp deixados (0 = o teste nao prova nada)");
        Check("houve escritas efetivas para inspecionar", validos > 0, $"{validos} ficheiro(s) valido(s)");

        // limpeza: mata os ESCRITORES que possam ter ficado, mas NUNCA a si proprio
        // (GetProcessesByName devolve tambem este processo - matava-se antes de
        // imprimir o resultado e de devolver o exit code).
        foreach (var p in Process.GetProcessesByName("GbSave"))
        {
            if (p.Id == Environment.ProcessId) continue;
            try { p.Kill(); } catch { }
        }
        try { if (File.Exists(RescueFile)) File.Delete(RescueFile); } catch { }
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        try { string rd = RescueFile + ".ready"; if (File.Exists(rd)) File.Delete(rd); } catch { }

        Console.WriteLine(_falhas == 0 ? "\nTODOS OS TESTES PASSARAM" : $"\n{_falhas} VERIFICACAO(OES) FALHARAM");
        return _falhas;
    }

    static string Fim(string s, int n) => s.Length <= n ? s : s.Substring(s.Length - n);
}
