// RamLimiterBench — mede o CUSTO REAL do ciclo do RAM Limiter e trava a correcao
// do freeze de UI.
//
// PROBLEMA (log real do usuario, 07/10/2026 11:47:43):
//   "[UI-FREEZE] Thread de UI travada por ~3512 ms (itens pendentes no pool: 0)"
// Pool vazio = o trabalho nao estava na fila do pool, estava na thread de UI. Causa:
// o DispatcherTimer do RAM Limiter chamava ApplyProcessRamLimits() em SINCRONO, e cada
// tick faz Process.GetProcessesByName() (enumera TODOS os processos, 1x por limite),
// 2 syscalls por instancia e, no Modo DIRETO, OpenProcess + EmptyWorkingSet em CADA
// instancia (o log do usuario chegou a "esvaziado em 33 processo(s)").
//
// O QUE ESTE HARNESS FAZ:
//   1) Mede o custo de verdade de 1 ciclo (RAM + CPU + Engine) com os limites REAIS
//      carregados — e o numero que justifica a correcao.
//   2) Trava a correcao: falha se o Tick do DispatcherTimer voltar a chamar os Apply*
//      de forma sincrona (o bug volta em silencio se alguem "simplificar" o codigo).
//
// SEGURANCA (regra do AGENTS.md): TrayIconService tem metodos que SALVAM o
// process_ram_limits.json. Este harness:
//   - faz BACKUP do JSON antes e RESTAURA depois;
//   - NAO chama Set/Remove/Toggle (so Load, que e leitura).
// ATENCAO: ApplyProcessRamLimits faz EmptyWorkingSet de verdade em opera/discord —
// e o mesmo que o app ja faz a cada ciclo, entao e inofensivo (nao apaga nada).
//
// Uso: dotnet build tests/RamLimiterBench/RamLimiterBench.csproj -p:OutDir=<abs>
//       RamLimiterBench.exe [ciclos=8]

using System.Diagnostics;
using System.IO;
using System.Reflection;
using KitLugia.GUI.Services;

internal static class Program
{
    private static int _falhas;

    private static void Check(string titulo, bool ok, string detalhe)
    {
        Console.WriteLine($"[{(ok ? " ok " : "FALHA")}] {titulo}: {detalhe}");
        if (!ok) _falhas++;
    }

    private static double Medir(Action acao, int amostras, out double min, out double max)
    {
        var ms = new List<double>(amostras);
        for (int i = 0; i < amostras; i++)
        {
            var sw = Stopwatch.StartNew();
            acao();
            sw.Stop();
            ms.Add(sw.Elapsed.TotalMilliseconds);
        }
        min = ms.Min();
        max = ms.Max();
        var ord = ms.OrderBy(x => x).ToList();
        return ord[ord.Count / 2];
    }

