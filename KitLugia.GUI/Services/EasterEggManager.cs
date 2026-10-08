using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Logger = KitLugia.Core.Logger;
// O projeto usa WPF + WinForms (UseWindowsForms): sem aliases, Panel/Brush/TextBox
// ficam ambíguos entre System.Windows.* e System.Windows.Forms.*.
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace KitLugia.GUI.Services
{
    /// <summary>
    /// Easter egg no estilo YouTube: digitar "awesome" em QUALQUER campo de texto do Kit
    /// faz todas as janelas abertas ficarem com as bordas piscando em modo colorido
    /// (arco-íris animado + pulso), por alguns segundos.
    ///
    /// Como funciona:
    ///  - Um class handler global de TextBoxBase.TextChanged observa digitação, colar e
    ///    texto setado por código, em qualquer janela (MainWindow, Kit TaskManager, loja).
    ///  - Quando o texto TERMINA em "awesome" (sem diferenciar maiúsculas) dispara a festa:
    ///    cria um overlay de borda (Border sem conteúdo) por cima de tudo, com gradiente
    ///    arco-íris cujas cores ciclam em durações diferentes + pulso de opacidade.
    ///  - Ao fim do tempo (ou ao re-disparar), as animações param, a borda some com fade
    ///    e é removida da árvore visual — nada fica "sujo" para o próximo uso.
    ///
    /// Anti-duplo: 1,2 s de cooldown (evita re-trigger por re-render do mesmo campo).
    /// </summary>
    internal static class EasterEggManager
    {
        private const string Trigger = "awesome";
        private static readonly TimeSpan PartyDuration = TimeSpan.FromSeconds(12);
        private const int MaxWindows = 8;
        private const int GradientStopCount = 7;

        private static bool _started;
        private static DateTime _lastTriggerUtc = DateTime.MinValue;
        private static readonly Dictionary<Window, PartyState> _parties = new Dictionary<Window, PartyState>();
        private static readonly HashSet<Window> _closeHooked = new HashSet<Window>();

        private sealed class PartyState
        {
            public List<Border> Borders { get; } = new List<Border>();
            public List<GradientStop> Stops { get; } = new List<GradientStop>();
            public DispatcherTimer? Timer { get; set; }
        }

        /// <summary>Registra o detector global (idempotente). Chamar 1x no startup.</summary>
        public static void Start()
        {
            if (_started) return;
            _started = true;

            // Handler de CLASSE global: dispara para todo TextBoxBase do app (TextBox e
            // PasswordBox), em todas as janelas, sem precisar tocar em cada página.
            EventManager.RegisterClassHandler(typeof(TextBoxBase), TextBoxBase.TextChangedEvent,
                new TextChangedEventHandler(OnAnyTextChanged));

            Logger.Log("[EASTER EGG] Ativo - digite 'awesome' em qualquer campo do Kit.");
        }

        /// <summary>Dispara manualmente (usado em teste/QA, sem digitar).</summary>
        public static void Play() => TriggerParty();

        // ---------------------------------------------------------------- deteccao

        private static void OnAnyTextChanged(object sender, TextChangedEventArgs e)
        {
            // Só TextBox tem texto legível (PasswordBox mascara; não interessa aqui).
            if (sender is not TextBox tb) return;

            string text;
            try { text = tb.Text ?? string.Empty; }
            catch { return; }

            if (text.Length < Trigger.Length) return;
            if (!text.EndsWith(Trigger, StringComparison.OrdinalIgnoreCase)) return;

            var now = DateTime.UtcNow;
            if ((now - _lastTriggerUtc).TotalMilliseconds < 1200) return;
            _lastTriggerUtc = now;

            TriggerParty();
        }

        private static void TriggerParty()
        {
            Logger.Log("[EASTER EGG] 'awesome' detectado - bordas em modo arco-iris!");

            try
            {
                var windows = Application.Current.Windows
                    .OfType<Window>()
                    .Where(w => w.IsVisible && w.IsLoaded)
                    .Take(MaxWindows)
                    .ToList();

                foreach (var w in windows) StartParty(w);

                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("🎉 AWESOME!", "Voce descobriu o easter egg do Kit Lugia! Bordas em modo colorido ativadas.");
            }
            catch (Exception ex)
            {
                Logger.Log($"[EASTER EGG] Erro ao ativar a festa: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------- visual

        private static void StartParty(Window w)
        {
            // Re-disparo durante a festa = estende o tempo (como no YouTube).
            if (_parties.TryGetValue(w, out var existing))
            {
                RestartTimer(w, existing);
                return;
            }

            var panel = FindPanel(w);
            if (panel == null)
            {
                Logger.Log($"[EASTER EGG] Sem Panel na janela '{w.Name}' - bordas ignoradas.");
                return;
            }

            var state = new PartyState();
            _parties[w] = state;

            var brush = BuildRainbowBrush(state);
            state.Borders.Add(AddFrame(panel, brush, 10, pulsePeriodMs: 420, minOpacity: 0.05));
            state.Borders.Add(AddFrame(panel, brush, 3, pulsePeriodMs: 260, minOpacity: 0.35, margin: 7));

            // Hook de Closed 1x por janela (senão cada nova festa empilha um handler).
            if (_closeHooked.Add(w))
            {
                w.Closed += (_, __) =>
                {
                    // Janela fechou no meio da festa: descarta a entrada, sem timer órfão.
                    if (_parties.Remove(w)) state.Timer?.Stop();
                    _closeHooked.Remove(w);
                };
            }

            RestartTimer(w, state);
        }

        private static void RestartTimer(Window w, PartyState state)
        {
            state.Timer?.Stop();

            var timer = new DispatcherTimer { Interval = PartyDuration };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                StopParty(w);
            };
            state.Timer = timer;
            timer.Start();
        }

        private static LinearGradientBrush BuildRainbowBrush(PartyState state)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };

            var palette = new Color[GradientStopCount];
            for (int i = 0; i < GradientStopCount; i++)
                palette[i] = FromHue(360.0 * i / GradientStopCount, 0.95);

            for (int i = 0; i < GradientStopCount; i++)
            {
                var stop = new GradientStop(palette[i], i / (double)(GradientStopCount - 1));
                brush.GradientStops.Add(stop);
                state.Stops.Add(stop);

                // Cada stop cicla para a PRÓXIMA cor com período diferente -> o arco-íris
                // "anda" pela borda em vez de piscar uniforme.
                stop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation
                {
                    From = palette[i],
                    To = palette[(i + 1) % GradientStopCount],
                    Duration = TimeSpan.FromMilliseconds(180 + i * 110),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                });
            }

            return brush;
        }

        /// <summary>HSL -> RGB (WPF Color não tem FromHsv; conversionária própria, sem deps).</summary>
        private static Color FromHue(double hue, double saturation, double value = 1.0)
        {
            double c = value * saturation;
            double hp = (hue % 360) / 60.0;
            double x = c * (1 - Math.Abs(hp % 2 - 1));
            double r1, g1, b1;

            if (hp < 1) { r1 = c; g1 = x; b1 = 0; }
            else if (hp < 2) { r1 = x; g1 = c; b1 = 0; }
            else if (hp < 3) { r1 = 0; g1 = c; b1 = x; }
            else if (hp < 4) { r1 = 0; g1 = x; b1 = c; }
            else if (hp < 5) { r1 = x; g1 = 0; b1 = c; }
            else { r1 = c; g1 = 0; b1 = x; }

            double m = value - c;
            return Color.FromRgb(
                (byte)Math.Round((r1 + m) * 255),
                (byte)Math.Round((g1 + m) * 255),
                (byte)Math.Round((b1 + m) * 255));
        }

        private static Border AddFrame(Panel panel, Brush brush, double thickness,
                                       int pulsePeriodMs, double minOpacity, double margin = 0)
        {
            var frame = new Border
            {
                BorderBrush = brush,
                BorderThickness = new Thickness(thickness),
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(margin),
                IsHitTestVisible = false,
                Focusable = false,
                Opacity = minOpacity
            };
            Panel.SetZIndex(frame, 99999);

            // Cobre o painel INTEIRO (o Grid da janela tem linha extra para o console).
            if (panel is Grid grid)
            {
                Grid.SetRowSpan(frame, Math.Max(1, grid.RowDefinitions.Count));
                Grid.SetColumnSpan(frame, Math.Max(1, grid.ColumnDefinitions.Count));
            }

            panel.Children.Add(frame);

            frame.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
            {
                From = minOpacity,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(pulsePeriodMs),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });

            return frame;
        }

        private static void StopParty(Window w)
        {
            if (!_parties.TryGetValue(w, out var state)) return;
            _parties.Remove(w);
            state.Timer?.Stop();

            try
            {
                foreach (var stop in state.Stops)
                    stop.BeginAnimation(GradientStop.ColorProperty, null);

                foreach (var frame in state.Borders)
                {
                    frame.BeginAnimation(UIElement.OpacityProperty, null);

                    var fade = new DoubleAnimation
                    {
                        To = 0.0,
                        Duration = TimeSpan.FromMilliseconds(450)
                    };
                    var target = frame;
                    fade.Completed += (_, __) =>
                    {
                        if (target.Parent is Panel p) p.Children.Remove(target);
                    };
                    frame.BeginAnimation(UIElement.OpacityProperty, fade);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[EASTER EGG] Erro ao encerrar a festa: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ utils

        private static Panel? FindPanel(DependencyObject root)
        {
            if (root is Panel p) return p;

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var found = FindPanel(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }

            return null;
        }
    }
}