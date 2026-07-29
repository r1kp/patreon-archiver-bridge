using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace PatreonArchiverBridge.UI.Shared
{
    /// <summary>
    /// Weiches Mausrad-Scrollen fuer ScrollViewer (ersetzt Windows' ruckartiges
    /// Zeilen-Scrollen durch eine kurze Ease-Out-Bewegung).
    ///
    /// WICHTIG - warum diese Datei so aussieht, wie sie aussieht:
    /// Die erste Fassung hat bei JEDEM Mausrad-Ereignis eine komplett NEUE
    /// 250ms-Animation gestartet, deren Startwert (From) der GERADE LAUFENDE
    /// Offset war. Ein Mausrad liefert beim schnellen Scrollen aber viele
    /// Ereignisse pro Sekunde (ein Praezisions-Touchpad 30-60/s). Folgen:
    ///   1. Jedes Ereignis rechnete sein Ziel aus dem HINTERHERHINKENDEN
    ///      Ist-Offset statt aus dem bereits gesetzten Ziel - die Bewegungen
    ///      haben sich gegenseitig aufgefressen, statt sich zu addieren. Das
    ///      fuehlt sich genau wie das gemeldete "laggt/haengt" an: man dreht,
    ///      und die Seite kommt kaum vom Fleck bzw. zuckt.
    ///   2. Pro Ereignis wurden ein DoubleAnimation- und ein QuadraticEase-Objekt
    ///      neu erzeugt und eine neue Animations-Uhr registriert - hunderte pro
    ///      Sekunde, alle auf dem UI-Thread.
    /// Jetzt: das Ziel wird AUFADDIERT (solange die Ereignisse zu einer Geste
    /// gehoeren), die laufende Animation uebernimmt ihren Startwert selbst
    /// (kein From -> nahtloser Uebergang), und die Easing-Funktion ist EINMAL
    /// erzeugt und eingefroren (Freeze -> keine Change-Notifications mehr).
    /// </summary>
    public static class ScrollViewerBehavior
    {
        // Animated Offset dependency property
        public static readonly DependencyProperty AnimatedOffsetProperty =
            DependencyProperty.RegisterAttached("AnimatedOffset", typeof(double), typeof(ScrollViewerBehavior),
                new FrameworkPropertyMetadata(0.0, OnAnimatedOffsetChanged));

        public static double GetAnimatedOffset(DependencyObject obj) => (double)obj.GetValue(AnimatedOffsetProperty);
        public static void SetAnimatedOffset(DependencyObject obj, double value) => obj.SetValue(AnimatedOffsetProperty, value);

        private static void OnAnimatedOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer sv)
            {
                sv.ScrollToVerticalOffset((double)e.NewValue);
            }
        }

        // Zuletzt angesteuerter Ziel-Offset + Zeitpunkt des letzten Rad-Ticks.
        // Beides pro ScrollViewer, damit mehrere ScrollViewer im selben Fenster
        // sich nicht gegenseitig beeinflussen.
        private static readonly DependencyProperty TargetOffsetProperty =
            DependencyProperty.RegisterAttached("TargetOffset", typeof(double), typeof(ScrollViewerBehavior),
                new PropertyMetadata(0.0));

        private static readonly DependencyProperty LastWheelTickProperty =
            DependencyProperty.RegisterAttached("LastWheelTick", typeof(long), typeof(ScrollViewerBehavior),
                new PropertyMetadata(0L));

        // EINMAL erzeugt und eingefroren statt pro Ereignis neu (Freezable ohne
        // Freeze() haengt an der Change-Notification-Maschinerie).
        private static readonly IEasingFunction WheelEase = CreateFrozenEase();

        private static IEasingFunction CreateFrozenEase()
        {
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            ease.Freeze();
            return ease;
        }

        /// <summary>Scroll-Weg pro Rad-Einheit (120 = eine Raste).</summary>
        private const double WheelStepFactor = 0.7;

        /// <summary>Kurz genug, um direkt zu wirken, lang genug fuer weiche Bewegung.</summary>
        private const int WheelAnimationMs = 180;

        /// <summary>
        /// Rad-Ticks innerhalb dieses Fensters gelten als EINE fortgesetzte Geste
        /// und addieren sich auf das bestehende Ziel, statt es neu vom aktuellen
        /// (hinterherhinkenden) Ist-Offset zu berechnen.
        /// </summary>
        private const long WheelChainWindowMs = 500;

        // IsEnabled attached property to easily toggle smooth scroll in XAML
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(ScrollViewerBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer sv)
            {
                if ((bool)e.NewValue)
                {
                    sv.PreviewMouseWheel += ScrollViewer_PreviewMouseWheel;
                }
                else
                {
                    sv.PreviewMouseWheel -= ScrollViewer_PreviewMouseWheel;
                }
            }
        }

        private static void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;
            e.Handled = true;

            long now = Environment.TickCount64;
            long lastTick = (long)sv.GetValue(LastWheelTickProperty);
            bool continuesGesture = now - lastTick < WheelChainWindowMs;

            // Beim Fortsetzen einer Geste auf dem bereits gesetzten ZIEL
            // aufbauen, sonst auf dem tatsaechlichen Ist-Offset (der Nutzer kann
            // zwischendurch die Scrollbar gezogen oder die Seite gewechselt haben).
            double basis = continuesGesture ? (double)sv.GetValue(TargetOffsetProperty) : sv.VerticalOffset;

            double target = basis - (e.Delta * WheelStepFactor);
            if (target < 0) target = 0;
            if (target > sv.ScrollableHeight) target = sv.ScrollableHeight;

            sv.SetValue(TargetOffsetProperty, target);
            sv.SetValue(LastWheelTickProperty, now);

            // Schon am Ziel (z.B. am oberen/unteren Anschlag weitergedreht):
            // keine Animation starten, sonst laeuft pro Tick eine Uhr ins Leere.
            if (Math.Abs(target - sv.VerticalOffset) < 0.5)
            {
                sv.BeginAnimation(AnimatedOffsetProperty, null);
                sv.ScrollToVerticalOffset(target);
                return;
            }

            var anim = new DoubleAnimation
            {
                To = target,
                Duration = TimeSpan.FromMilliseconds(WheelAnimationMs),
                EasingFunction = WheelEase,
                FillBehavior = FillBehavior.HoldEnd
            };

            // Nur beim START einer Geste den Ausgangswert explizit setzen: die
            // Attached-Property kennt den echten Offset nicht, wenn zwischendurch
            // per Scrollbar/Tastatur gescrollt wurde. Waehrend einer laufenden
            // Geste bleibt From bewusst leer - dann startet die neue Animation
            // exakt beim aktuellen Animationswert und der Uebergang ist nahtlos.
            if (!continuesGesture)
            {
                anim.From = sv.VerticalOffset;
            }

            if (anim.CanFreeze) anim.Freeze();
            sv.BeginAnimation(AnimatedOffsetProperty, anim);
        }
    }
}
