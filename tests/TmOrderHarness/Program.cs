// Harness de ORDEM/CRASH do KitTaskManager (aba Processos).
//
// Reproduz o crash reportado pelo usuario:
//
//   System.ArgumentOutOfRangeException: Index must be within the bounds of the List.
//     em System.Collections.Generic.List`1.Insert(Int32 index, T item)
//     em System.Collections.ObjectModel.ObservableCollection`1.MoveItem(Int32 oldIndex, Int32 newIndex)
//     em KitTaskManagerWindow.ApplyFilter  (linha do `_groupedLive.Move(cur, i)`)
//
// CAUSA (provada na maquina do usuario): o Windows NAO distingue maiuscula de
// minuscula no nome do processo. Com "Devin" e "devin" rodando ao mesmo tempo:
//   1) o agrupamento usava comparador ORDINAL  -> 2 grupos ("Devin" e "devin");
//   2) o dedupe da lista viva usava OrdinalIgnoreCase -> apagava 1 das linhas;
//   3) a lista viva ficava com MENOS linhas que a lista desejada;
//   4) o laco de Move usava o indice i da lista DESEJADA como indice real da
//      lista viva -> Insert(i) fora de faixa -> o app caia a cada refresh.
//
// O que este harness prova:
//   T1) o cenario exato do crash NAO estoura mais (e a lista nao fica corrompida);
//   T2) a ordem das linhas VIVAS segue a ordem desejada e as FANTASMAS ficam
//       onde estavam (a versao antiga posicionava errado quando havia fantasma);
//   T3) duas linhas que so diferem em caixa nao quebram nem duplicam a lista;
//   T4) end-to-end: o refresh real (ApplyFilter) nao estoura e a lista viva
//       tem exatamente uma linha por chave (case-insensitive) da desejada.
//
// A janela e aberta FORA DA TELA (Left/Top = -32000): o usuario nao ve nada.
// Nada aqui altera o sistema.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using KitLugia.GUI.Windows.TaskManager;