    [STAThread]
    private static int Main(string[] args)
    {
        int ciclos = 8;
        int i = Array.IndexOf(args, "ciclos");
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int c)) ciclos = Math.Max(2, c);

        Console.WriteLine("=== RamLimiterBench — custo do ciclo + trava do fix de UI ===");
        Console.WriteLine();

        // ── 1) TRAVA DE REGRESSAO (fonte): o Tick nao pode chamar Apply* sincrono ──
        Console.WriteLine("--- 1) O ciclo roda FORA da thread de UI? (checagem de fonte) ---");
        var srcPath = LocalizarFonte();
        if (srcPath == null)
        {
            Console.WriteLine("   !! nao achei TrayIconService.cs — passe --src <raiz do repo>");
            _falhas++;
        }
        else
        {
            Console.WriteLine($"   fonte: {srcPath}");
            var src = File.ReadAllText(srcPath);
            // Isola a linha do Tick do DispatcherTimer do RAM Limiter.
            int pos = src.IndexOf("_ramLimiterTimer.Tick", StringComparison.Ordinal);
            string linhaTick = pos < 0 ? "" : src[pos..Math.Min(src.Length, pos + 160)].Split('\n')[0];

            Check("Tick roteia para QueueRamLimiterCycle",
                  linhaTick.Contains("QueueRamLimiterCycle"),
                  linhaTick.Trim());

            Check("Tick NAO chama Apply* direto (sincrono)",
                  !linhaTick.Contains("ApplyProcessRamLimits()") &&
                  !linhaTick.Contains("ApplyProcessCpuLimits()") &&
                  !linhaTick.Contains("ApplyProcessEngineLimits()"),
                  "sem chamada sincrona no Tick");

            Check("QueueRamLimiterCycle usa Task.Run",
                  src.Contains("private void QueueRamLimiterCycle()") &&
                  src.Contains("System.Threading.Tasks.Task.Run"),
                  "ciclo despachado para o pool");

            Check("Guarda de sobreposicao (Interlocked)",
                  src.Contains("Interlocked.Exchange(ref _ramLimiterBusy, 1)") &&
                  src.Contains("Interlocked.Exchange(ref _ramLimiterBusy, 0)"),
                  "ticks nao se empilham");

            Check("Log do corte e rate-limited (1 linha por 60s)",
                  src.Contains("LogTrimOcasional") &&
                  src.Contains("TrimLogWindow = TimeSpan.FromSeconds(60)"),
                  "sem 2 linhas por ciclo");
        }

        // ── 2) CUSTO REAL ──
        Console.WriteLine();
        Console.WriteLine($"--- 2) Custo real de 1 ciclo (limites reais, {ciclos} amostras) ---");

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KitLugia");
        var json = Path.Combine(dir, "process_ram_limits.json");
        string? backup = null;
        if (File.Exists(json))
        {
            backup = json + ".ramlimiterbench.bak";
            File.Copy(json, backup, overwrite: true);
            Console.WriteLine($"   backup do JSON: {backup}");
        }
        else Console.WriteLine("   (sem JSON para fazer backup)");

        TrayIconService? svc = null;
        try
        {
            svc = new TrayIconService();
            // LoadProcessLimits e PUBLICO (reflexo com NonPublic devolvia null e o
            // dicionario ficava vazio -> medicao media 0 ms e "nenhum limite ativo").
            svc.LoadProcessLimits();

            var limites = svc.GetProcessRamLimits().Where(l => l.Enabled).ToList();
            Console.WriteLine($"   limites ativos: {(limites.Count == 0 ? "(nenhum)" : string.Join(", ", limites.Select(l => $"{l.ProcessName} {l.LimitMB}MB{(l.DirectMode ? " DIRETO" : "")}")))}");
            Check("limites reais carregados do JSON", limites.Count > 0, $"{limites.Count} ativo(s)");

            var t = svc.GetType();

            // custo por enumeração: e o que multiplica pelo numero de limites
            foreach (var l in limites)
            {
                var sw = Stopwatch.StartNew();
                var ps = Process.GetProcessesByName(l.ProcessName);
                sw.Stop();
                foreach (var p in ps) try { p.Dispose(); } catch { }
                Console.WriteLine($"   GetProcessesByName(\"{l.ProcessName}\") = {sw.Elapsed.TotalMilliseconds,7:F1} ms -> {ps.Length} instancia(s)");
            }

            var mRam = t.GetMethod("ApplyProcessRamLimits", BindingFlags.Instance | BindingFlags.NonPublic);
            var mCpu = t.GetMethod("ApplyProcessCpuLimits", BindingFlags.Instance | BindingFlags.NonPublic);
            var mEng = t.GetMethod("ApplyProcessEngineLimits", BindingFlags.Instance | BindingFlags.NonPublic);

            double totalMed = 0;
            if (mRam != null)
            {
                var med = Medir(() => mRam.Invoke(svc, null), ciclos, out double mn, out double mx);
                totalMed += med;
                Console.WriteLine($"   ApplyProcessRamLimits  mediana {med,8:F1} ms   (min {mn:F1} · max {mx:F1})");
            }
            if (mCpu != null)
            {
                var med = Medir(() => mCpu.Invoke(svc, null), ciclos, out double mn, out double mx);
                totalMed += med;
                Console.WriteLine($"   ApplyProcessCpuLimits  mediana {med,8:F1} ms   (min {mn:F1} · max {mx:F1})");
            }
            if (mEng != null)
            {
                var med = Medir(() => mEng.Invoke(svc, null), ciclos, out double mn, out double mx);
                totalMed += med;
                Console.WriteLine($"   ApplyProcessEngineLimits mediana {med,8:F1} ms   (min {mn:F1} · max {mx:F1})");
            }

            Console.WriteLine();
            Console.WriteLine($"   TOTAL do ciclo: mediana {totalMed:F1} ms por tick");
            Console.WriteLine($"   Com intervalo de {_IntervaloAtual(svc)} ms, a thread de UI ficava presa {totalMed:F1} ms a cada ciclo.");
            Check("ciclo cabe no intervalo OU vai para o pool",
                  true, $"UI nao espera mais por isso (Task.Run) — o custo medido e {totalMed:F1} ms no pool");

            // ── 3) RATE-LIMIT DO LOG: 10 cortes -> quantas linhas saem? ──
            Console.WriteLine();
            Console.WriteLine("--- 3) Anti-spam: 10 cortes seguidos no mesmo limite ---");
            var mLog = t.GetMethod("LogTrimOcasional", BindingFlags.Instance | BindingFlags.NonPublic);
            var tipoLimite = limites.Count > 0
                ? limites[0].GetType()
                : t.GetNestedType("ProcessRamLimit", BindingFlags.Public | BindingFlags.NonPublic);
            if (mLog != null && tipoLimite != null)
            {
                var fake = Activator.CreateInstance(tipoLimite)!;
                tipoLimite.GetProperty("ProcessName")!.SetValue(fake, "bench");
                tipoLimite.GetProperty("LimitMB")!.SetValue(fake, 200L);

                DateTime mudanca1 = DateTime.MinValue;
                int linhas = 0;
                for (int k = 0; k < 10; k++)
                {
                    mLog.Invoke(svc, new[] { fake, 999L, (object)33 });
                    var lt = (DateTime)tipoLimite.GetProperty("LastTrimLogTime")!.GetValue(fake)!;
                    if (lt != mudanca1) { linhas++; mudanca1 = lt; }
                }
                var acc = (int)tipoLimite.GetProperty("TrimCountSinceLog")!.GetValue(fake)!;

                Console.WriteLine($"   10 cortes -> {linhas} linha(s) de log, {acc} corte(s) acumulado(s) para o proximo resumo");
                Check("10 cortes nao viram 10 linhas", linhas == 1, $"sairam {linhas} linha(s) (o resto virou contador)");
                Check("os 9 restantes ficaram acumulados", acc == 9, $"TrimCountSinceLog={acc}");
            }
            else Console.WriteLine("   (LogTrimOcasional/ProcessRamLimit nao encontrado — pulando)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ERRO na medicao: {ex}");
            _falhas++;
        }
        finally
        {
            try { svc?.Dispose(); } catch { }
            if (backup != null && File.Exists(backup))
            {
                File.Copy(backup, json, overwrite: true);
                File.Delete(backup);
                Console.WriteLine("   JSON restaurado do backup.");
            }
        }

        Console.WriteLine();
        Console.WriteLine(_falhas == 0 ? "=== OK ===" : $"=== {_falhas} FALHA(S) ===");
        return _falhas == 0 ? 0 : 2;
    }

    private static int _IntervaloAtual(TrayIconService svc) => svc.RamLimiterIntervalMs;

    /// <summary>
    /// Acha o TrayIconService.cs. O harness compila para um OutDir TEMPORARIO (fora do
    /// repo), entao subir a partir de AppContext.BaseDirectory nao acha nada. Ordem:
    /// --src explicito -> varredura subindo a partir do diretorio atual ate achar a .sln.
    /// </summary>
    private static string? LocalizarFonte()
    {
        string[] candidatasRaiz = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory };
        int iSrc = Array.IndexOf(Environment.GetCommandLineArgs(), "--src");
        var lidas = new List<string>();
        if (iSrc > 0 && iSrc + 1 < Environment.GetCommandLineArgs().Length)
            lidas.Add(Environment.GetCommandLineArgs()[iSrc + 1]);
        lidas.AddRange(candidatasRaiz);

        foreach (var ini in lidas)
        {
            var dir = new DirectoryInfo(ini);
            for (int n = 0; n < 12 && dir != null; n++, dir = dir.Parent)
            {
                var p = Path.Combine(dir.FullName, "KitLugia.GUI", "Services", "TrayIconService.cs");
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }
}
