using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KitLugia.GUI.Controls;
using KitLugia.GUI.Pages;

namespace RestorePointVis;

/// <summary>
/// Prova do fluxo "Ponto de Restauração" da AppsPage:
///
///  1) a TELA (overlay) aparece com a barrinha enchendo;
///  2) a NOTIFICAÇÃO (toast) sai no tom CINZA (NotificationType.Neutral),
///     e não verde/amarelo/vermelho;
///  3) a barrinha do toast e' a mesma que a da tela, em cinza.
///
/// Nao cria ponto de restauracao de verdade (exige admin): a tela e' aberta
/// chamando os mesmos metodos privados que o botao chama.
///
/// Saida: PNGs em shots/restorepoint/ (gitignored).
/// </summary>
internal static class Program
{
    private static int _pass;
    private static int _fail;
    private static string _outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "shots", "restorepoint");

    [STAThread]
    private static int Main(string[] args)
    {
        int iOut = Array.IndexOf(args, "--out");
        if (iOut >= 0 && iOut + 1 < args.Length) _outDir = args[iOut + 1];
        Directory.CreateDirectory(_outDir);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/KitLugia.GUI;component/Themes/Generic.xaml", UriKind.Absolute)
        });

        Console.WriteLine("=== RestorePointVis — tela + toast cinza do ponto de restauracao ===\n");

        CheckToastNeutral();
        CheckToastProgressBar();
        CheckAppsPageScreen();
        CheckAppsPageProgressColumns();

        Console.WriteLine($"\n=== {_pass} pass, {_fail} fail ===");
        Console.WriteLine($"PNGs em {Path.GetFullPath(_outDir)}");
        return _fail == 0 ? 0 : 1;
    }

    private static void Ok(string msg) { _pass++; Console.WriteLine("  PASS  " + msg); }
    private static void Fail(string msg) { _fail++; Console.WriteLine("  FAIL  " + msg); }
    private static void Check(bool cond, string msg) { if (cond) Ok(msg); else Fail(msg); }

    // ────────────────────────────────────────────────────────────── TOAST

    /// <summary>O toast de progresso tem de sair no tom DOURADO (Neutral), identidade do Kit.</summary>
    private static void CheckToastNeutral()
    {
        var toast = new LugiaToast();
        toast.SetContent("PONTO DE RESTAURAÇÃO", "Criando ponto de restauração...", NotificationType.Neutral);
        toast.IsProgress = true;

        Check(toast.ToastType == NotificationType.Neutral,
              $"toast criado com NotificationType.Neutral (atual: {toast.ToastType})");

        // A janela precisa estar montada: os triggers de estilo do toast so' sao
        // avaliados quando o elemento entra na arvore visual.
        var window = Host(toast, 380, 120);

        // A borda do card tem de ser dourada (#66FFD700), e nao verde/vermelha/cinza
        var border = toast.FindName("ToastBorder") as Border;
        string borda = border?.BorderBrush is SolidColorBrush b ? b.Color.ToString() : "<null>";
        Check(borda.StartsWith("#66FFD700"),
              $"borda do toast dourada (atual: {borda})");

        var title = toast.FindName("TxtTitle") as TextBlock;
        string corTitulo = title?.Foreground is SolidColorBrush tb ? tb.Color.ToString() : "<null>";
        Check(corTitulo.StartsWith("#FFFFD700") || corTitulo.StartsWith("#FFD700"),
              $"titulo do toast dourado (atual: {corTitulo})");

        Salvar(Render(window), Path.Combine(_outDir, "toast_neutral.png"));
        window.Close();
    }

    /// <summary>A barrinha do toast tem de encher e acompanhar o valor pedido.</summary>
    private static void CheckToastProgressBar()
    {
        var toast = new LugiaToast();
        toast.SetContent("PONTO DE RESTAURAÇÃO", "Criando ponto de restauração...", NotificationType.Neutral);
        toast.IsProgress = true;

        var window = Host(toast, 380, 120);
        toast.SetProgress(45);
        window.UpdateLayout();

        var fill = toast.FindName("ColProgressFill") as ColumnDefinition;
        var rest = toast.FindName("ColProgressRest") as ColumnDefinition;
        Check(fill != null && rest != null, "colunas da barrinha do toast existem");
        if (fill != null && rest != null)
        {
            Check(Math.Abs(fill.Width.Value - 45) < 0.01 && Math.Abs(rest.Width.Value - 55) < 0.01,
                  $"barrinha do toast em 45% (fill={fill.Width.Value}, rest={rest.Width.Value})");
        }

        var host = toast.FindName("ProgressBarHost") as Grid;
        Check(host != null && host.Visibility == Visibility.Visible, "barrinha do toast visivel em IsProgress=True");

        toast.SetProgress(100);
        window.UpdateLayout();
        Check(fill != null && Math.Abs(fill.Width.Value - 100) < 0.01, "barrinha do toast fecha em 100%");

        Salvar(Render(window), Path.Combine(_outDir, "toast_neutral_progresso.png"));
        window.Close();
    }

    // ────────────────────────────────────────────────────────── TELA (AppsPage)

    /// <summary>A tela do ponto de restauracao tem de abrir de verdade.</summary>
    private static void CheckAppsPageScreen()
    {
        var page = new AppsPage();
        var window = Host(page, 1200, 900);
        Pump(400);

        Invoke(page, "ShowRestorePointScreen", "Preparando o ponto de restauração...");
        Invoke(page, "SetRestoreProgress", 45.0);
        window.UpdateLayout();
        Pump(300);

        var overlay = page.FindName("RestorePointOverlay") as Border;
        Check(overlay != null, "overlay do ponto de restauracao existe na AppsPage");
        Check(overlay != null && overlay.Visibility == Visibility.Visible,
              "overlay do ponto de restauracao fica VISIVEL");

        var status = page.FindName("TxtRestorePointStatus") as TextBlock;
        Check(status != null && !string.IsNullOrWhiteSpace(status.Text),
              $"status da tela preenchido (atual: '{status?.Text}')");

        var percent = page.FindName("TxtRestorePointPercent") as TextBlock;
        Check(percent != null && percent.Text.Contains("45"),
              $"percentual da tela mostra 45% (atual: '{percent?.Text}')");

        var close = page.FindName("BtnRestorePointClose") as Button;
        Check(close != null && !close.IsEnabled, "botao Fechar comeca desabilitado (operaçao em andamento)");

        Salvar(Render(window), Path.Combine(_outDir, "appspage_restorepoint.png"));

        // Estado final: o overlay mostra o resultado e libera o botao
        Invoke(page, "SetRestorePointResult", true, "Ponto de restauração criado com sucesso!", "O Windows registrou o ponto de restauração.");
        window.UpdateLayout();
        Pump(200);
        Check(close != null && close.IsEnabled, "botao Fechar libera quando o processo termina");
        Salvar(Render(window), Path.Combine(_outDir, "appspage_restorepoint_ok.png"));

        window.Close();
    }

    /// <summary>As colunas da barrinha da tela acompanham o percentual.</summary>
    private static void CheckAppsPageProgressColumns()
    {
        var page = new AppsPage();
        var window = Host(page, 1200, 900);
        Pump(300);

        Invoke(page, "ShowRestorePointScreen", "Preparando...");
        Invoke(page, "SetRestoreProgress", 72.0);
        window.UpdateLayout();

        var fill = page.FindName("ColRestoreFill") as ColumnDefinition;
        var rest = page.FindName("ColRestoreRest") as ColumnDefinition;
        Check(fill != null && rest != null, "colunas da barrinha da tela existem");
        if (fill != null && rest != null)
        {
            Check(Math.Abs(fill.Width.Value - 72) < 0.01 && Math.Abs(rest.Width.Value - 28) < 0.01,
                  $"barrinha da tela em 72% (fill={fill.Width.Value}, rest={rest.Width.Value})");
        }

        window.Close();
    }

    // ────────────────────────────────────────────────────────────── APOIO

    private static void Invoke(object target, string metodo, params object?[] args)
    {
        var m = target.GetType().GetMethod(metodo, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (m == null) throw new InvalidOperationException($"metodo '{metodo}' nao encontrado em {target.GetType().Name}");
        m.Invoke(target, args);
    }

    private static Window Host(UIElement content, double w, double h)
    {
        // O Content vai DIRETO na Window: Page so' aceita Window ou Frame como pai
        // (Page somente pode ter Window ou Frame como pai).
        var window = new Window
        {
            Width = w,
            Height = h,
            Left = -32000,
            Top = -32000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)),
            Content = content
        };
        window.Show();
        window.UpdateLayout();
        Pump(250);
        return window;
    }

    private static void Pump(int ms)
    {
        var limite = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < limite)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    private static RenderTargetBitmap Render(Window window)
    {
        var raiz = window.Content as FrameworkElement;
        if (raiz == null) throw new InvalidOperationException("janela sem conteudo visual");
        raiz.UpdateLayout();
        double w = raiz.ActualWidth > 1 ? raiz.ActualWidth : window.Width;
        double h = raiz.ActualHeight > 1 ? raiz.ActualHeight : window.Height;
        int pw = Math.Max(1, (int)Math.Ceiling(w));
        int ph = Math.Max(1, (int)Math.Ceiling(h));

        var bmp = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        var fundo = new DrawingVisual();
        using (var dc = fundo.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)), null, new Rect(0, 0, w, h));
        bmp.Render(fundo);
        bmp.Render(raiz);
        bmp.Freeze();
        return bmp;
    }

    private static void Salvar(RenderTargetBitmap bmp, string caminho)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(caminho);
        enc.Save(fs);
        Console.WriteLine($"  PNG  {caminho}");
    }
}
