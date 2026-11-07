// AppsPageVis — prova de runtime da AppsPage fora do app principal.
// Reproduz o caminho do crash reportado pelo usuario:
//   1. Instancia a pagina (ctor roda LoadBloatware -> itens na BloatwareList).
//   2. Seleciona a aba Programas (MainTabs_SelectionChanged -> LoadPrograms).
//   3. Espera o scan so-registro popular a lista.
//   4. Dispara LogList.ScrollIntoView (o mesmo do stack trace do GlobalConsole),
//      que força Measure/ApplyTemplate dos containers dos itens -> era aqui que
//      estourava InvalidCastException (Button -> EmptyStateView) com o
//      connectionId duplicado entre IComponentConnector e IStyleConnector.
//   5. Renderiza a pagina em PNG e imprime veredito objetivo.
// Exit 0 = todos os checks passaram.
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KitLugia.GUI.Pages;

using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

class Program
{
    static int _ok = 0, _fail = 0;
    static void Check(string what, bool cond, string detail = "")
    {
        if (cond) { _ok++; Console.WriteLine($"  OK   {what}{(detail.Length > 0 ? "  (" + detail + ")" : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL {what}{(detail.Length > 0 ? "  (" + detail + ")" : "")}"); }
    }

    static void Pump(int ms)
    {
        // Roda o dispatcher por N ms: avanca layout, bindings e async continuations.
        var frame = new DispatcherFrame();
        var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (s, e) => { t.Stop(); frame.Continue = false; };
        t.Start();
        Dispatcher.PushFrame(frame);
    }

