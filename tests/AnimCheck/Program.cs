using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using KitLugia.GUI.Controls;

namespace AnimCheck;

/// <summary>
/// Prova que o realce de hover voltou a ANIMAR (nao so' mudar de cor).
///
/// Um PNG nao mostra hover, e "ter storyboard no estilo" nao prova nada: o
/// ScaleTransform vindo de um Setter de Style chega congelado e a animacao vira
/// no-op silencioso (foi exatamente o que esta sonda descobriu). Por isso ela
/// aplica o estilo real num Border, dispara MouseEnter de verdade, bombeia o
/// Dispatcher e LE a escala.
/// </summary>
internal static class Program
{
    private static int _fail;
    private static int _pass;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/KitLugia.GUI;component/Themes/Generic.xaml", UriKind.Absolute)
        });

        Console.WriteLine("=== AnimCheck — hover dos cards e dos botoes ===\n");

        CheckCardStatic();
        CheckCardStyle("InteractiveCardStyle", 1.01);
        CheckCardStyle("SubtleCardStyle", 1.01);
        CheckIconHighlight();
        CheckPumpSanity();
        CheckNoShadow();
        CheckTemplateHover();

        Console.WriteLine($"\n=== {_pass} pass, {_fail} fail ===");
        return _fail == 0 ? 0 : 1;
    }

    private static void Ok(string msg) { _pass++; Console.WriteLine("  PASS  " + msg); }
    private static void Fail(string msg) { _fail++; Console.WriteLine("  FAIL  " + msg); }
    private static void Check(bool cond, string msg) { if (cond) Ok(msg); else Fail(msg); }

    private static T Res<T>(string key) where T : class
    {
        if (Application.Current.TryFindResource(key) is T value)
            return value;
        throw new InvalidOperationException($"recurso '{key}' nao encontrado no tema");
    }

    private static Window Host(UIElement content, double w = 420, double h = 260)
    {
        var window = new Window
        {
            Width = w, Height = h, Left = -32000, Top = -32000,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            Content = new Grid { Children = { content } }
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static void Hover(DependencyObject element, bool entering)
    {
        var args = new MouseEventArgs(Mouse.PrimaryDevice, 0)
        {
            RoutedEvent = entering ? Mouse.MouseEnterEvent : Mouse.MouseLeaveEvent
        };
        ((UIElement)element).RaiseEvent(args);
    }

    // Card de conteudo (o bloco inteiro de uma secao): realce so' de cor. Nem
    // zoom (empurrava a borda para fora do lugar num card de 700 px) nem
    // Cursor=Hand (a mao de clique era herdada por todo texto dentro dele).
    private static void CheckCardStatic()
    {
        Console.WriteLine("[CardStyle — card de conteudo]");
        var style = Res<Style>("CardStyle");
        bool hasZoom = false, hasHand = false;
        foreach (Setter s in style.Setters)
        {
            if (s.Property == HoverZoom.EnabledProperty && s.Value is bool b && b) hasZoom = true;
            if (s.Property == FrameworkElement.CursorProperty && s.Value is Cursor c && c == Cursors.Hand) hasHand = true;
        }
        Check(!hasZoom, "CardStyle: sem zoom (card de conteudo nao cresce no hover)");
        Check(!hasHand, "CardStyle: sem Cursor=Hand (nao promete clique)");

        var border = new Border { Width = 300, Height = 120 };
        border.Style = style;
        var window = Host(border);

        Check(!HoverZoom.GetEnabled(border), "CardStyle: comportamento desligado no card real");
        Hover(border, true);
        Pump(400);
        double scale = border.RenderTransform is ScaleTransform t ? t.ScaleX : 1.0;
        Check(Math.Abs(scale - 1.0) < 0.001, $"CardStyle: hover NAO muda a escala (lido {scale:F3})");

        window.Close();
        Console.WriteLine();
    }

    // O realce do hover do botao de acao tem que preencher o botao inteiro: o
    // Padding morava no Border de fora, entao o brilho nascia 10 px para dentro
    // e as bordas esquerda/direita ficavam sem luz.
    private static void CheckIconHighlight()
    {
        Console.WriteLine("[IconTextButtonStyle — brilho do hover]");
        var button = new Button
        {
            Content = "Selecionar Todos",
            Width = 240,
            Height = 32,
            Style = Res<Style>("IconTextButtonStyle"),
            Tag = Res<Geometry>("IconSelectAll"),
            // mesma familia verde do "Selecionar Todos" da AppsPage
            Background = new SolidColorBrush(Color.FromArgb(0x1F, 0x4C, 0xAF, 0x50)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x4D, 0x4C, 0xAF, 0x50)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A))
        };
        var window = Host(button, 340, 120);
        button.ApplyTemplate();
        window.UpdateLayout();

        if (button.Template.FindName("hl", button) is not Border hl)
        {
            Fail("IconTextButtonStyle: realce 'hl' nao existe no template");
            window.Close();
            Console.WriteLine();
            return;
        }

        hl.Visibility = Visibility.Visible;
        window.UpdateLayout();

        double hInset = button.ActualWidth - hl.ActualWidth;
        double vInset = button.ActualHeight - hl.ActualHeight;
        Check(hInset <= 4.0, $"realce cobre a largura (botao {button.ActualWidth:F0} px, realce {hl.ActualWidth:F0} px, sobra {hInset:F0} px)");
        Check(vInset <= 4.0, $"realce cobre a altura (sobra {vInset:F0} px)");

        // Path: qualificado por causa do System.Windows.Shapes.Path (shapes), que
        // sombreia System.IO.Path neste arquivo.
        string png = System.IO.Path.Combine(AppContext.BaseDirectory, "icon_button_hover.png");
        SavePng(button, png, 3);
        Console.WriteLine("  PNG    " + png);

        window.Close();
        Console.WriteLine();
    }

    private static void CheckCardStyle(string key, double expectedScale)
    {
        Console.WriteLine($"[{key}]");
        Style style;
        try { style = Res<Style>(key); }
        catch (Exception ex) { Fail($"{key}: {ex.Message}"); return; }

        var border = new Border { Width = 300, Height = 120 };
        border.Style = style;
        var window = Host(border);

        Check(HoverZoom.GetEnabled(border), $"{key}: estilo liga o HoverZoom");
        Check(Math.Abs(HoverZoom.GetScale(border) - expectedScale) < 0.0001,
            $"{key}: escala do hover = {HoverZoom.GetScale(border):F3} (esperado {expectedScale:F3})");

        var transform = border.RenderTransform as ScaleTransform;
        Check(transform is not null, $"{key}: Border recebeu ScaleTransform");
        if (transform is null) { window.Close(); Console.WriteLine(); return; }
        Check(!transform.IsFrozen, $"{key}: transform MUTAVEL (o de Setter vem congelado e mata a animacao)");

        double rest = transform.ScaleX;
        Check(Math.Abs(rest - 1.0) < 0.001, $"{key}: repouso em 1.0 (lido {rest:F3})");

        try
        {
            Hover(border, true);
            Pump(600);
            double hovered = transform.ScaleX;
            Check(hovered > 1.004, $"{key}: MouseEnter cresce o card (1.0 -> {hovered:F3})");

            Hover(border, false);
            Pump(600);
            double back = transform.ScaleX;
            Check(Math.Abs(back - 1.0) < 0.002, $"{key}: MouseLeave devolve a 1.0 (lido {back:F3})");
        }
        catch (Exception ex)
        {
            Fail($"{key}: animacao estourou: {ex.GetType().Name}: {ex.Message}");
        }

        window.Close();
        Console.WriteLine();
    }

    // Controle do teste: um storyboard montado no codigo TEM que mexer a escala.
    // Se isto falhar, o problema e' o ambiente (clock parado), nao o tema.
    private static void CheckPumpSanity()
    {
        Console.WriteLine("[controle do teste]");
        var border = new Border { Width = 200, Height = 80, RenderTransformOrigin = new Point(0.5, 0.5) };
        border.RenderTransform = new ScaleTransform(1, 1);
        var window = Host(border);

        var anim = new System.Windows.Media.Animation.DoubleAnimation(1, 1.2, TimeSpan.FromMilliseconds(200));
        System.Windows.Media.Animation.Storyboard.SetTarget(anim, border);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(anim, new PropertyPath("RenderTransform.ScaleX"));
        var sb = new System.Windows.Media.Animation.Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
        Pump(400);

        double v = ((ScaleTransform)border.RenderTransform).ScaleX;
        Check(v > 1.05, $"controle: storyboard de codigo animou a escala (1.0 -> {v:F3})");
        window.Close();
        Console.WriteLine();
    }

    private static void CheckNoShadow()
    {
        Console.WriteLine("[sombra]");
        foreach (string key in new[] { "CardStyle", "SubtleCardStyle", "CompactCardStyle" })
        {
            var style = Res<Style>(key);
            bool hasEffect = false;
            foreach (Setter s in style.Setters)
                if (s.Property == UIElement.EffectProperty) hasEffect = true;
            Check(!hasEffect, $"{key}: sem DropShadowEffect (o custo que motivou a F3 continua fora)");
        }
        Console.WriteLine();
    }

    // Hover dos botoes: vale para o estilo GLOBAL (todo Button do app), o de acao
    // com icone e os cards do Dashboard (SettingsCardStyle).
    private static void CheckButtonHover(string label, Style? style, Geometry? icon)
    {
        var button = new Button { Content = "Selecionar Todos", Width = 240, Height = 40 };
        if (style is not null) button.Style = style;
        if (icon is not null) button.Tag = icon;
        var window = Host(button, 340, 160);

        Check(button.RenderTransform is ScaleTransform, $"{label}: botao recebeu ScaleTransform no load");
        if (button.RenderTransform is not ScaleTransform transform)
        {
            window.Close();
            return;
        }

        Check(!transform.IsFrozen, $"{label}: transform mutavel (o de template/Setter vem congelado)");

        Hover(button, true);
        Pump(600);
        double hovered = transform.ScaleX;
        Check(hovered > 1.004, $"{label}: MouseEnter cresce o botao (1.0 -> {hovered:F3})");

        Hover(button, false);
        Pump(600);
        double back = transform.ScaleX;
        Check(Math.Abs(back - 1.0) < 0.005, $"{label}: MouseLeave devolve a 1.0 (lido {back:F3})");
        window.Close();
    }

    private static void CheckTemplateHover()
    {
        Console.WriteLine("[hover dos botoes]");
        CheckButtonHover("estilo global (todo botao)", null, null);
        CheckButtonHover("IconTextButtonStyle", Res<Style>("IconTextButtonStyle"), Res<Geometry>("IconSelectAll"));
        CheckButtonHover("SettingsCardStyle (cards do Dashboard)", Res<Style>("SettingsCardStyle"), null);
        Console.WriteLine();
    }

    // Recorte ampliado: um print 1:1 nao mostra se o brilho encosta na borda.
    private static void SavePng(FrameworkElement element, string path, double zoom)
    {
        int w = (int)(element.ActualWidth * zoom);
        int h = (int)(element.ActualHeight * zoom);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)), null, new Rect(0, 0, w, h));
            dc.PushTransform(new ScaleTransform(zoom, zoom));
            dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                             null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            dc.Pop();
        }

        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(path);
        encoder.Save(fs);
    }

    private static void Pump(int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            var frame = new DispatcherFrame();
            // Prioridade Render: e' nela que o MediaContext (dono dos clocks de
            // animacao) roda. Com Background o valor animado ficava parado.
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
