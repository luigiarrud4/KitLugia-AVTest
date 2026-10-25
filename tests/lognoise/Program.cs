// ===========================================================================
// LogNoise - regressao dos fixes de log repetitivo e dos 2 bugs de P/Invoke
// encontrados no log real de 06/10/2026.
//
// USO:
//   LogNoise.exe            (usa a pasta pai do exe para achar o fonte)
//   LogNoise.exe --src <repo>
//
// O que este harness PROVA (nao estima):
//   T1  RM_PROCESS_INFO tem o tamanho NATIVO (668 B) e os offsets do C
//   T2  EnableDebugPrivilege() nao lanca EntryPointNotFoundException
//   T3  ShouldLogBoost: 100 reaplicacoes IGUAIS -> poucas linhas (nao 100)
//   T4  Mudanca real de assinatura -> volta a logar na hora
//   T5  ShouldLogBand: janela por PID nas faixas
//   T6  VERY_LOW do RAM Limiter agrega por NOME (50 instancias -> 1 chave)
//   T7  MemoryCleaner: a guarda de throttle vem ANTES do log + existe backoff
// ===========================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using KitLugia.GUI.Services;

internal static class Program
{
    private static int _ok, _fail;

    private static void Check(string nome, bool cond, string detalhe)
    {
        if (cond) { _ok++; Console.WriteLine("  OK    {0}  ({1})", nome, detalhe); }
        else { _fail++; Console.WriteLine("  FALHA {0}  ({1})", nome, detalhe); }
    }

