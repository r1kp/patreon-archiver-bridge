using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace PatreonArchiverBridge.UI.Shared
{
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
            if (sender is ScrollViewer sv)
            {
                e.Handled = true;
                
                // Smoothly animate scroll offset
                double currentOffset = sv.VerticalOffset;
                double target = currentOffset - (e.Delta * 0.7); // 0.7 scales standard line scroll distance for premium feel
                
                if (target < 0) target = 0;
                if (target > sv.ScrollableHeight) target = sv.ScrollableHeight;

                var anim = new DoubleAnimation
                {
                    From = currentOffset,
                    To = target,
                    Duration = TimeSpan.FromMilliseconds(250),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                sv.BeginAnimation(AnimatedOffsetProperty, anim);
            }
        }
    }
}
