// Mede o custo REAL de cada página do KitLugia:
//   - frio/quente: construção (UI thread: é o que o usuário paga no clique da sidebar);
//   - layout: Measure/Arrange da 1ª renderização;
//   - assentar (modo --loaded): tempo até o trabalho do Loaded terminar — é o "travamento";
//   - retido: RAM gerenciada enquanto a página está VIVA;
//   - sobra2/sobra3: o que fica DEPOIS do Cleanup() em corridas seguidas.
//     sobra3 ≈ sobra2 > 512 KB => vazamento de verdade (a página se segura via algo estático);
//     sobra2 > 512 KB mas sobra3 ≈ 0 => só uma cache estática de primeira visita.
//
// Cada página é construída e descartada SEM Show() no modo padrão: nenhum Loaded dispara,
// nenhum timer de refresh começa. Nada aqui altera o sistema.
//
// Uso: dotnet run --project tests/PagesBench [-- loaded] [-- leak]

using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

internal static class Program
{
    private sealed record Medida(string Nome, double Ms1, double Ms2, double LayoutMs, double SettleMs,
        long RetidoKb, int Nos, long Sobra2Kb, long Sobra3Kb, string Erro)
    {
        public bool Vazando => Sobra2Kb > 512 && Sobra3Kb > 512;
    }

    private static bool _comLoaded;
    private static int _execucoes = 3;
    // Teto do "assentar": se a página nunca chega a ApplicationIdle, esperamos isto e
    // marcamos como - (ex.: timer recorrente que re-enfileira trabalho o tempo todo).
    private static int _capSettleMs = 4000;
    private static readonly Dictionary<string, int> ErrosRaiz = new();