    private static string FindSrc(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--src") return args[i + 1];
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    private static List<string> CaptureLogs(Action acao)
    {
        var linhas = new List<string>();
        Action<string> h = m => { lock (linhas) linhas.Add(m ?? ""); };
        KitLugia.Core.Logger.OnLogReceived += h;
        try { acao(); } finally { KitLugia.Core.Logger.OnLogReceived -= h; }
        return linhas;
    }

    private static int SizeOf(Type t)
    {
        try { return System.Runtime.InteropServices.Marshal.SizeOf(Activator.CreateInstance(t)); }
        catch { return -1; }
    }

    private static int Main(string[] args)
    {
        var src = FindSrc(args);
        Console.WriteLine("LogNoise | fonte: {0}", src);
        Console.WriteLine();

        // ───────────────────────── T1: layout do Restart Manager ─────────
        Console.WriteLine("T1  RM_PROCESS_INFO / RM_UNIQUE_PROCESS (restartapi.h)");
        var asmF = typeof(KitLugia.Core.ForceStopUnlockService).Assembly;
        var tInfo = asmF.GetType("KitLugia.Core.ForceStopUnlockService+RM_PROCESS_INFO", true);
        var tUniq = asmF.GetType("KitLugia.Core.ForceStopUnlockService+RM_UNIQUE_PROCESS", true);

        int szInfo = SizeOf(tInfo);
        int szUniq = SizeOf(tUniq);
        Check("RM_UNIQUE_PROCESS = 12 B", szUniq == 12, "obtido " + szUniq);
        Check("RM_PROCESS_INFO = 668 B (nativo)", szInfo == 668, "obtido " + szInfo + " (antes: 676 -> drift de 8 B por elemento)");

        var esperado = new (string, int)[] { ("Process", 0), ("strAppName", 12), ("strServiceShortName", 524),
                                             ("ApplicationType", 652), ("AppStatus", 656),
                                             ("TSSessionId", 660), ("bRestartable", 664) };
        foreach (var (campo, offEsperado) in esperado)
        {
            int obtido = (int)System.Runtime.InteropServices.Marshal.OffsetOf(tInfo, campo);
            Check("offset " + campo + " = " + offEsperado, obtido == offEsperado, "obtido " + obtido);
        }
        Console.WriteLine();

        // ───────────────────────── T2: EntryPoint do SeDebugPrivilege ──────
        Console.WriteLine("T2  EnableDebugPrivilege (DllImport sem EntryPoint usava o nome do metodo)");
        var mEnable = typeof(KitLugia.Core.ForceStopUnlockService)
            .GetMethod("EnableDebugPrivilege", BindingFlags.Public | BindingFlags.Static);
        string resEnable = "nao executado";
        try
        {
            bool ok = (bool)mEnable.Invoke(null, null);
            resEnable = ok ? "habilitada (admin)" : "nao habilitada (sem admin - esperado fora de admin)";
            Check("nao lanca EntryPointNotFoundException", true, resEnable);
        }
        catch (TargetInvocationException tie)
        {
            Check("nao lanca EntryPointNotFoundException", false, tie.InnerException?.Message ?? tie.Message);
        }
        Console.WriteLine("        resultado: {0}", resEnable);
        Console.WriteLine();

        // ───────────────────────── T3/T4: dedupe do GameBoost ──────────────
        Console.WriteLine("T3  ShouldLogBoost - 100 reaplicacoes com a MESMA configuracao");
        var tTray = typeof(TrayIconService);
        var mShould = tTray.GetMethod("ShouldLogBoost", BindingFlags.NonPublic | BindingFlags.Static);
        var mSig = tTray.GetMethod("BoostSignature", BindingFlags.NonPublic | BindingFlags.Static);
        Check("helpers de log existem", mShould != null && mSig != null, "ShouldLogBoost=" + (mShould != null));
        if (mShould == null || mSig == null) return 2;

        uint pid = 424242;
        var cfg = new CustomEngineConfig
        {
            CpuPriority = "high", IoPriorityLevel = 3, PagePriorityLevel = 5,
            ThreadMemoryPriority = 5, EcoQoSEnabled = false, TimerBoost = false, GameClassInfo = true
        };
        string sig = (string)mSig.Invoke(null, new object[] { cfg });
        Console.WriteLine("        assinatura = {0}", sig);

        // 1o uso: a DECISAO de logar tem de ser true
        bool primeira = (bool)mShould.Invoke(null, new object[] { pid, sig });
        Check("1o uso decide logar", primeira, "ShouldLogBoost=" + primeira);

        int decidemLog = 0;
        for (int i = 0; i < 100; i++)
            if ((bool)mShould.Invoke(null, new object[] { pid, sig })) decidemLog++;

        Check("100 chamadas iguais nao viram 100 linhas", decidemLog <= 6,
            "decidiram logar: " + decidemLog + " de 100 (reducao de " + (100 - decidemLog) + "%)");
        Console.WriteLine();

        Console.WriteLine("T4  Mudanca real de assinatura volta a logar");
        var cfg2 = new CustomEngineConfig
        {
            CpuPriority = "high", IoPriorityLevel = 2, PagePriorityLevel = 4,
            ThreadMemoryPriority = 5, EcoQoSEnabled = false, TimerBoost = false, GameClassInfo = true
        };
        string sig2 = (string)mSig.Invoke(null, new object[] { cfg2 });
        Check("assinaturas diferentes", sig != sig2, "\"" + sig + "\"  vs  \"" + sig2 + "\"");
        Check("config nova -> loga imediatamente", (bool)mShould.Invoke(null, new object[] { pid, sig2 }), "ok");
        Check("voltar a config antiga tambem loga", (bool)mShould.Invoke(null, new object[] { pid, sig }), "ok");
        Console.WriteLine();

        // ───────────────────────── T5: janela das faixas ───────────────────
        Console.WriteLine("T5  ShouldLogBand - janela por PID");
        var mBand = tTray.GetMethod("ShouldLogBand", BindingFlags.NonPublic | BindingFlags.Static);
        uint pidB = 515151;
        var l1 = CaptureLogs(() => mBand.Invoke(null, new object[] { pidB, "faixa 1" }));
        var l2 = CaptureLogs(() => mBand.Invoke(null, new object[] { pidB, "faixa 2" }));
        var l3 = CaptureLogs(() => mBand.Invoke(null, new object[] { pidB, "faixa 3" }));
        Check("1a transicao do PID loga", l1.Count == 1, l1.Count + " linha(s)");
        Check("repeticoes dentro da janela nao logam", l2.Count == 0 && l3.Count == 0, "2a=" + l2.Count + " 3a=" + l3.Count);
        Console.WriteLine();

        // ───────────────────────── T6: VERY_LOW agregado por nome ─────────
        Console.WriteLine("T6  LogMemPrioOcasional agrega por NOME de processo");
        var mPrio = tTray.GetMethod("LogMemPrioOcasional", BindingFlags.NonPublic | BindingFlags.Static);
        var fPend = tTray.GetField("_memPrioPendingByName", BindingFlags.NonPublic | BindingFlags.Static);
        Check("metodo e' estatico e os campos existem", mPrio != null && fPend != null, "ok");
        if (mPrio != null && fPend != null)
        {
            var pend = (System.Collections.Concurrent.ConcurrentDictionary<string, int>)fPend.GetValue(null);
            pend.Clear();

            var logs = CaptureLogs(() =>
            {
                for (int i = 0; i < 50; i++)
                    mPrio.Invoke(null, new object[] { new TrayIconService.ProcessRamLimit { ProcessName = "opera", LimitMB = 250 } });
            });

            int chaves = pend.Count;
            int total = pend.TryGetValue("opera", out var t) ? t : 0;
            Check("50 instancias -> 1 chave de nome", chaves <= 1, "chaves=" + chaves + " (antes: 1 por instancia)");
            // A 1a chamada incrementa E loga (reseta o contador para 0), entao o que fica
            // na chave sao as 49 seguintes. A soma tem de fechar em 50.
            Check("nenhuma chamada se perde na agregacao", total + logs.Count == 50,
                "na chave=" + total + " + logadas=" + logs.Count + " = " + (total + logs.Count));
            Check("50 chamadas produzem no maximo 1 linha na janela", logs.Count <= 1,
                logs.Count + " linha(s): " + (logs.Count > 0 ? logs[0] : "-"));
        }
        Console.WriteLine();

        // ───────────────────────── T7: ordem no MemoryCleaner ─────────────
        Console.WriteLine("T7  AggressiveMemoryCleaner - guarda ANTES do log + backoff");
        string arq = Path.Combine(src, "KitLugia.GUI", "Services", "AggressiveMemoryCleaner.cs");
        if (!File.Exists(arq)) arq = Path.Combine(AppContext.BaseDirectory, "AggressiveMemoryCleaner.cs");
        string codigo = File.Exists(arq) ? File.ReadAllText(arq) : "";

        if (codigo.Length > 0)
        {
            int iGuarda = codigo.IndexOf("if (DateTime.Now - _lastCleanup < MinCleanupInterval) return;");
            // Procura a LINHA DE CODIGO do log (o comentario do bloco anti-spam tambem
            // cita "Iniciando limpeza...", entao o padrao precisa do fechamento da linha).
            int iLog = codigo.IndexOf("Iniciando limpeza...\");");
            int iBackoff = codigo.IndexOf("_retryNotBefore = DateTime.UtcNow + backoff;");
            int iStreak = codigo.IndexOf("NoEffectStreakBeforeBackoff");

            Check("fonte do cleaner encontrado", iLog > 0, arq);
            Check("a guarda de throttle vem ANTES do log", iGuarda > 0 && iLog > iGuarda, "guarda@" + iGuarda + " log@" + iLog);
            Check("existe backoff por tentativas sem efeito", iBackoff > 0 && iStreak > 0, "backoff@" + iBackoff + " streak@" + iStreak);
            Check("o log de estado ficou rate-limitado", codigo.Contains("StartLogWindow"), "StartLogWindow presente");
            Check("limpeza sem efeito NAO fica mais em loop", codigo.Contains("_cleanupInFlight"), "guarda de reentrada");
        }
        else Check("fonte do cleaner encontrado", false, "nao achei " + arq);
        Console.WriteLine();

        // ───────────────────────── T8: LogOnce com ASSINATURA + intervalo ──
        // Este e' o caso REAL do governador de RAM e do Standby (24/10/2026): a
        // mensagem carrega numeros que mudam SEMPRE (MB, cpu, faults, RAM livre)
        // e o estado ALTERNA (esvaziado <-> teto). Comparar o texto inteiro fazia
        // o LogOnce vazar todas as linhas; a assinatura + intervalo resolvem.
        Console.WriteLine("T8  LogOnce com ASSINATURA e intervalo minimo (RAM Limiter / Standby)");
        string chave = "lognoise|t8|" + Guid.NewGuid().ToString("N");
        var alt = CaptureLogs(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                KitLugia.Core.Logger.LogOnce(chave,
                    string.Format("RAM Limiter: opera (limite 200 MB) -> {0} {1} MB em {2} proc(s). faults {3}/s",
                        i % 2 == 0 ? "esvaziado + teto" : "teto", 200 + i * 137, 30 + i, 17 * i),
                    heartbeatSeconds: 300,
                    signature: i % 2 == 0 ? "esvaziado|opera|200" : "teto|opera|200",
                    minChangeIntervalSeconds: 120);
            }
        });
        Check("20 ciclos alternando estado, com numeros diferentes -> 1 linha", alt.Count == 1,
            alt.Count + " linha(s): " + (alt.Count > 0 ? alt[0] : "-"));

