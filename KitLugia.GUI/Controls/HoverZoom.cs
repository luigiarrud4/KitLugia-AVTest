using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KitLugia.GUI.Controls;

/// <summary>
/// Realce de hover por escala, aplicavel a qualquer FrameworkElement por ESTILO.
///
/// Existe porque o caminho "de estilo" nao anima: um <c>ScaleTransform</c> vindo de
/// um Setter de Style chega ao elemento CONGELADO (o WPF congela Freezables ao selar
/// o estilo), e animar propriedade de Freezable congelado nao faz nada - sem erro,
/// sem movimento. Aqui o transform e' criado (ou descongelado) por elemento, entao a
/// animacao realmente roda.
///
/// Uso:
/// <code>
/// &lt;Setter Property="controls:HoverZoom.Enabled" Value="True"/&gt;
/// &lt;Setter Property="controls:HoverZoom.Scale" Value="1.015"/&gt;
/// &lt;Setter Property="controls:HoverZoom.DurationMs" Value="180"/&gt;
/// </code>
/// </summary>
public static class HoverZoom
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(HoverZoom), new PropertyMetadata(false, OnEnabledChanged));

    public static readonly DependencyProperty ScaleProperty = DependencyProperty.RegisterAttached(
        "Scale", typeof(double), typeof(HoverZoom), new PropertyMetadata(1.015));

    public static readonly DependencyProperty DurationMsProperty = DependencyProperty.RegisterAttached(
        "DurationMs", typeof(int), typeof(HoverZoom), new PropertyMetadata(180));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetScale(DependencyObject element, double value) => element.SetValue(ScaleProperty, value);
    public static double GetScale(DependencyObject element) => (double)element.GetValue(ScaleProperty);

    public static void SetDurationMs(DependencyObject element, int value) => element.SetValue(DurationMsProperty, value);
    public static int GetDurationMs(DependencyObject element) => (int)element.GetValue(DurationMsProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        element.MouseEnter -= OnMouseEnter;
        element.MouseLeave -= OnMouseLeave;

        if (e.NewValue is not true)
            return;

        EnsureTransform(element);
        element.MouseEnter += OnMouseEnter;
        element.MouseLeave += OnMouseLeave;
    }

    /// <summary>
    /// Garante um ScaleTransform MUTAVEL no elemento (o do estilo vem congelado).
    /// </summary>
    public static ScaleTransform EnsureTransform(FrameworkElement element)
    {
        // System.Windows qualificado: System.Drawing.Point tambem esta no escopo.
        element.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);

        if (element.RenderTransform is ScaleTransform existing && !existing.IsFrozen)
            return existing;

        var transform = new ScaleTransform(1, 1);
        element.RenderTransform = transform;
        return transform;
    }

    /// <summary>
    /// Anima a escala. Publico para o harness provar o efeito sem mouse de verdade.
    /// </summary>
    public static void Animate(FrameworkElement element, bool hovering)
    {
        var transform = EnsureTransform(element);
        int ms = Math.Max(0, GetDurationMs(element));
        double target = hovering ? GetScale(element) : 1.0;
        var duration = TimeSpan.FromMilliseconds(ms);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        transform.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(target, duration) { EasingFunction = ease });
        transform.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(target, duration) { EasingFunction = ease });
    }

    // System.Windows.Input qualificado: o projeto referencia WinForms e o nome
    // MouseEventArgs fica ambiguo (CS0104) - mesma armadilha do EmptyStateView.
    private static void OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement element)
            Animate(element, true);
    }

    private static void OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement element)
            Animate(element, false);
    }
}