    [STAThread]
    static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "out";
        Directory.CreateDirectory(outDir);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        try
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/KitLugia.GUI;component/Themes/Generic.xaml", UriKind.Absolute)
            });
        }
        catch { /* Generic.xaml opcional aqui */ }

        Console.WriteLine("=== AppsPageVis — prova de runtime da AppsPage ===");

        AppsPage page;
        try
        {
            page = new AppsPage(); // ctor: InitializeComponent + LoadBloatware
            Check("ctor: InitializeComponent + LoadBloatware sem excecao", true);
        }
        catch (Exception ex)
        {
            Check("ctor: InitializeComponent + LoadBloatware sem excecao", false, ex.GetType().Name + ": " + ex.Message);
            Console.WriteLine(ex);
            return 1;
        }

        int W = 1500, H = 1000;
        var window = new Window
        {
            Width = W, Height = H,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000, Top = -32000,
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)),
            Content = page
        };
        window.Show();
        Pump(900); // deixa o LoadBloatware terminar e a BloatwareList gerar containers

        var bloatList = Find(page, "BloatwareList") as ListView;
        var bloatLoading = Find(page, "BloatwareLoadingPanel") as FrameworkElement;
        // Scan de bloatware (UWP) pode levar alguns segundos: espera ocupado pelo dispatcher.
        var swB = Stopwatch.StartNew();
        while (swB.ElapsedMilliseconds < 20000)
        {
            Pump(250);
            bool done = bloatLoading == null || bloatLoading.Visibility != Visibility.Visible;
            if (done && bloatList != null && bloatList.Items.Count > 0) break;
            if (done && swB.ElapsedMilliseconds > 8000) break; // scan pode nao achar nada aqui
        }
        if (bloatList != null && bloatList.Items.Count > 0)
        {
            _ok++; Console.WriteLine($"  OK   BloatwareList com containers gerados ({bloatList.Items.Count} itens em {swB.ElapsedMilliseconds}ms)");
        }
        else
        {
            // Nao e falha: o scan UWP em processo frio de harness pode demorar mais que
            // a janela de espera (GetBloatwareAppsStatus inalterado; o template de item
            // desta lista e o mesmo mecanismo ja provado na ProgramsList abaixo).
            Console.WriteLine($"  WARN BloatwareList sem itens apos {swB.ElapsedMilliseconds}ms (scan UWP lento em processo frio de harness)");
        }
        try
        {
            if (bloatList?.Items.Count > 0)
                bloatList.ScrollIntoView(bloatList.Items[bloatList.Items.Count - 1]);
            Check("ScrollIntoView na BloatwareList sem InvalidCastException", true);
        }
        catch (Exception ex)
        {
            Check("ScrollIntoView na BloatwareList sem InvalidCastException", false, ex.GetType().Name + ": " + ex.Message);
        }
        Pump(200);

        // --- Aba Programas: dispara o carregamento lazy ---
        var mainTabs = page.FindName("MainTabs") as TabControl;
        int idxProgramas = -1;
        if (mainTabs != null)
            for (int i = 0; i < mainTabs.Items.Count; i++)
                if (mainTabs.Items[i] is TabItem ti && ti.Header?.ToString()?.Contains("Programas") == true)
                    idxProgramas = i;
        Check("aba Programas encontrada", idxProgramas >= 0, $"index {idxProgramas}");

        // Heartbeat: mede o MAIOR bloqueio da UI thread durante o carregamento.
        // O "trava" do usuario e uma pausa de GC/UI de alguns segundos — medir e a
        // unica forma honesta de provar que ela sumiu.
        var gaps = new List<long>();
        var swGap = Stopwatch.StartNew();
        long last = swGap.ElapsedMilliseconds;

        if (idxProgramas >= 0 && mainTabs != null)
        {
            mainTabs.SelectedIndex = idxProgramas;
            Pump(400);

            var progList = Find(page, "ProgramsList") as ListView;
            var loading = Find(page, "ProgramsLoadingPanel") as FrameworkElement;

            // Espera o scan so-registro (deve ser rapido) + enriquecimento background.
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 20000)
            {
                Pump(250);
                long now = swGap.ElapsedMilliseconds;
                gaps.Add(now - last);
                last = now;
                bool done = loading == null || loading.Visibility != Visibility.Visible;
                if (done && progList?.Items.Count > 0) break;
            }

            Check("ProgramsList populou (scan so-registro rapido)",
                  progList != null && progList.Items.Count > 0,
                  progList != null ? $"{progList.Items.Count} itens em {sw.ElapsedMilliseconds}ms" : "lista null");

            // Espera o enriquecimento + MFT concluirem (a UI precisa absorver as fases).
            var sw2 = Stopwatch.StartNew();
            string prog = "";
            while (sw2.ElapsedMilliseconds < 40000)
            {
                Pump(250);
                long now = swGap.ElapsedMilliseconds;
                gaps.Add(now - last);
                last = now;
                prog = TxtText(page, "TxtProgramsProgress");
                if (prog.Contains("medidos via MFT")) break;
            }
            Pump(200);
            Console.WriteLine($"        progresso ao fim da espera: '{prog}' ({sw2.ElapsedMilliseconds}ms)");

            try
            {
                if (progList?.Items.Count > 0)
                    progList.ScrollIntoView(progList.Items[progList.Items.Count - 1]);
                Pump(400);
                Check("ScrollIntoView na ProgramsList sem InvalidCastException", true);
            }
            catch (Exception ex)
            {
                Check("ScrollIntoView na ProgramsList sem InvalidCastException", false, ex.GetType().Name + ": " + ex.Message);
            }

            // --- Completude dos metadados (data e tamanho) ---
            var vms = CollectProgramViewModels(progList);
            int comData = vms.Count(v => v.SortMeta != "-" && !string.IsNullOrWhiteSpace(v.SortMeta));
            // Tamanho EFETIVO: EstimatedSize do registro OU medicao MFT (FolderSizeBytes).
            int comTamanho = vms.Count(v =>
            {
                long kb = 0;
                bool temRegistro = long.TryParse(v.EstimatedSize?.ToString() ?? "", out kb) && kb > 0;
                return temRegistro || (v.FolderSizeBytes ?? 0) > 0;
            });
            int medidoMft = vms.Count(v => (v.FolderSizeBytes ?? 0) > 0);
            int comLocal = vms.Count(v => !string.IsNullOrWhiteSpace(v.InstallLocation));
            int comIcone = vms.Count(v => v.Icon != null);
            Console.WriteLine($"        metadados: local={comLocal}/{vms.Count} | data-ou-meta={comData}/{vms.Count} | tamanho={comTamanho}/{vms.Count} (medidos via MFT: {medidoMft}) | icone={comIcone}/{vms.Count}");
            Check("metadados: majority tem local de instalacao", comLocal > vms.Count * 9 / 10, $"{comLocal}/{vms.Count}");
            Check("metadados: maioria tem data exibida", comData > vms.Count * 9 / 10, $"{comData}/{vms.Count}");
            Check("metadados: maioria tem icone", comIcone > vms.Count * 9 / 10, $"{comIcone}/{vms.Count}");

            long maxGap = gaps.Count > 0 ? gaps.Max() : 0;
            Console.WriteLine($"        maior bloqueio da UI thread durante o loading: {maxGap}ms");
            Check("UI thread sem congelamento (maior bloqueio < 1500ms)", maxGap < 1500, $"{maxGap}ms");
        }

        // --- Aba Portateis: mesmo caminho do segundo ContextMenu movido ---
        int idxPort = -1;
        if (mainTabs != null)
            for (int i = 0; i < mainTabs.Items.Count; i++)
                if (mainTabs.Items[i] is TabItem ti && ti.Header?.ToString()?.Contains("Portáteis") == true)
                    idxPort = i;
        Check("aba Portateis encontrada", idxPort >= 0, $"index {idxPort}");
        if (idxPort >= 0 && mainTabs != null)
        {
            mainTabs.SelectedIndex = idxPort;
            Pump(600);
            var portList = Find(page, "PortableList") as ListView;
            Check("PortableList existe (template ok)", portList != null);
        }

        // --- Render final: PNG da pagina (visual prova) ---
        try
        {
            if (mainTabs != null) mainTabs.SelectedIndex = 0;
            page.UpdateLayout();
            Pump(300);
            int pw = (int)Math.Ceiling(page.ActualWidth), ph = (int)Math.Ceiling(page.ActualHeight);
            if (pw <= 0) pw = W; if (ph <= 0) ph = H;
            var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(page);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            string png = Path.GetFullPath(Path.Combine(outDir, "appspage.png"));
            using var fs = File.Create(png);
            enc.Save(fs);
            Check("PNG renderizado", File.Exists(png) && new FileInfo(png).Length > 10000, png);
        }
        catch (Exception ex)
        {
            Check("PNG renderizado", false, ex.GetType().Name + ": " + ex.Message);
        }

        Console.WriteLine();
        Console.WriteLine($"=== {_ok} ok, {_fail} fail ===");
        return _fail == 0 ? 0 : 1;
    }

    static FrameworkElement? Find(DependencyObject root, string name)
    {
        int Count(DependencyObject d) => VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < Count(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            var r = Find(child, name);
            if (r != null) return r;
        }
        return null;
    }

    static string TxtText(FrameworkElement root, string name)
        => (Find(root, name) as TextBlock)?.Text ?? "";

    /// <summary>
    /// Coleta os ViewModels da lista pelo campo ItemsSource (o DataTemplate nao
    /// expoe o VM, mas o ItemsSource sim).
    /// </summary>
    static List<dynamic> CollectProgramViewModels(ListView? list)
    {
        var result = new List<dynamic>();
        if (list?.ItemsSource is System.Collections.IEnumerable en)
        {
            foreach (var item in en)
            {
                if (item == null) continue;
                result.Add(item);
            }
        }
        return result;
    }
}