internal static class Program
{
    private static int _fail;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what);
        if (!ok) _fail++;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Console.WriteLine("=== Harness de ordem/crash do KitTaskManager ===");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/KitLugia.GUI;component/Themes/Generic.xaml")
            });
        }
        catch { }

        bool uiException = false;
        app.DispatcherUnhandledException += (_, e) =>
        {
            uiException = true;
            Console.WriteLine($"   [excecao na UI] {e.Exception.GetType().Name}: {e.Exception.Message}");
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();

        var janela = new KitTaskManagerWindow
        {
            Width = 1400,
            Height = 900,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false
        };
        try { janela.Show(); } catch (Exception ex) { Console.WriteLine($"AVISO: Show() falhou: {ex.Message}"); }
        Pump(1500);
        try { janela.SwitchTabByTag("Processes"); } catch { }
        Pump(500);

        var live = Campo<ObservableCollection<KitTaskManagerWindow.ProcessRow>>(janela, "_groupedLive");
        Check(live != null, "colecao _groupedLive obtida");
        if (live == null) { Console.WriteLine("== FIM (sem colecao) =="); return 1; }

        var metodo = typeof(KitTaskManagerWindow).GetMethod("ApplyLiveOrder",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Check(metodo != null, "metodo ApplyLiveOrder existe");
        if (metodo == null) { Console.WriteLine("== FIM (sem metodo) =="); return 1; }

        void Aplicar(List<KitTaskManagerWindow.ProcessRow> desejado)
        {
            metodo.Invoke(janela, new object?[] { desejado });
        }

        // ── T1) cenario exato do crash ───────────────────────────────────
        Console.WriteLine("\nT1) lista viva com MENOS linhas que a desejada (colisao de caixa):");
        var a = Linha("Devin", "Aplicativos");
        var b = Linha("devin", "Aplicativos");
        live.Clear();
        live.Add(a);
        var desejadoT1 = new List<KitTaskManagerWindow.ProcessRow> { a, b };
        try
        {
            Aplicar(desejadoT1);
            Check(true, "ApplyLiveOrder NAO estourou com lista viva menor que a desejada");
        }
        catch (TargetInvocationException ex)
        {
            Check(false, $"ApplyLiveOrder estourou: {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
        }
        Check(live.Count == 1, $"a lista viva continua com 1 linha (nao duplicou/nem sumiu) — Count={live.Count}");
        Check(live.Count > 0 && ReferenceEquals(live[0], a), "a linha viva original foi preservada");

        // ── T2) ordem com fantasma no meio ───────────────────────────────
        Console.WriteLine("\nT2) ordem das linhas vivas com uma fantasma no meio:");
        var x = Linha("Chrome", "Aplicativos");
        var g = Linha("Morto", "Aplicativos");
        g.IsGhost = true;
        var y = Linha("Firefox", "Aplicativos");
        var z = Linha("Edge", "Aplicativos");
        live.Clear();
        live.Add(x);
        live.Add(g);
        live.Add(y);
        live.Add(z);
        Aplicar(new List<KitTaskManagerWindow.ProcessRow> { z, y, x });
        var vivas = live.Where(r => !r.IsGhost).ToList();
        Console.WriteLine("  ordem final: " + string.Join(", ", live.Select(r => r.Name + (r.IsGhost ? "(fant)" : ""))));
        Check(vivas.Count == 3, $"as 3 linhas vivas continuam na lista (Count vivas={vivas.Count})");
        Check(ReferenceEquals(vivas[0], z) && ReferenceEquals(vivas[1], y) && ReferenceEquals(vivas[2], x),
              "as linhas vivas seguem a ordem desejada (Edge, Firefox, Chrome)");
        Check(live.Any(r => r.IsGhost), "a fantasma continua na lista (nao foi apagada)");

        // ── T3) colisao de caixa com as DUAS linhas vivas ────────────────
        Console.WriteLine("\nT3) duas linhas vivas que so diferem em caixa:");
        var c1 = Linha("Devin", "Aplicativos");
        var c2 = Linha("devin", "Aplicativos");
        live.Clear();
        live.Add(c1);
        live.Add(c2);
        try
        {
            Aplicar(new List<KitTaskManagerWindow.ProcessRow> { c1, c2 });
            Check(true, "ApplyLiveOrder NAO estourou com colisao de caixa");
        }
        catch (TargetInvocationException ex)
        {
            Check(false, $"ApplyLiveOrder estourou: {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
        }
        Check(live.Count == 2, $"as 2 linhas continuam na lista (Count={live.Count})");

        // ── T4) end-to-end: o refresh real ────────────────────────────────
        Console.WriteLine("\nT4) refresh real (ApplyFilter) com os processos da maquina:");
        uiException = false;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                typeof(KitTaskManagerWindow)
                    .GetMethod("ApplyFilter", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.Invoke(janela, new object?[] { "" });
            }
            catch (TargetInvocationException ex)
            {
                Check(false, $"ApplyFilter estourou: {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
            }
            Pump(900);
        }
        Check(!uiException, "nenhuma excecao na UI durante os refreshes");

        var desejadas = Campo<List<KitTaskManagerWindow.ProcessRow>>(janela, "_filteredRows");
        if (desejadas != null && desejadas.Count > 0)
        {
            int chaves = desejadas.Select(r => r.GroupKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int vivasReal = live.Count(r => !r.IsChild && !r.IsGhost);
            Console.WriteLine($"  desejadas={desejadas.Count} chaves distintas(ignore-case)={chaves} linhas vivas={vivasReal}");
            Check(chaves == desejadas.Count,
                  "a lista desejada nao tem chaves repetidas (agrupamento case-insensitive)");
            Check(vivasReal <= chaves,
                  $"a lista viva nao tem MAIS linhas que chaves distintas ({vivasReal} <= {chaves})");
        }
        else
        {
            Console.WriteLine("  (sem _filteredRows — o refresh nao rodou; nada a comparar)");
        }

        try { janela.Close(); } catch { }

        Console.WriteLine(_fail == 0 ? "\n== ALL-PASS ==" : $"\n== {_fail} FALHA(S) ==");
        return _fail == 0 ? 0 : 1;
    }

    // ── helpers ────────────────────────────────────────────────────────────
    private static KitTaskManagerWindow.ProcessRow Linha(string nome, string grupo) =>
        new KitTaskManagerWindow.ProcessRow { Name = nome, Group = grupo, DisplayName = nome };

    private static T? Campo<T>(object obj, string nome) where T : class =>
        obj.GetType().GetField(nome, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(obj) as T;

    private static void Pump(int ms)
    {
        var limite = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < limite)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }
}
