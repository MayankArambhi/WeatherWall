using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WeatherWall
{
    public static class SmoothScrollBehavior
    {
        public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
            "Enable", typeof(bool), typeof(SmoothScrollBehavior), new PropertyMetadata(false, OnEnableChanged));

        public static void SetEnable(DependencyObject element, bool value) => element.SetValue(EnableProperty, value);
        public static bool GetEnable(DependencyObject element) => (bool)element.GetValue(EnableProperty);

        private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is System.Windows.Controls.ListBox lb)
            {
                lb.Loaded += ListBox_Loaded;
                lb.Unloaded += ListBox_Unloaded;
            }
            else if (d is ScrollViewer sv)
            {
                Attach(sv);
            }
        }

        private static void ListBox_Loaded(object? sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.ListBox lb)
            {
                var sv = FindDescendant<ScrollViewer>(lb);
                if (sv != null) Attach(sv);
            }
        }

        private static void ListBox_Unloaded(object? sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.ListBox lb)
            {
                var sv = FindDescendant<ScrollViewer>(lb);
                if (sv != null) Detach(sv);
            }
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t) return t;
                var sub = FindDescendant<T>(child);
                if (sub != null) return sub;
            }
            return null;
        }

        private static readonly DependencyProperty AnimatorProperty = DependencyProperty.RegisterAttached(
            "Animator", typeof(SmoothAnimator), typeof(SmoothScrollBehavior), new PropertyMetadata(null));

        private static void Attach(ScrollViewer sv)
        {
            var anim = new SmoothAnimator(sv);
            sv.SetValue(AnimatorProperty, anim);
        }

        private static void Detach(ScrollViewer sv)
        {
            var anim = sv.GetValue(AnimatorProperty) as SmoothAnimator;
            if (anim != null) anim.Dispose();
            sv.ClearValue(AnimatorProperty);
        }

        private sealed class SmoothAnimator : IDisposable
        {
            private readonly ScrollViewer _sv;
            private double _startOffset;
            private double _targetOffset;
            private DateTime _startTime;
            private TimeSpan _duration = TimeSpan.FromMilliseconds(160);
            private bool _animating = false;

            public SmoothAnimator(ScrollViewer sv)
            {
                _sv = sv;
                _sv.PreviewMouseWheel += OnMouseWheel;
            }

            private void OnMouseWheel(object? sender, MouseWheelEventArgs e)
            {
                // Intercept and perform frame-synced smooth scroll
                e.Handled = true;

                double delta = -e.Delta; // MouseWheel delta is reversed
                // Normalize: 120 units per notch. Use a scale for comfortable speed.
                double pixels = (delta / 120.0) * 48.0; // 48 pixels per notch

                _startOffset = _sv.VerticalOffset;
                _targetOffset = Math.Max(0, Math.Min(_sv.ScrollableHeight, _startOffset + pixels));
                _startTime = DateTime.Now;
                _animating = true;

                // Indicate scrolling to other systems (thumbnail loader)
                ThumbnailProvider.SuspendDecodingDuringScroll = true;

                CompositionTarget.Rendering -= OnRendering;
                CompositionTarget.Rendering += OnRendering;
            }

            private void OnRendering(object? sender, EventArgs e)
            {
                if (!_animating)
                {
                    CompositionTarget.Rendering -= OnRendering;
                    ThumbnailProvider.SuspendDecodingDuringScroll = false;
                    return;
                }

                var elapsed = DateTime.Now - _startTime;
                double t = Math.Min(1.0, elapsed.TotalMilliseconds / _duration.TotalMilliseconds);
                // Cubic ease out
                double eased = 1 - Math.Pow(1 - t, 3);
                double value = _startOffset + (_targetOffset - _startOffset) * eased;
                try { _sv.ScrollToVerticalOffset(value); } catch { }

                if (t >= 1.0)
                {
                    _animating = false;
                    // allow one more frame to settle then clear suspend flag
                    ThumbnailProvider.SuspendDecodingDuringScroll = false;
                    CompositionTarget.Rendering -= OnRendering;
                }
            }

            public void Dispose()
            {
                _sv.PreviewMouseWheel -= OnMouseWheel;
                CompositionTarget.Rendering -= OnRendering;
            }
        }
    }
}