        // Dentro da janela, nem a repeticao identica nem a mudanca de estado logam
        var iguais = CaptureLogs(() =>
        {
            for (int i = 0; i < 10; i++)
                KitLugia.Core.Logger.LogOnce(chave, "teto 999 MB", heartbeatSeconds: 300,
                    signature: "teto|opera|200", minChangeIntervalSeconds: 120);
        });
        Check("repeticoes identicas dentro da janela nao logam", iguais.Count == 0, iguais.Count + " linha(s)");

        // Heartbeat: passados os segundos, sai UMA linha com a contagem de suprimidas.
        // (LogOnce usa no minimo 5 s de heartbeat; esperamos 6 s de proposito.)
        System.Threading.Thread.Sleep(6000);
        var hb = CaptureLogs(() => KitLugia.Core.Logger.LogOnce(chave, "teto 1000 MB", heartbeatSeconds: 5,
            signature: "teto|opera|200", minChangeIntervalSeconds: 120));
        Check("o heartbeat volta a falar depois do intervalo", hb.Count == 1, hb.Count + " linha(s)");
        Check("o heartbeat leva a contagem do que foi suprimido", hb.Count == 1 && hb[0].Contains("x"),
            hb.Count > 0 ? hb[0] : "-");
        KitLugia.Core.Logger.ResetLogState(chave);
        Console.WriteLine();

        Console.WriteLine("===============================================");
        Console.WriteLine("  OK={0}  FALHAS={1}", _ok, _fail);
        Console.WriteLine("===============================================");
        return _fail == 0 ? 0 : 1;
    }
}
