// Auditoria do MENU DE CONTEXTO do KitTaskManager (aba Processos).
//
// O que prova:
//   1) AUDITORIA ESTATICA: todo handler declarado no XAML da janela existe como metodo
//      (um handler renomeado = XamlParseException em tempo de execucao, ou seja,
//      o menu inteiro MORRE ao abrir);
//   2) o item "Limpar memoria de trabalho" existe no menu, com o handler ligado;
//   3) o item de N aparece quando ha multi-selecao, com o cabecalho certo;
//   4) o clique REAL no item limpa a memoria e a barra de status mostra o numero
//      verdadeiro (antes -> depois), ou o motivo da falha.
//
// A janela e aberta FORA DA TELA (Left/Top = -32000): o usuario nao ve nada.
// Nada aqui altera o sistema alem do trim de teste (que e o proprio alvo).

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        Console.WriteLine("=== Auditoria do menu de contexto do KitTaskManager ===");

        // ─────────────────────────────────────────────────────────────────
        // 1) AUDITORIA ESTATICA dos handlers do XAML
        // ─────────────────────────────────────────────────────────────────
        string xaml = LocalizarXaml();
        Console.WriteLine($"\nXAML: {xaml}");
        var metodos = typeof(KitTaskManagerWindow)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var handlers = Regex.Matches(File.ReadAllText(xaml), "(?:Click|ContextMenuOpening|Closed|SelectionChanged|MouseDoubleClick|TextChanged|Checked|Unchecked|SizeChanged|Loaded)=\"([A-Za-z0-9_]+)\"")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .OrderBy(s => s)
            .ToList();

        int orfaos = 0;
        foreach (string h in handlers)
        {
            bool existe = metodos.Contains(h);
            if (!existe) orfaos++;
            Console.WriteLine($"    {(existe ? "ok  " : "ORFAO")} {h}");
        }
        Console.WriteLine($"  total de handlers no XAML: {handlers.Count}");
        Check(orfaos == 0, $"todo handler do XAML existe na janela (orfaos: {orfaos})");

        // ─────────────────────────────────────────────────────────────────
        // 2) Janela real: menu, item de limpar RAM e clique de verdade
        // ─────────────────────────────────────────────────────────────────
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/KitLugia.GUI;component/Themes/Generic.xaml")
            });
        }
        catch { }

        app.DispatcherUnhandledException += (_, e) =>
        {
            Console.WriteLine($"   [excecao na UI absorvida] {e.Exception.GetType().Name}: {e.Exception.Message}");
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

        Pump(2500);
        try { janela.SwitchTabByTag("Processes"); } catch { }
        Pump(600);

        var dg = (DataGrid?)Campo(janela, "DgProcesses");
        Check(dg != null, "DataGrid de processos encontrado");
        if (dg == null) { Console.WriteLine("== FIM (sem grid) =="); return 1; }

        var menu = dg.ContextMenu;
        Check(menu != null, "a lista de processos tem menu de contexto");
        if (menu == null) { Console.WriteLine("== FIM (sem menu) =="); return 1; }

        // ── 2.1) inventario do menu ──────────────────────────────────────
        Console.WriteLine("\nItens do menu de contexto da aba Processos:");
        var itens = Achatar(menu.Items).OfType<MenuItem>().ToList();
        foreach (var it in itens)
        {
            string cab = (it.Header as string) ?? it.Header?.ToString() ?? "";
            Console.WriteLine($"    - {cab}  [Icon={it.Icon}]  visivel={it.Visibility}");
        }
        Check(itens.Count > 0, $"o menu tem {itens.Count} itens");

        // ── 2.2) item de limpar memoria ──────────────────────────────────
        var itemLimpar = itens.FirstOrDefault(i => (i.Header as string) == "Limpar memória de trabalho");
        Check(itemLimpar != null, "menu tem o item \"Limpar memória de trabalho\"");

        var itemLimparN = itens.FirstOrDefault(i => (i.Header as string)?.StartsWith("Limpar memória de") == true
                                                    && (i.Header as string) != "Limpar memória de trabalho");
        Check(itemLimparN != null, "menu tem o item de N processos (\"Limpar memória de N processos\")");
        Check(itemLimparN?.Visibility == Visibility.Collapsed,
              "o item de N começa oculto (so aparece com multi-seleção)");

        // ── 2.3) o item simples NAO pode estar com o header duplicado ────
        var eco = itens.FirstOrDefault(i => (i.Header as string)?.Contains("EcoQoS") == true && !(i.Header as string)!.Contains("("));
        Check(eco != null && !(eco.Header as string)!.Contains("  "),
              $"item EcoQoS sem espaço duplo ('{eco?.Header}')");

        // ── 2.4) multi-seleção: o item de N aparece com o numero certo ───
        var linhas = new List<object>();
        foreach (var o in (IEnumerable)dg.Items) linhas.Add(o!);
        Console.WriteLine($"\nlinhas na lista: {linhas.Count}");
        if (linhas.Count >= 2)
        {
            dg.SelectedItems.Clear();
            dg.SelectedItems.Add(linhas[0]);
            dg.SelectedItems.Add(linhas[1]);
            Invocar(janela, "UpdateMultiSelectionUi");
            Pump(200);
            var itemN = (MenuItem?)Campo(janela, "MenuClearMemMulti");
            Check(itemN != null, "campo MenuClearMemMulti existe");
            Check(itemN?.Visibility == Visibility.Visible, "com 2 selecionadas o item de N fica VISIVEL");
            Check(itemN?.Header as string == "Limpar memória de 2 processos",
                  $"cabecalho do item de N = '{itemN?.Header}'");
        }

        // ── 2.5) CLIQUE REAL: limpa a memoria e mostra o numero real ─────
        if (linhas.Count >= 1)
        {
            dg.SelectedItems.Clear();
            dg.SelectedItem = linhas[0];

            var alvo = linhas[0];
            string nome = Prop(alvo, "Name") as string ?? "?";
            int pid = Convert.ToInt32(Prop(alvo, "Pid") ?? 0);
            Console.WriteLine($"\nclique em \"Limpar memória de trabalho\" no processo {nome} (PID {pid})");

            if (itemLimpar != null)
                itemLimpar.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump(300);

            string status = AguardaStatus(janela);
            Console.WriteLine($"  status: {status}");
            Check(!string.IsNullOrWhiteSpace(status), "a barra de status recebeu a resposta");
            Check(status.Contains("🧹") || status.Contains("⚠️"),
                  "o status mostra o resultado real (limpo ou falha com motivo)");
            Check(status.Contains("MB"), "o status traz o numero de MB (antes/depois)");
            Check(!status.Contains("Memória de") || !status.EndsWith("limpa."),
                  "o status nao usa a mensagem antiga que mentia (\"memória limpa\" sem numero)");
        }

        // ── 2.6) o botao do painel de detalhe usa o mesmo caminho ─────────
        Invoke(janela, "BtnClearMemory_Click");
        Pump(300);
        string status2 = AguardaStatus(janela);
        Console.WriteLine($"  status apos o botao do painel: {status2}");
        Check(status2.Contains("🧹") || status2.Contains("⚠️"),
              "o botao do painel de detalhe tambem devolve o resultado real");

        // ── 2.7) GRUPO: suspender/retomar precisa atingir TODOS os membros ──
        Console.WriteLine("\n== GRUPO (2 processos com o mesmo nome) ==");
        var filhos = new List<System.Diagnostics.Process>();
        try
        {
            // "ping -t" em console OCULTO (CreateNoWindow): 2 processos inofensivos
            // que a lista agrupa por nome (exatamente o cenario do bug).
            string ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
            if (!File.Exists(ping)) { Console.WriteLine("  (ping.exe nao encontrado: teste ignorado)"); }
            else
            {
                for (int i = 0; i < 2; i++)
                    filhos.Add(System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ping, "-t 127.0.0.1")
                    { UseShellExecute = false, CreateNoWindow = true })!);

                // Espera a lista materializar o grupo (o refresh roda sozinho).
                object? grupo = null;
                for (int tent = 0; tent < 40 && grupo == null; tent++)
                {
                    Pump(500);
                    // O nome vem em CAIXA ALTA da enumeracao de processos ("PING.EXE").
                    grupo = ((IEnumerable)dg.Items).Cast<object>()
                        .FirstOrDefault(o => string.Equals(Prop(o, "Name") as string, "ping.exe", StringComparison.OrdinalIgnoreCase) &&
                                             (Prop(o, "ChildCount") as int?) >= 2);
                }
                Console.WriteLine($"    filhos vivos: {filhos.Count(f => { try { return !f.HasExited; } catch { return false; } })}/{filhos.Count}");
                var todos = ((IEnumerable)dg.Items).Cast<object>().ToList();
                Console.WriteLine($"    linhas na lista: {todos.Count}");
                foreach (var o in todos.Where(o => (Prop(o, "Name") as string)?.Contains("ping", StringComparison.OrdinalIgnoreCase) == true))
                    Console.WriteLine($"      -> nome='{Prop(o, "Name")}' childCount={Prop(o, "ChildCount")} display='{Prop(o, "DisplayName")}'");
                Check(grupo != null, "a lista formou o grupo de 2 processos ping.exe");
                if (grupo != null)
                {
                    int membros = Convert.ToInt32(Prop(grupo, "ChildCount") ?? 0);
                    Console.WriteLine($"    membros do grupo: {membros}");
                    dg.SelectedItems.Clear();
                    dg.SelectedItem = grupo;
                    Pump(200);

                    Invoke(janela, "MenuSuspend_Click");
                    Pump(800);
                    string stSus = Texto(janela, "TxtStatus") ?? "";
                    Console.WriteLine($"  status: {stSus}");
                    // Com o bug antigo, so o PID representante era suspenso: "/1".
                    Check(stSus.Contains($"/{membros}") && stSus.Contains("suspenso"),
                          $"suspender atingiu TODOS os {membros} processos do grupo");
                    Check(StatusSemFalha(stSus),
                          $"o status de suspender nao se contradiz ('{stSus}')");

                    Invoke(janela, "MenuResume_Click");
                    Pump(800);
                    string stRes = Texto(janela, "TxtStatus") ?? "";
                    Console.WriteLine($"  status: {stRes}");
                    Check(stRes.Contains($"/{membros}") && stRes.Contains("retomado"),
                          $"retomar atingiu TODOS os {membros} processos do grupo");
                    Check(StatusSemFalha(stRes),
                          $"o status de retomar nao se contradiz ('{stRes}')");

                    // EcoQoS no grupo: o mesmo caminho precisa fechar o handle sem
                    // contar falha (o bug do EntryPoint vivia aqui tambem).
                    Invoke(janela, "MenuEcoQoS_Click");
                    Pump(800);
                    string stEco = Texto(janela, "TxtStatus") ?? "";
                    Console.WriteLine($"  status: {stEco}");
                    Check(!stEco.Contains("acesso negado", StringComparison.OrdinalIgnoreCase),
                          $"EcoQoS no grupo nao acusou acesso negado ('{stEco}')");
                }
            }
        }
        finally
        {
            foreach (var f in filhos)
            {
                try { if (!f.HasExited) f.Kill(); } catch { }
                try { f.Dispose(); } catch { }
            }
        }

        try { janela.Close(); } catch { }

        Console.WriteLine(_fail == 0 ? "\n== ALL-PASS ==" : $"\n== {_fail} FALHA(S) ==");
        return _fail == 0 ? 0 : 1;
    }

    // ── helpers ────────────────────────────────────────────────────────────
    private static IEnumerable<object> Achatar(IEnumerable items)
    {
        foreach (var i in items)
        {
            yield return i!;
            if (i is MenuItem mi && mi.Items != null)
                foreach (var c in Achatar(mi.Items)) yield return c;
        }
    }

    private static object? Campo(object obj, string nome) =>
        obj.GetType().GetField(nome, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(obj);

    private static string? Texto(object obj, string nome) =>
        Campo(obj, nome) is TextBlock tb ? tb.Text : null;

    private static object? Prop(object obj, string nome) =>
        obj.GetType().GetProperty(nome, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(obj);

    private static void Invocar(object obj, string metodo) =>
        obj.GetType().GetMethod(metodo, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            ?.Invoke(obj, null);

    private static void Invoke(object obj, string metodo)
    {
        var m = obj.GetType().GetMethod(metodo, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        if (m == null) return;
        m.Invoke(obj, new object?[] { obj, new RoutedEventArgs() });
    }

    /// <summary>
    /// O status e' contraditorio quando diz que TODOS deram certo e ainda
    /// acusa falhas ("2/2 suspenso(s) (2 falharam: codigo 0)"). Foi exatamente o
    /// que apareceu com o EntryPoint faltando no CloseHandleP: a excecao de
    /// fechamento virava "falha" DEPOIS do sucesso ja contabilizado.
    /// </summary>
    private static bool StatusSemFalha(string status) =>
        !status.Contains("falharam", StringComparison.OrdinalIgnoreCase) &&
        !status.Contains("falhou", StringComparison.OrdinalIgnoreCase) &&
        !status.Contains("acesso negado", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Espera a acao TERMINAR (o status sai da mensagem de "em andamento").
    /// Tempo fixo deixava o teste depender da maquina: limpar 30 processos
    /// pode passar de 1 s e o harness lia a mensagem de progresso.
    /// </summary>
    private static string AguardaStatus(object janela, int limiteSegundos = 30)
    {
        var fim = DateTime.UtcNow.AddSeconds(limiteSegundos);
        string atual = "";
        while (DateTime.UtcNow < fim)
        {
            Pump(200);
            atual = Texto(janela, "TxtStatus") ?? "";
            if (!atual.Contains("Limpando", StringComparison.OrdinalIgnoreCase) &&
                !atual.Contains("Finalizando", StringComparison.OrdinalIgnoreCase) &&
                !atual.Contains("Force Stop", StringComparison.OrdinalIgnoreCase))
                return atual;
        }
        return atual;
    }

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

    private static string LocalizarXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string p = Path.Combine(dir.FullName, "KitLugia.GUI", "Windows", "TaskManager", "KitTaskManagerWindow.xaml");
            if (File.Exists(p)) return p;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("KitTaskManagerWindow.xaml nao encontrado");
    }
}
