// UiShotHarness — captura visual das paginas do KitLugia (F6 do docs/UI_CONSISTENCY_PLAN.md).
//
// Mesmo padrao do NetPageVis/TmVisualBench: cada Page e instanciada e renderizada
// FORA da tela (janela em -32000), sem elevacao e sem UIA, e salva em PNG.
// Serve para comparar antes/depois de cada fase da padronizacao visual.
//
// Uso:
//   dotnet run --project tests/UiShotHarness -c Release
//   dotnet run --project tests/UiShotHarness -c Release -- --only Tweaks,Dashboard
//   ... -- --list                 (so lista as paginas encontradas)
//   ... -- --out C:\temp\ui       (diretorio de saida)
//   ... -- --size 1200x900        (viewport)
//   ... -- --pump 900             (ms de espera por pagina)
//
// Read-only: o harness nao clica em nada, so abre e mede.

using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class Program
{
    private static int _w = 1200;
    private static int _h = 900;
    private static int _dpi = 96;
    private static int _pumpMs = 900;
    private static string _outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "out");
    private static string _only = "";

    private static double Escala => _dpi / 96.0;

    [STAThread]
    private static int Main(string[] args)
    {
        int iOnly = Array.IndexOf(args, "--only");
        if (iOnly >= 0 && iOnly + 1 < args.Length) _only = args[iOnly + 1];

        int iOut = Array.IndexOf(args, "--out");
        if (iOut >= 0 && iOut + 1 < args.Length) _outDir = args[iOut + 1];

        int iPump = Array.IndexOf(args, "--pump");
        if (iPump >= 0 && iPump + 1 < args.Length && int.TryParse(args[iPump + 1], out int p)) _pumpMs = p;

        int iSize = Array.IndexOf(args, "--size");
        if (iSize >= 0 && iSize + 1 < args.Length)
        {
            var partes = args[iSize + 1].Split('x');
            if (partes.Length == 2 && int.TryParse(partes[0], out int w) && int.TryParse(partes[1], out int h))
            { _w = w; _h = h; }
        }

        if (args.Contains("--selfcheck"))
        {
            var appCheck = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            appCheck.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/KitLugia.GUI;component/Themes/Generic.xaml")
            });
            var display = appCheck.TryFindResource("FontSizeDisplay");
            Console.WriteLine($"FontSizeDisplay = {display ?? "<null>"} ({display?.GetType().Name})");
            var estilo = appCheck.TryFindResource("PageTitleStyle") as Style;
            Console.WriteLine($"PageTitleStyle = {(estilo == null ? "<null>" : estilo.Setters.Count + " setters")}");
            if (estilo != null)
                foreach (var s in estilo.Setters.OfType<Setter>())
                    Console.WriteLine($"   {s.Property.Name} = {s.Value ?? "<null>"} ({s.Value?.GetType().Name})");
            var tb = new TextBlock { Text = "teste" };
            if (estilo != null) tb.Style = estilo;
            var host = new Window { Content = tb, Width = 300, Height = 100, Left = -32000, Top = -32000, ShowInTaskbar = false };
            host.Show();
            tb.UpdateLayout();
            Console.WriteLine($"TextBlock.FontSize aplicado = {tb.FontSize}");
            host.Close();
            return 0;
        }

        var paginas = DescobrirPaginas();
        if (args.Contains("--list"))
        {
            Console.WriteLine($"=== {paginas.Count} pagina(s) encontrada(s) ===");
            foreach (var t in paginas) Console.WriteLine($"  {t.Name,-34} {t.Namespace}");
            return 0;
        }

        Directory.CreateDirectory(_outDir);
        Console.WriteLine("=== UiShotHarness — captura visual das paginas ===");
        Console.WriteLine($"viewport {_w}x{_h} · dpi {_dpi} · out {Path.GetFullPath(_outDir)}");
        Console.WriteLine();

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/KitLugia.GUI;component/Themes/Generic.xaml")
            });
        }
        catch (Exception ex) { Console.WriteLine($"AVISO: Generic.xaml nao carregou ({ex.Message})"); }

        app.DispatcherUnhandledException += (_, e) =>
        {
            Console.WriteLine($"   [excecao na UI absorvida] {e.Exception.GetType().Name}: {e.Exception.Message}");
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        var indice = new StringBuilder("pagina;status;largura;altura;png\n");
        int ok = 0, falhas = 0;

        foreach (var tipo in paginas)
        {
            string nome = tipo.Name;
            string png = Path.Combine(_outDir, nome + ".png");
            FrameworkElement? pagina = null;
            Window? janela = null;

            try
            {
                // Construtores com parametros OPCIONAIS contam (ServicesPage(int tabIndex = 0)).
                var ctor = tipo.GetConstructors()
                    .First(c => c.GetParameters().All(p => p.IsOptional));
                pagina = (FrameworkElement)ctor.Invoke(ctor.GetParameters().Select(p => p.DefaultValue).ToArray());

                janela = new Window
                {
                    Width = _w,
                    Height = _h,
                    Left = -32000,
                    Top = -32000,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)),
                    Content = pagina,
                };

                janela.Show();
                Pump(_pumpMs);

                var bmp = Render(pagina);
                SalvarPng(bmp, png);
                ok++;
                Console.WriteLine($"OK   {nome,-34} {bmp.PixelWidth}x{bmp.PixelHeight}");
                indice.AppendLine($"{nome};ok;{bmp.PixelWidth};{bmp.PixelHeight};{png}");
            }
            catch (Exception ex)
            {
                falhas++;
                var causa = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                Console.WriteLine($"FAIL {nome,-34} {causa.GetType().Name}: {causa.Message}");
                indice.AppendLine($"{nome};fail;0;0;{causa.GetType().Name}: {causa.Message}");
            }
            finally
            {
                try { janela?.Close(); } catch { }
            }
        }

        File.WriteAllText(Path.Combine(_outDir, "_index.csv"), indice.ToString(), new UTF8Encoding(true));

        Console.WriteLine();
        Console.WriteLine($"=== {ok} capturada(s), {falhas} falha(s) — PNGs em {Path.GetFullPath(_outDir)} ===");
        return falhas == 0 ? 0 : 2;
    }

    private static List<Type> DescobrirPaginas()
    {
        var assembly = typeof(KitLugia.GUI.Pages.NetworkPage).Assembly;
        return assembly.GetTypes()
            .Where(t => t.IsPublic && !t.IsAbstract && typeof(Page).IsAssignableFrom(t))
            .Where(t => t.Namespace != null && t.Namespace.StartsWith("KitLugia.GUI.Pages", StringComparison.Ordinal))
            .Where(t => t.GetConstructors().Any(c => c.GetParameters().All(p => p.IsOptional)))
            .Where(t => Filtro(t.Name))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
    }

    private static bool Filtro(string nome)
    {
        if (string.IsNullOrWhiteSpace(_only)) return true;
        foreach (var termo in _only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (nome.Contains(termo, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static RenderTargetBitmap Render(FrameworkElement raiz)
    {
        raiz.UpdateLayout();
        double w = raiz.ActualWidth > 1 ? raiz.ActualWidth : _w;
        double h = raiz.ActualHeight > 1 ? raiz.ActualHeight : _h;
        int pw = Math.Max(1, (int)Math.Ceiling(w * Escala));
        int ph = Math.Max(1, (int)Math.Ceiling(h * Escala));

        var bmp = new RenderTargetBitmap(pw, ph, _dpi, _dpi, PixelFormats.Pbgra32);
        var fundo = new DrawingVisual();
        using (var dc = fundo.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)), null, new Rect(0, 0, w, h));
        bmp.Render(fundo);
        bmp.Render(raiz);
        bmp.Freeze();
        return bmp;
    }

    private static void SalvarPng(BitmapSource bmp, string caminho)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(caminho);
        enc.Save(fs);
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
}
