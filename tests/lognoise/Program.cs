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
//   T3  100 reaplicacoes IGUAIS do GameBoost -> 1 linha (antes: 1 a cada 30)
//   T4  Mudanca real de assinatura -> volta a logar na hora
//   T5  ShouldLogBand: estado por APLICATIVO (nao por PID)
//   T6  VERY_LOW do RAM Limiter agrega por NOME (50 instancias -> 1 chave)
//   T7  MemoryCleaner: a guarda de throttle vem ANTES do log + existe backoff
//   T8  LogOnce com assinatura + intervalo minimo (RAM Limiter / Standby)
//   T9  Politica de log do monitor de RAM e do GameBoost (fonte)
//   T10 CloseHandleNative: EntryPoint resolvido (o log real acusava a cada force stop)
//   T11 100 alt-tabs entre 3 apps -> 11 linhas (sem repetir estado)
//   T12 Scan portatil: cancelamento devolve lista parcial, sem excecao
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
        // Sessao 25/10: o gate `ShouldLogBoost` (1 linha a cada 30 reaplicacoes) foi
        // TROCADO por Logger.LogOnce com a assinatura da configuracao — o motor
        // Automatico reaplica a cada troca de janela e o log real tinha ~140 linhas/dia
        // do MESMO PID. Estes testes reproduzem o contrato do call site.
        Console.WriteLine("T3  Reaplicacao do GameBoost - 100 chamadas com a MESMA configuracao");
        var tTray = typeof(TrayIconService);
        var mSig = tTray.GetMethod("BoostSignature", BindingFlags.NonPublic | BindingFlags.Static);
        var gateAntigo = tTray.GetMethod("ShouldLogBoost", BindingFlags.NonPublic | BindingFlags.Static);
        Check("ShouldLogBoost removido (agora e' Logger.LogOnce)", gateAntigo == null,
            gateAntigo == null ? "removido" : "AINDA EXISTE");
        Check("BoostSignature continua (assinatura do que e' aplicado)", mSig != null, "ok");
        if (mSig == null) return 2;

        uint pid = 424242;
        var cfg = new CustomEngineConfig
        {
            CpuPriority = "high", IoPriorityLevel = 3, PagePriorityLevel = 5,
            ThreadMemoryPriority = 5, EcoQoSEnabled = false, TimerBoost = false, GameClassInfo = true
        };
        string sig = (string)mSig.Invoke(null, new object[] { cfg });
        Console.WriteLine("        assinatura = {0}", sig);

        string chaveBoost = "lognoise|t3|" + pid;
        KitLugia.Core.Logger.ResetLogState(chaveBoost);
        var boostLogs = CaptureLogs(() =>
        {
            for (int i = 0; i < 100; i++)
                KitLugia.Core.Logger.LogOnce(chaveBoost,
                    "⚡ GameBoost: opera (PID: " + pid + ") · CPU=high IO=3 Page=5 Mem=5 EcoQoS=off",
                    heartbeatSeconds: 3600, signature: sig);
        });
        Check("100 reaplicacoes identicas -> 1 linha", boostLogs.Count == 1,
            boostLogs.Count + " linha(s) (antes: ~4 com o gate de 30 reaplicacoes)");
        Console.WriteLine();

        Console.WriteLine("T4  Mudanca real de assinatura volta a logar");
        var cfg2 = new CustomEngineConfig
        {
            CpuPriority = "high", IoPriorityLevel = 2, PagePriorityLevel = 4,
            ThreadMemoryPriority = 5, EcoQoSEnabled = false, TimerBoost = false, GameClassInfo = true
        };
        string sig2 = (string)mSig.Invoke(null, new object[] { cfg2 });
        Check("assinaturas diferentes", sig != sig2, "\"" + sig + "\"  vs  \"" + sig2 + "\"");
        var depoisMudou = CaptureLogs(() => KitLugia.Core.Logger.LogOnce(chaveBoost,
            "⚡ GameBoost: opera (PID: " + pid + ") · CPU=high IO=2 Page=4 Mem=5 EcoQoS=off",
            heartbeatSeconds: 3600, signature: sig2));
        Check("config nova -> loga imediatamente", depoisMudou.Count == 1, depoisMudou.Count + " linha(s)");
        KitLugia.Core.Logger.ResetLogState(chaveBoost);
        Console.WriteLine();

        // ───────────────────────── T5: janela das faixas ───────────────────
        // A chave passa a ser o NOME do aplicativo (sessao 25/10): SearchHost/VoyageSteam
        // reiniciam varias vezes por dia e cada PID novo virava um estado novo.
        Console.WriteLine("T5  ShouldLogBand - estado por APLICATIVO (nao por PID)");
        var mBand = tTray.GetMethod("ShouldLogBand", BindingFlags.NonPublic | BindingFlags.Static);
        Check("ShouldLogBand(app, estado, texto)", mBand != null && mBand.GetParameters().Length == 3,
            mBand == null ? "AUSENTE" : mBand.GetParameters().Length + " parametro(s)");
        if (mBand != null)
        {
            var l1 = CaptureLogs(() => mBand.Invoke(null, new object[] { "lognoiseapp", "sustentado", "🎮 GameBoost: lognoiseapp saiu do foco mas continua ativo -> faixa SUSTENTADA (AboveNormal)." }));
            var l2 = CaptureLogs(() => mBand.Invoke(null, new object[] { "LOGNOISEAPP", "ocioso", "🎮 GameBoost: lognoiseapp ficou ocioso — prioridade original restaurada." }));
            var l3 = CaptureLogs(() => mBand.Invoke(null, new object[] { "outroapplognoise", "sustentado", "🎮 GameBoost: outroapplognoise saiu do foco mas continua ativo -> faixa SUSTENTADA (AboveNormal)." }));
            Check("1a transicao do app loga", l1.Count == 1, l1.Count + " linha(s)");
            Check("mesmo app (outra caixa) e outro estado -> NAO loga", l2.Count == 0, l2.Count + " linha(s)");
            Check("outro app -> loga", l3.Count == 1, l3.Count + " linha(s)");
        }
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
        // ───────────────────────── T9: politica de log (fonte) ────────────
        // Guarda de regressao: se alguem voltar a usar Logger.Log() cru nos pontos que
        // inundavam o log real, o harness acusa. Os numeros sao a politica combinada.
        Console.WriteLine("T9  Politica de log do monitor de RAM e do GameBoost (fonte)");
        string traySrc = Path.Combine(src, "KitLugia.GUI", "Services", "TrayIconService.cs");
        string codigoTray = File.Exists(traySrc) ? File.ReadAllText(traySrc) : "";
        if (codigoTray.Length > 0)
        {
            Check("Standby: skip fala no maximo 1x/hora",
                codigoTray.Contains("islc|skip|ramLivre") && codigoTray.Contains("heartbeatSeconds: 3600"),
                "heartbeat 3600 presente");
            Check("Standby: a mudanca tambem e' limitada a 1x/hora",
                codigoTray.Contains("signature: \"ram livre acima do limite\", minChangeIntervalSeconds: 3600"),
                "minChange 3600 presente");
            Check("Governador de RAM: heartbeat 30 min + mudanca 15 min",
                codigoTray.Contains("heartbeatSeconds: 1800") && codigoTray.Contains("minChangeIntervalSeconds: 900"),
                "1800/900 presente");
            Check("Corte DIRETO: janela de 30 min",
                codigoTray.Contains("TrimLogWindow = TimeSpan.FromMinutes(30)"), "TrimLogWindow 30 min");
            Check("VERY_LOW: janela de 60 min",
                codigoTray.Contains("MemPrioLogWindow = TimeSpan.FromMinutes(60)"), "MemPrioLogWindow 60 min");
            Check("GameBoost por PID usa LogOnce (chave gboost|apply|)",
                codigoTray.Contains("gboost|apply|{pid}") && codigoTray.Contains("signature: BoostSignature(config)"),
                "chave + assinatura presentes");
            Check("Timer Resolution e rede global sao LogOnce",
                codigoTray.Contains("gboost|timerres") && codigoTray.Contains("gboost|netglobal"),
                "2 chaves de estado global");
            Check("GameBoost AUTO e ProBalance folgado sao LogOnce",
                codigoTray.Contains("gboost|auto|{pid}") && codigoTray.Contains("gboost|autopb|folgada"),
                "2 chaves presentes");
            Check("Storm e piso>teto sao LogOnce",
                codigoTray.Contains("ramlimit|storm|") && codigoTray.Contains("ramlimit|piso|"),
                "2 chaves presentes");
        }
        else Check("fonte do TrayIconService encontrado", false, "nao achei " + traySrc);
        Console.WriteLine();

        // ───────────────────────── T10: EntryPoint do CloseHandleNative ──
        Console.WriteLine("T10 CloseHandleNative (o log real acusava a cada force stop)");
        var mClose = typeof(KitLugia.Core.ForceStopUnlockService)
            .GetMethod("CloseHandleNative", BindingFlags.NonPublic | BindingFlags.Static);
        Check("metodo existe", mClose != null, mClose == null ? "AUSENTE" : "ok");
        if (mClose != null)
        {
            var attr = mClose.GetCustomAttributes(typeof(System.Runtime.InteropServices.DllImportAttribute), false)
                .OfType<System.Runtime.InteropServices.DllImportAttribute>().FirstOrDefault();
            Check("EntryPoint aponta para CloseHandle", attr != null && attr.EntryPoint == "CloseHandle",
                attr == null ? "sem DllImport" : "EntryPoint=" + (attr.EntryPoint ?? "(nome do metodo)"));
            string res = "nao executado";
            try
            {
                // CloseHandle(NULL) devolve false e NAO fecha nada — o objetivo aqui e'
                // so provar que o CLR resolve o EntryPoint (antes: EntryPointNotFoundException).
                mClose.Invoke(null, new object[] { IntPtr.Zero });
                res = "resolvido (sem excecao)";
                Check("nao lanca EntryPointNotFoundException", true, res);
            }
            catch (TargetInvocationException tie)
            {
                res = tie.InnerException?.Message ?? tie.Message;
                Check("nao lanca EntryPointNotFoundException", false, res);
            }
        }
        Console.WriteLine();

        // ───────────────────────── T11: alt-tab nao infla o log ────────────
        // Sessao 25/10: o usuario pode ficar trocando de app o dia inteiro. A promessa
        // e' "uma linha ou nenhuma": as chaves sao fixas (por app/PID + estado) e o
        // intervalo minimo impede que o flip-flop foco<->sustentado vire log novo.
        Console.WriteLine("T11 Simulacao de alt-tab (100 trocas entre 3 apps)");
        string[] apps = { "kl11alpha", "kl11beta", "kl11gamma" };
        uint[] pids = { 910001, 910002, 910003 };
        foreach (var a in apps) KitLugia.Core.Logger.ResetLogState("gboost|band|" + a);
        foreach (var p in pids)
        {
            KitLugia.Core.Logger.ResetLogState("gboost|apply|" + p);
            KitLugia.Core.Logger.ResetLogState("gboost|auto|" + p);
        }
        KitLugia.Core.Logger.ResetLogState("gboost|netglobal");
        KitLugia.Core.Logger.ResetLogState("gboost|timerres");

        var altTab = CaptureLogs(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                int k = i % 3;
                // 1) boost do app que ganhou o foco (gboost|apply|{pid})
                KitLugia.Core.Logger.LogOnce("gboost|apply|" + pids[k],
                    "⚡ GameBoost: " + apps[k] + " (PID: " + pids[k] + ") · CPU=high IO=3 Page=5 Mem=5 EcoQoS=off",
                    heartbeatSeconds: 3600, signature: "high|3|5|5|False|False|True");
                // 2) app perdeu o foco -> faixa SUSTENTADA (ShouldLogBand real)
                if (mBand != null)
                    mBand.Invoke(null, new object[] { apps[k], "sustentado",
                        "🎮 GameBoost: " + apps[k] + " saiu do foco mas continua ativo -> faixa SUSTENTADA (AboveNormal)." });
                // 3) motor Automatico (gboost|auto|{pid})
                KitLugia.Core.Logger.LogOnce("gboost|auto|" + pids[k],
                    "🤖 GameBoost AUTO: fg=True cpu=" + i + "% amostra=True faixa=Loading -> IO=3 Page=5",
                    heartbeatSeconds: 3600, signature: "1|3|5", minChangeIntervalSeconds: 1800);
                // 4) tweaks GLOBAIS, que alternam a cada troca de foco
                bool entra = i % 2 == 0;
                KitLugia.Core.Logger.LogOnce("gboost|netglobal",
                    entra ? "🌐 GameBoost V3: Network priorizado (throttling reduzido) — valor original guardado para reversão."
                          : "🌐 GameBoost: tweak global de rede revertido ao valor original do Windows.",
                    heartbeatSeconds: 3600, signature: entra ? "aplicado" : "revertido", minChangeIntervalSeconds: 3600);
                KitLugia.Core.Logger.LogOnce("gboost|timerres",
                    entra ? "⏱️ Timer Resolution: 15,62ms → 1,00ms (boosted)" : "⏱️ Timer Resolution restaurado: 15,62ms",
                    heartbeatSeconds: 3600, signature: entra ? "aplicado" : "revertido", minChangeIntervalSeconds: 3600);
            }
        });
        Console.WriteLine("        linhas geradas: {0}", altTab.Count);
        foreach (var l in altTab) Console.WriteLine("          · " + l);
        Check("100 alt-tabs -> no maximo 11 linhas (1 por app/evento + 2 globais)",
            altTab.Count <= 11, altTab.Count + " linha(s) para 100 trocas");
        Check("nenhum app repetiu o mesmo estado",
            apps.All(a => altTab.Count(l => l.Contains(a)) <= 3),
            string.Join(" ", apps.Select(a => a + "=" + altTab.Count(l => l.Contains(a)))));
        Check("tweak global de rede falou 1x",
            altTab.Count(l => l.Contains("Network priorizado") || l.Contains("tweak global de rede")) == 1,
            altTab.Count(l => l.Contains("Network priorizado") || l.Contains("tweak global de rede")) + " linha(s)");
        Check("Timer Resolution falou 1x",
            altTab.Count(l => l.Contains("Timer Resolution")) == 1,
            altTab.Count(l => l.Contains("Timer Resolution")) + " linha(s)");
        foreach (var a in apps) KitLugia.Core.Logger.ResetLogState("gboost|band|" + a);
        foreach (var p in pids)
        {
            KitLugia.Core.Logger.ResetLogState("gboost|apply|" + p);
            KitLugia.Core.Logger.ResetLogState("gboost|auto|" + p);
        }
        KitLugia.Core.Logger.ResetLogState("gboost|netglobal");
        KitLugia.Core.Logger.ResetLogState("gboost|timerres");
        Console.WriteLine();

        // ───────────────────────── T12: cancelamento do scan portatil ──────
        // O usuario colou (25/10) um stack de OperationCanceledException saindo de
        // PortableAppScanner.Scan linha 210 e AppsPage.LoadPortableApps linha 1322:
        // e' o cancelamento do scan de ~20 s (Atualizar/navegar) escapando como ERRO.
        Console.WriteLine("T12 Scan de apps portateis: cancelamento nao vira excecao");
        var arqPort = Path.Combine(src, "KitLugia.Core", "PortableAppScanner.cs");
        string codigoPort = File.Exists(arqPort) ? File.ReadAllText(arqPort) : "";
        if (codigoPort.Length > 0)
        {
            int iForeach = codigoPort.IndexOf("Parallel.ForEach(candidates", StringComparison.Ordinal);
            int iCatch = codigoPort.IndexOf("catch (OperationCanceledException)", iForeach, StringComparison.Ordinal);
            Check("o cancelamento do Parallel.ForEach e' capturado", iForeach > 0 && iCatch > iForeach,
                "ForEach@" + iForeach + " catch@" + iCatch);
            Check("guarda antes do scan de MFT (20 s)",
                codigoPort.Contains("if (ct.IsCancellationRequested) return new List<PortableAppEntry>();"),
                "early return presente");
        }
        else Check("fonte do PortableAppScanner encontrado", false, arqPort);

        // Comportamento: token JA cancelado -> devolve lista vazia, sem lancar nada.
        string resScan = "nao executado";
        int qtdScan = -1;
        var swScan = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var ctsScan = new System.Threading.CancellationTokenSource();
            ctsScan.Cancel();
            var r = KitLugia.Core.PortableAppScanner.Scan(null, ctsScan.Token);
            qtdScan = r == null ? -1 : r.Count;
            resScan = "sem excecao";
        }
        catch (Exception ex)
        {
            resScan = ex.GetType().Name + ": " + ex.Message;
        }
        swScan.Stop();
        Check("token cancelado -> nenhuma excecao", resScan == "sem excecao", resScan);
        Check("token cancelado -> lista vazia", qtdScan == 0, "app(s)=" + qtdScan);
        Check("retorna rapido (nao faz o MFT de 20 s)", swScan.Elapsed.TotalSeconds < 5,
            $"{swScan.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine();

        Console.WriteLine("  OK={0}  FALHAS={1}", _ok, _fail);
        Console.WriteLine("===============================================");
        return _fail == 0 ? 0 : 1;
    }
}