    [STAThread]
    private static int Main(string[] args)
    {
        _comLoaded = args.Contains("loaded");
        bool soLeak = args.Contains("leak");

        if (args.Contains("selftest")) return AutoTeste();
        if (args.Contains("ctx")) return TesteContexto();
        if (args.Contains("ctx2")) return TesteContextoReal();

        int iCap = Array.IndexOf(args, "--cap");
        if (iCap >= 0 && iCap + 1 < args.Length && int.TryParse(args[iCap + 1], out int cap))
            _capSettleMs = cap;

        int iRoot = Array.IndexOf(args, "--root");
        if (iRoot >= 0 && iRoot + 1 < args.Length)
        {
            _execucoes = args.Contains("--once") ? 1 : 3;
            int esperaS = 0;
            int iWait = Array.IndexOf(args, "--wait");
            if (iWait >= 0 && iWait + 1 < args.Length) int.TryParse(args[iWait + 1], out esperaS);
            return Raiz(args[iRoot + 1], esperaS);
        }
        Console.WriteLine($"=== KitLugia — custo de construção, layout e retenção por página ===");
        Console.WriteLine(_comLoaded
            ? "modo: com Loaded (mede o tempo até a página assentar — leitura/read-only)"
            : "modo: só construção (nenhum Loaded dispara)");
        Console.WriteLine();

        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/KitLugia.GUI;component/Themes/Generic.xaml")
            });

            // Blindagem: o modo --loaded levanta o Loaded de páginas que disparam trabalho
            // assíncrono. Em produção o DispatcherSynchronizationContext devolve as continuações
            // para a UI thread; aqui instalamos o mesmo contexto abaixo. O que ainda estourar
            // (fire-and-forget sem contexto) é logado e absorvido em vez de derrubar o processo.
            app.DispatcherUnhandledException += (s, e) =>
            {
                Console.WriteLine($"   [exceção na UI absorvida] {e.Exception.GetType().Name}: {e.Exception.Message}");
                e.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (s, e) => e.SetObserved();

            // Em produção (Application.Run) a UI thread tem DispatcherSynchronizationContext;
            // sem ele aqui, todo `await` das páginas continuaria na ThreadPool e estouraria
            // "O thread de chamada não pode acessar este objeto" (o diálogo de Erro ao carregar
            // programas que aparecia a cada execução do bench).
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AVISO: recursos do app não carregaram ({ex.Message}) — resultados parciais.");
        }

        long wsInicial = Environment.WorkingSet;

        var tipos = typeof(KitLugia.GUI.App).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(Page).IsAssignableFrom(t)
                        && t.Namespace != null && t.Namespace.StartsWith("KitLugia.GUI"))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        Console.WriteLine($"páginas encontradas: {tipos.Count}");
        Console.WriteLine();

        var medidas = new List<Medida>(tipos.Count);
        int indice = 0;
        foreach (var tipo in tipos)
        {
            var m = MeDir(tipo);
            if (m == null) continue;
            medidas.Add(m);
            // Progresso incremental: um timeout no meio da corrida não perde tudo.
            indice++;
            string assenta = m.SettleMs < 0 ? "-" : m.SettleMs.ToString("F0");
            Console.WriteLine($"[{indice}/{tipos.Count}] {m.Nome,-38} frio={m.Ms1,6:F1}  layout={m.LayoutMs,6:F1}  " +
                              $"assenta={assenta,5}  retido={m.RetidoKb,6:N0} KB  sobra3={m.Sobra3Kb,6:N0} KB" +
                              (m.Erro.Length > 0 ? $"  !! {m.Erro}" : ""));
        }
        var ordenadas = medidas.OrderByDescending(m => m.Ms1).ToList();

        Console.WriteLine($"{"página",-40} {"frio",7} {"quente",7} {"layout",8} {"assenta",8} {"retidoKB",9} {"nós",6} {"sobra3",8}");
        Console.WriteLine(new string('-', 100));
        foreach (var m in ordenadas)
        {
            if (soLeak && !m.Vazando) continue;
            string nome = m.Nome.Length > 39 ? m.Nome[..39] : m.Nome;
            string assenta = m.SettleMs < 0 ? "-" : m.SettleMs.ToString("F0");
            string sobra = m.Vazando ? $"{m.Sobra3Kb}" : m.Sobra3Kb.ToString();
            Console.WriteLine($"{nome,-40} {m.Ms1,7:F1} {m.Ms2,7:F1} {m.LayoutMs,8:F1} {assenta,8} {m.RetidoKb,9:N0} {m.Nos,6:N0} {sobra,8}");
            if (m.Erro.Length > 0) Console.WriteLine($"      ^ {m.Erro}");
        }

        Console.WriteLine(new string('-', 100));
        var ok = medidas.Where(m => m.Erro.Length == 0).ToList();
        Console.WriteLine($"construção frio total (todas de uma vez): {ok.Sum(m => m.Ms1):F0} ms na UI thread");
        Console.WriteLine($"construção quente média:                 {ok.Average(m => m.Ms2):F1} ms/página");
        Console.WriteLine($"layout médio:                            {ok.Where(m => m.LayoutMs > 0).Average(m => m.LayoutMs):F1} ms/página");
        if (_comLoaded)
            Console.WriteLine($"assentar (Loaded) pior caso:             {ok.Where(m => m.SettleMs >= 0).Max(m => m.SettleMs):F0} ms");
        Console.WriteLine($"soma do retido por página:               {ok.Sum(m => m.RetidoKb) / 1024.0:F1} MB");
        Console.WriteLine($"working set: {wsInicial / (1024 * 1024)} MB -> {Environment.WorkingSet / (1024 * 1024)} MB");

        var vazando = ok.Where(m => m.Vazando).ToList();
        Console.WriteLine();
        Console.WriteLine(vazando.Count == 0
            ? "Nenhuma página reteve >512 KB depois do Cleanup() em 2 corridas seguidas."
            : $"!! {vazando.Count} página(s) vazando (sobra2 e sobra3 >512 KB): {string.Join(", ", vazando.Select(v => v.Nome))}");

        return 0;
    }

    /// <summary>Isola se o próprio harness está retendo objetos: se caso 1/2/3 saírem
    /// alive=False mas 4/5 saírem True, a retenção vem do WPF; se TODOS saírem True,
    /// o bug é do harness e nenhum resultado de vazamento é confiável.</summary>
    private static int AutoTeste()
    {
        Console.WriteLine("=== autoteste: o harness segura objetos? ===");

        var o1 = new object();
        var w1 = new WeakReference(o1);
        o1 = null;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Console.WriteLine($"1) object + GC ....................... alive={w1.IsAlive}  (esperado False)");

        // 1b) mesmo objeto, mas criado num MÉTODO AUXILIAR (nenhuma local do chamador o segura)
        var w1b = CriarEDescartar();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Console.WriteLine($"1b) criado em método auxiliar ........ alive={w1b.IsAlive}  (esperado False)");

        // 1c) o GC está funcionando? aloca 20 MB, solta, mede
        long pico = MedirAlocacao20Mb();
        Console.WriteLine($"1c) GC devolve 20 MB ................. {pico / (1024 * 1024)} MB antes/depois (esperado ~0 depois)");

        var o2 = new object();
        var w2 = new WeakReference(o2);
        BombeiaDispatcher(400);
        o2 = null;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Console.WriteLine($"2) object + bombeio do Dispatcher .... alive={w2.IsAlive}  (esperado False)");

        WeakReference? w3 = null;
        for (int i = 0; i < 1; i++)
        {
            object? p = new object();
            BombeiaDispatcher(400);
            w3 = new WeakReference(p);
            p = null;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        Console.WriteLine($"3) estrutura igual ao Raiz ........... alive={w3?.IsAlive}  (esperado False)");

        var o4 = new Page();
        var w4 = new WeakReference(o4);
        o4 = null;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Console.WriteLine($"4) new Page() sem XAML + GC .......... alive={w4.IsAlive}");

        var o5 = new Page();
        var w5 = new WeakReference(o5);
        BombeiaDispatcher(1500);
        o5 = null;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Console.WriteLine($"5) new Page() + bombeio 1,5s ......... alive={w5.IsAlive}");

        Console.WriteLine();
        Console.WriteLine(w1.IsAlive || w2.IsAlive || w3?.IsAlive == true
            ? "=> BUG DO HARNESS: nem object() puro é coletado; resultados de vazamento NÃO são confiáveis."
            : "=> harness OK para object(). Olhar casos 4/5 para o efeito do WPF.");
        return 0;
    }

    private static WeakReference CriarEDescartar()
    {
        var o = new object();
        return new WeakReference(o);
    }

    private static long MedirAlocacao20Mb()
    {
        long antes = GC.GetTotalMemory(true);
        var buf = new byte[20 * 1024 * 1024];
        buf[0] = 1;
        GC.KeepAlive(buf);
        buf = null;
        long depois = GC.GetTotalMemory(true);
        Console.WriteLine($"      (alocacao: {antes / (1024 * 1024)} MB -> {depois / (1024 * 1024)} MB)");
        return Math.Max(0, depois - antes);
    }

    /// <summary>Descobre se a UI thread do WPF tem SynchronizationContext durante o loop de
    /// mensagens. Se NÃO tiver, todo `await` continua na ThreadPool e o app inteiro estoura
    /// "O thread de chamada não pode acessar este objeto" — que é exatamente o erro que o
    /// usuário vê ao carregar programas.</summary>
    private static int TesteContexto()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Console.WriteLine($"fora do loop ................. {Nome(SynchronizationContext.Current)}");

        Dispatcher.CurrentDispatcher.Invoke(() =>
            Console.WriteLine($"dentro de Dispatcher.Invoke .. {Nome(SynchronizationContext.Current)}"));

        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            Console.WriteLine($"na fila do dispatcher ........ {Nome(SynchronizationContext.Current)}");
        }));

        // Simula Application.Run: bombeia o dispatcher como o loop real do WPF
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => Dispatcher.CurrentDispatcher.InvokeShutdown()));
        Dispatcher.PushFrame(new DispatcherFrame());

        Console.WriteLine($"fora do loop (depois) ........ {Nome(SynchronizationContext.Current)}");
        return 0;
    }

    /// <summary>Com Application.Run() de verdade (como o kit roda): o contexto existe dentro
    /// dos eventos normais (Loaded, clique, SelectionChanged)? É o que decide se o bug de
    /// thread é real em produção ou só do harness.</summary>
    private static int TesteContextoReal()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var janela = new Window { Width = 200, Height = 100, Content = new Grid(), ShowInTaskbar = false };

        janela.Loaded += (s, e) =>
        {
            Console.WriteLine($"Loaded da janela ......... {Nome(SynchronizationContext.Current)}");
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                Console.WriteLine($"pos-Loaded (na fila) ..... {Nome(SynchronizationContext.Current)}");
                app.Shutdown();
            }));
        };

        janela.Show();
        app.Run();
        return 0;
    }

    private static string Nome(SynchronizationContext? ctx) =>
        ctx == null ? "NULL  => await continua na ThreadPool (BUG em produção)"
                    : ctx.GetType().Name + "  => await volta para a UI thread (OK)";

    private static Medida? MeDir(Type tipo)
    {
        var ctor = EscolherCtor(tipo);
        if (ctor == null)
            return new Medida(tipo.Name, 0, 0, 0, -1, 0, 0, 0, 0, "sem construtor utilizável");

        try
        {
            var r1 = Rodar(tipo, ctor, comLoaded: false, medirLayout: true, medirSettle: _comLoaded);
            var r2 = Rodar(tipo, ctor, comLoaded: false, medirLayout: false, medirSettle: false);
            var r3 = Rodar(tipo, ctor, comLoaded: false, medirLayout: false, medirSettle: false);
            return new Medida(tipo.Name, r1.Ms, r2.Ms, r1.LayoutMs, r1.SettleMs,
                r1.Retido / 1024, r1.Nos, r2.Sobra / 1024, r3.Sobra / 1024, "");
        }
        catch (Exception ex)
        {
            return new Medida(tipo.Name, 0, 0, 0, -1, 0, 0, 0, 0, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static ConstructorInfo? EscolherCtor(Type tipo) =>
        tipo.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.GetParameters().All(p => p.HasDefaultValue))
            .OrderBy(c => c.GetParameters().Length)
            .FirstOrDefault();

    private static object Criar(Type tipo, ConstructorInfo ctor) =>
        ctor.Invoke(ctor.GetParameters().Select(p => p.DefaultValue).ToArray());

    private sealed record Resultado(double Ms, double LayoutMs, double SettleMs, long Retido, int Nos, long Sobra);

    private static Resultado Rodar(Type tipo, ConstructorInfo ctor, bool comLoaded, bool medirLayout, bool medirSettle)
    {
        long antes = GC.GetTotalMemory(true);

        // A criação/medição/limpeza acontecem num frame QUE VAI SAIR antes do GC.
        // Se a página fosse criada e nulled aqui mesmo, a local viva desta pilha a seguraria
        // e o GC nunca a coletaria — o autoteste (1/2/3 vs 1b) prova exatamente isso, e era
        // a causa de todo "vazamento" medido até aqui (incluindo de new object() puro).
        var m = FaseInterna(tipo, ctor, medirLayout, medirSettle);

        long sobra = Coletar(antes);
        return new Resultado(m.Ms, m.LayoutMs, m.SettleMs, m.Retido, m.Nos, sobra);
    }

    private sealed record MedicaoInterna(double Ms, double LayoutMs, double SettleMs, long Retido, int Nos);

    private static MedicaoInterna FaseInterna(Type tipo, ConstructorInfo ctor, bool medirLayout, bool medirSettle)
    {
        long antes = GC.GetTotalMemory(true);

        var sw = Stopwatch.StartNew();
        object? pagina = Criar(tipo, ctor);
        sw.Stop();

        long retido = Math.Max(0, GC.GetTotalMemory(true) - antes);
        int nos = pagina is Page p ? ContarNos(p) : 0;

        double layoutMs = 0;
        if (medirLayout && pagina is Page pl)
        {
            var swL = Stopwatch.StartNew();
            try
            {
                pl.Measure(new Size(1440, 900));
                pl.Arrange(new Rect(0, 0, 1440, 900));
                pl.UpdateLayout();
            }
            catch { /* página que exige DataContext não invalida a medição */ }
            swL.Stop();
            layoutMs = swL.Elapsed.TotalMilliseconds;
        }

        double settleMs = -1;
        if (medirSettle && pagina is Page ps)
        {
            settleMs = MedirSettle(ps);
        }

        try { tipo.GetMethod("Cleanup", BindingFlags.Public | BindingFlags.Instance)?.Invoke(pagina, null); }
        catch (Exception ex) { Console.WriteLine($"!! Cleanup de {tipo.Name} FALHOU: {ex.InnerException?.Message ?? ex.Message}"); }

        // DEPOIS do Cleanup: deixa o Dispatcher esvaziar a fila (bindings adiados, Invoke de
        // Task.Run, timers de Background). Sem isso, trabalho em TRÂNSITO conta como vazamento.
        // 1200ms (era 400): MEDIDO — paginas com carga no Loaded (ProcessMonitorPage,
        // OptimizationPage) ainda tinham trabalho em voo em 400ms e eram acusadas de vazar
        // em sobra2/sobra3; o modo --root (WeakReference + 1,5s + GC) prova que liberam.
        BombeiaDispatcher(1200);
        pagina = null;

        return new MedicaoInterna(sw.Elapsed.TotalMilliseconds, layoutMs, settleMs, retido, nos);
    }

    /// <summary>Cria a página, roda o Cleanup e devolve só a WeakReference: a local que
    /// segurava a página morre junto com este frame, permitindo que o GC a colede.</summary>
    private static WeakReference CriarECleanup(Type tipo, ConstructorInfo ctor, bool comCleanup)
    {
        object? p = Criar(tipo, ctor);
        if (comCleanup)
            try { tipo.GetMethod("Cleanup", BindingFlags.Public | BindingFlags.Instance)?.Invoke(p, null); }
            catch (Exception ex) { Console.WriteLine($"Cleanup FALHOU: {ex.InnerException?.Message ?? ex.Message}"); }
        var wr = new WeakReference(p);
        p = null;
        return wr;
    }

    /// <summary>Levanta o Loaded e bombeia o Dispatcher até ficar ocioso: é o tempo que a UI
    /// fica presa processando o trabalho inicial da página.</summary>
    private static double MedirSettle(Page pagina)
    {
        var sw = Stopwatch.StartNew();
        bool ocioso = false;
        var contextoAnterior = SynchronizationContext.Current;
        try
        {
            // Em produção o WPF tem isto instalado: é o que faz `await` voltar para a UI thread.
            // Sem ele, toda continuação roda na ThreadPool e estoura VerifyAccess em DependencyProperty.
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => { ocioso = true; sw.Stop(); }));

            pagina.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

            var limite = DateTime.UtcNow.AddMilliseconds(_capSettleMs);
            while (!ocioso && DateTime.UtcNow < limite)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(5);
            }
        }
        catch { return -1; }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(contextoAnterior);
        }
        return ocioso ? sw.Elapsed.TotalMilliseconds : -1;
    }

    private static long Coletar(long antes)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return Math.Max(0, GC.GetTotalMemory(true) - antes);
    }

    /// <summary>Esvazia a fila do Dispatcher por até <paramref name="maxMs"/> ms (prioridade
    /// Background ou mais alta). É o equivalente ao que o app faz em produção: sem bombeio,
    /// um DispatcherInvoke pendente e um binding adiado parecem vazamento e não são.</summary>
    private static void BombeiaDispatcher(int maxMs)
    {
        try
        {
            var limite = DateTime.UtcNow.AddMilliseconds(maxMs);
            while (DateTime.UtcNow < limite)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                // volta pro loop só depois que a fila de Background esvaziar (ou estourar o tempo)
            }
        }
        catch { /* dispatcher em shutdown — nada a bombear */ }
    }

    // =================== DIAGNÓSTICO: o que segura a página viva ===================

    /// <summary>Cria a página 3x, descarta e verifica se ela continua viva. Se sim, procura o
    /// CAMINHO desde um campo estático até a página — ou seja, a causa raiz do vazamento.</summary>
    private static int Raiz(string nome, int esperaS = 0)
    {
        // CONTROLE: página sintética pura (sem XAML, sem eventos, sem Cleanup). Se ELA "vazar",
        // o problema é do harness, não das páginas do Kit.
        bool sintetico = nome.Equals("SINTETICO", StringComparison.OrdinalIgnoreCase);
        bool objetoPuro = nome.Equals("OBJECT", StringComparison.OrdinalIgnoreCase);

        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/KitLugia.GUI;component/Themes/Generic.xaml")
            });
        }
        catch { }

        Type tipo;
        ConstructorInfo? ctor;
        if (objetoPuro)
        {
            tipo = typeof(object);
            ctor = tipo.GetConstructor(Type.EmptyTypes)!;
            Console.WriteLine("== controle: new object() puro (sem WPF) ==");
        }
        else if (sintetico)
        {
            tipo = typeof(Page);
            ctor = tipo.GetConstructor(Type.EmptyTypes)!;
            Console.WriteLine("== controle sintético: new Page() puro, sem Cleanup ==");
        }
        else
        {
            tipo = typeof(KitLugia.GUI.App).Assembly.GetTypes()
                .FirstOrDefault(t => t.Name.Equals(nome, StringComparison.OrdinalIgnoreCase));
            if (tipo == null) { Console.WriteLine($"página '{nome}' não encontrada"); return 1; }
            ctor = EscolherCtor(tipo);
            if (ctor == null) { Console.WriteLine("sem construtor utilizável"); return 1; }
        }
        var tipoUsado = tipo;
        var ctorUsado = ctor;

        WeakReference? wr = null;
        int execucoes = _execucoes;
        for (int i = 0; i < execucoes; i++)
        {
            // criação + Cleanup num frame que SAI: nenhuma local de pilha segura a página aqui
            wr = CriarECleanup(tipoUsado, ctorUsado, comCleanup: !sintetico && !objetoPuro);

            // Deixa o trabalho em trânsito terminar ANTES de julgar: em produção o Dispatcher
            // bombeia o tempo todo, então um Invoke/ binding pendente não é vazamento.
            BombeiaDispatcher(1500);

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }

        if (wr == null || !wr.IsAlive) { Console.WriteLine($"{tipoUsado.Name}: NÃO vazou — libera normalmente (o que parecia retenção era trabalho pendente do WPF). "); return 0; }

        // Espera instrumentada: se a página só morre DEPOIS de N segundos, a retenção era
        // trabalho em background ainda em execução (Task.Run do construtor), não vazamento.
        for (int s = 1; s <= esperaS && wr.IsAlive; s++)
        {
            BombeiaDispatcher(1000);
            Thread.Sleep(1000);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Console.WriteLine(wr.IsAlive
                ? $"  t+{s}s: ainda viva"
                : $"  t+{s}s: LIBEROU — era trabalho em background, NÃO é vazamento");
        }
        if (!wr.IsAlive) return 0;

        Console.WriteLine($"{tipoUsado.Name}: CONFIRMADO vivo DEPOIS do Cleanup + bombeio + espera ({execucoes} construção(ões)). Procurando a raiz...");
        Console.WriteLine();

        var alvo = wr.Target!;
        var caminho = EncontrarCaminho(alvo);
        if (caminho == null)
        {
            Console.WriteLine("Nenhum campo estático (KitLugia + WPF) referencia a página.");
            Console.WriteLine("  (o grafo de busca está em EncontrarCaminho; orçamento 5M nós, profundidade 14)");
            Console.WriteLine("Suspeita: referência retida por cache do CLR/WPF ou por trabalho em background");
            Console.WriteLine("ainda em execução — rode de novo após aguardar 2 s.");
            return 2;
        }

        Console.WriteLine("CAUSA RAIZ:");
        foreach (var passo in caminho) Console.WriteLine("  " + passo);
        return 0;
    }

    private static List<string>? EncontrarCaminho(object alvo)
    {
        var raizes = new List<(string Nome, object Valor)>();

        var ordem = new List<Assembly>
        {
            typeof(KitLugia.GUI.App).Assembly, typeof(KitLugia.Core.Logger).Assembly,
            typeof(Application).Assembly, typeof(System.Windows.Media.Visual).Assembly,
            typeof(Dispatcher).Assembly,
        };
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            if (!ordem.Contains(asm)) ordem.Add(asm);

        foreach (var asm in ordem)
        {
            Type[] tipos;
            try { tipos = asm.GetTypes(); }
            catch (Exception ex)
            {
                Console.WriteLine($"   !! assembly NÃO enumerável: {asm.GetName().Name} -> {ex.GetType().Name}");
                continue;
            }
            foreach (var t in tipos)
            {
                FieldInfo[] campos; try { campos = t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                catch { continue; }
                foreach (var f in campos)
                {
                    if (f.IsLiteral) continue;
                    object? v;
                    try { v = f.GetValue(null); }
                    catch (Exception ex)
                    {
                        ErrosRaiz[$"{t.FullName}.{f.Name} -> {ex.InnerException?.GetType().Name ?? ex.GetType().Name}"] =
                            ErrosRaiz.GetValueOrDefault($"{t.FullName}.{f.Name} -> {ex.InnerException?.GetType().Name ?? ex.GetType().Name}") + 1;
                        continue;
                    }
                    if (v == null || v is string) continue;
                    raizes.Add(($"{t.FullName}.{f.Name}", v));
                }
            }
        }
        if (Application.Current != null) raizes.Add(("Application.Current", Application.Current));

        Console.WriteLine($"raizes estáticas consideradas: {raizes.Count}");
        if (ErrosRaiz.Count > 0)
        {
            var naoWinRt = ErrosRaiz.Keys
                .Where(k => !k.Contains("WinRT.") && !k.Contains("WinRT.", StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
            Console.WriteLine($"!! {ErrosRaiz.Values.Sum()} campo(s) estático(s) ilegível(s) — " +
                              $"{naoWinRt.Count} NÃO-WinRT (podem esconder a raiz):");
            foreach (var k in naoWinRt) Console.WriteLine($"   {k}");
        }

        var visitados = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var fila = new Queue<(object No, List<string> Passos)>();
        foreach (var (nome, valor) in raizes)
        {
            if (ReferenceEquals(valor, alvo)) return new List<string> { $"{nome}  ==> É A PRÓPRIA PÁGINA" };
            if (visitados.Add(valor)) fila.Enqueue((valor, new List<string> { nome }));
        }

        int processados = 0;
        const int Orcamento = 5_000_000;
        const int ProfMax = 14;
        while (fila.Count > 0 && processados++ < Orcamento)
        {
            var (no, passos) = fila.Dequeue();
            if (passos.Count > ProfMax) continue;

            foreach (var (nomeCampo, valorCampo) in EnumerarCampos(no))
            {
                if (valorCampo == null) continue;
                if (ReferenceEquals(valorCampo, alvo))
                {
                    var r = new List<string>(passos) { $"{nomeCampo}  ==> A PÁGINA" };
                    return r;
                }
                if (valorCampo is string) continue;
                if (passos.Count >= ProfMax) continue;
                if (visitados.Add(valorCampo)) fila.Enqueue((valorCampo, new List<string>(passos) { nomeCampo }));
            }
        }

        Console.WriteLine($"  explorados: {processados:N0} | fila restante: {fila.Count:N0} | visitados: {visitados.Count:N0}");
        Console.WriteLine(processados >= Orcamento
            ? "  => ORÇAMENTO ESGOTADO (grafo maior que o limite)."
            : "  => grafo esgotado: nenhuma raiz estática alcança a página.");
        return null;
    }

    private static IEnumerable<(string Nome, object? Valor)> EnumerarCampos(object o)
    {
        var t = o.GetType();
        if (o is Delegate del)
        {
            yield return ("Target", del.Target);
            yield break;
        }
        if (t.IsPrimitive || t.IsEnum || t == typeof(string)) yield break;

        if (o is Array arr)
        {
            if (arr.Rank != 1) yield break;
            int n = Math.Min(arr.Length, 512);
            for (int i = 0; i < n; i++) yield return ($"[{i}]", arr.GetValue(i));
            yield break;
        }

        for (var tt = t; tt != null && tt != typeof(object); tt = tt.BaseType)
        {
            FieldInfo[] campos;
            try { campos = tt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
            catch { continue; }
            foreach (var f in campos)
            {
                object? v; try { v = f.GetValue(o); } catch { continue; }
                yield return ($"{tt.Name}.{f.Name}", v);
            }
        }
    }

    /// <summary>Nós da árvore lógica: é o que o BAML materializa no clique.</summary>
    private static int ContarNos(DependencyObject raiz)
    {
        int total = 0;
        var fila = new Queue<(DependencyObject No, int Prof)>();
        fila.Enqueue((raiz, 0));
        while (fila.Count > 0)
        {
            var (no, prof) = fila.Dequeue();
            total++;
            if (prof > 48) continue;
            if (no is not FrameworkElement and not FrameworkContentElement) continue;
            foreach (var filho in System.Windows.LogicalTreeHelper.GetChildren(no))
            {
                if (filho is DependencyObject d) fila.Enqueue((d, prof + 1));
            }
        }
        return total;
    }
}
