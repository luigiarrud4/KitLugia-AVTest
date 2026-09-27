// Mede o custo REAL de construir a janela do Gerenciador de Tarefas do KitLugia.
//
// É esse custo (parse do BAML/XAML gigante + JIT das partials) que o Pré-aquecimento
// (KitTaskManagerWindow.Prewarm) tira do clique: a 1ª abertura passa a ser Show()+Activate()
// de uma janela já construída.
//
// A janela é construída e DESCARTADA sem Show(): o Loaded nunca dispara, então nenhum timer
// de refresh começa e nenhuma coleta de sistema roda. Nada aqui altera o sistema.
//
// Uso: dotnet run --project tests/TaskManagerWindowBench [-- tabs]

using System.Diagnostics;
using KitLugia.GUI.Windows.TaskManager;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool abas = args.Contains("tabs");
        Console.WriteLine("=== KitLugia — custo de construção da janela do Gerenciador de Tarefas ===");
        Console.WriteLine();

        // JIT frio é parte do que o usuário sente — por isso a PRIMEIRA medição não é descartada.
        var tempos = new List<double>();
        for (int i = 0; i < 4; i++)
        {
            var sw = Stopwatch.StartNew();
            KitTaskManagerWindow? w = null;
            try
            {
                w = new KitTaskManagerWindow();
                sw.Stop();
                tempos.Add(sw.Elapsed.TotalMilliseconds);
                Console.WriteLine($"  construção #{i + 1}: {sw.Elapsed.TotalMilliseconds,7:F1} ms");

                if (abas) MedirAbas(w);
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.WriteLine($"  construção #{i + 1}: FALHOU — {ex.GetType().Name}: {ex.Message}");
                break;
            }
            finally
            {
                // Fecha sem Show(): os handlers de Closed param timers se algum tiver começado.
                try { w?.Close(); } catch { }
            }
        }

        if (tempos.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  frio (1ª construção, o que o usuário paga sem prewarm): {tempos[0],7:F1} ms");
            if (tempos.Count > 1)
            {
                double depois = tempos.Skip(1).Average();
                Console.WriteLine($"  quente (média das seguintes, o que o prewarm entrega):  {depois,7:F1} ms");
                Console.WriteLine();
                Console.WriteLine($"  O prewarm em idle move {tempos[0]:F0} ms de UI thread do CLIQUE para um momento");
                Console.WriteLine("  em que o app está ocioso — o clique fica só com Show()+Activate().");
            }
        }

        return 0;
    }

    /// <summary>Mede a troca de aba: cada aba nova paga o próprio layout/construção na 1ª vez.</summary>
    private static void MedirAbas(KitTaskManagerWindow w)
    {
        string[] tags = { "Diagnostic", "Storage", "Latency", "Performance", "Services", "Startup", "Users", "Connections", "Processes", "Summary" };
        foreach (var tag in tags)
        {
            var sw = Stopwatch.StartNew();
            try { w.SwitchTabByTag(tag); }
            catch (Exception ex) { Console.WriteLine($"      {tag,-12} FALHOU ({ex.GetType().Name})"); continue; }
            sw.Stop();
            Console.WriteLine($"      {tag,-12} {sw.Elapsed.TotalMilliseconds,7:F1} ms");
        }
    }
}
