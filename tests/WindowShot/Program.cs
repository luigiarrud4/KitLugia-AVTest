using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KitLugia.GUI.Controls;

namespace WindowShot;

/// <summary>
/// Captura a MainWindow REAL (o UiShotHarness so' alcanca as Pages) e prova o
/// hover da barra do topo: renderiza em repouso e com o zoom aplicado no botao
/// do Gerenciador de Tarefas.
///
/// Roda sem elevacao, entao as acoes privilegiadas do kit falham em vez de agir:
/// e' um launch curto (alguns segundos), nao o app em uso.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string outDir = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(outDir);

        int exit = 0;
        Window? window = null;
        try
        {
            var app = new KitLugia.GUI.App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            window = new KitLugia.GUI.MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false
            };
            window.Show();
            Pump(2500);

            // O splash de carregamento e' um overlay por cima do conteudo: sem
            // fecha-lo a captura sai so' com "KIT LUGIA".
            if (window.FindName("LoadingOverlay") is UIElement overlay)
            {
                overlay.Visibility = Visibility.Collapsed;
                Pump(400);
                Console.WriteLine("OK   splash fechado para a captura");
            }
            else
            {
                Console.WriteLine("AVISO LoadingOverlay nao encontrado");
            }

            string rest = Path.Combine(outDir, "mainwindow_rest.png");
            Save(window, rest);
            Console.WriteLine("OK   " + rest);

            // hover no botao do Gerenciador de Tarefas (barra do topo)
            var button = window.FindName("BtnKitTaskManager") as Button;
            if (button is null)
            {
                Console.WriteLine("FAIL botao BtnKitTaskManager nao encontrado");
                exit = 1;
            }
            else
            {
                // recorte 3x em volta do botao, para o zoom ficar visivel
                var content = (FrameworkElement)window.Content;
                var origin = button.TranslatePoint(new Point(0, 0), content);
                var rect = new Rect(origin.X - 14, origin.Y - 10,
                                    button.ActualWidth + 28, button.ActualHeight + 20);
                string cropRest = Path.Combine(outDir, "barra_repouso.png");
                SaveCrop(window, rect, cropRest);
                Console.WriteLine("OK   " + cropRest);

                var transform = HoverZoom.EnsureTransform(button);
                HoverZoom.Animate(button, true);
                Pump(600);
                double scale = transform.ScaleX;
                Console.WriteLine($"{(scale > 1.05 ? "OK  " : "FAIL")} hover da barra: {scale:F3} (esperado ~1.1)");
                if (scale <= 1.05) exit = 1;

                string zoomed = Path.Combine(outDir, "mainwindow_hover.png");
                Save(window, zoomed);
                Console.WriteLine("OK   " + zoomed);

                string cropHover = Path.Combine(outDir, "barra_hover.png");
                SaveCrop(window, rect, cropHover);
                Console.WriteLine("OK   " + cropHover);

                HoverZoom.Animate(button, false);
                Pump(500);
                Console.WriteLine($"{(Math.Abs(transform.ScaleX - 1.0) < 0.01 ? "OK  " : "FAIL")} volta ao repouso: {transform.ScaleX:F3}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            exit = 1;
        }
        finally
        {
            try { window?.Close(); } catch { }
            Application.Current?.Shutdown();
        }

        return exit;
    }

    private static void Save(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        int w = (int)Math.Max(1, content.ActualWidth);
        int h = (int)Math.Max(1, content.ActualHeight);

        // Renderizar so' o conteudo deixa TRANSPARENTE o que o fundo da janela
        // cobria (a tela sai com faixas brancas no visualizador). Pinta o
        // Background da Window antes.
        Brush? background = window.Background;
        if (background is null || background == Brushes.Transparent)
            background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(background, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, w, h));
        }

        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static void SaveCrop(Window window, Rect rect, string path, double zoom = 3)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        Brush background = window.Background ?? Brushes.Black;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(background, null, new Rect(0, 0, rect.Width * zoom, rect.Height * zoom));
            dc.PushTransform(new ScaleTransform(zoom, zoom));
            dc.PushTransform(new TranslateTransform(-rect.X, -rect.Y));
            dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                             null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            dc.Pop();
            dc.Pop();
        }

        var bmp = new RenderTargetBitmap((int)(rect.Width * zoom), (int)(rect.Height * zoom), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static void Pump(int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }
}
