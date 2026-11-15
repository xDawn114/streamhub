using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseEventHandler = System.Windows.Input.MouseEventHandler;

namespace StreamHub.Host;

public static class ScrollActivity
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ScrollActivity), new PropertyMetadata(false, EnabledChanged));
    public static readonly DependencyProperty ActiveProperty = DependencyProperty.RegisterAttached(
        "Active", typeof(bool), typeof(ScrollActivity), new FrameworkPropertyMetadata(false,
            FrameworkPropertyMetadataOptions.Inherits));
    public static readonly DependencyProperty PointerNearProperty = DependencyProperty.RegisterAttached(
        "PointerNear", typeof(bool), typeof(ScrollActivity), new FrameworkPropertyMetadata(false,
            FrameworkPropertyMetadataOptions.Inherits));
    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.RegisterAttached(
        "ReduceMotion", typeof(bool), typeof(ScrollActivity), new FrameworkPropertyMetadata(false,
            FrameworkPropertyMetadataOptions.Inherits));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(IDisposable), typeof(ScrollActivity));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static bool GetActive(DependencyObject target) => (bool)target.GetValue(ActiveProperty);
    public static bool GetPointerNear(DependencyObject target) => (bool)target.GetValue(PointerNearProperty);
    public static bool GetReduceMotion(DependencyObject target) => (bool)target.GetValue(ReduceMotionProperty);
    public static void SetReduceMotion(DependencyObject target, bool value) => target.SetValue(ReduceMotionProperty, value);

    private static void EnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        (target.GetValue(StateProperty) as IDisposable)?.Dispose();
        target.ClearValue(StateProperty);
        if (args.NewValue is not true) return;
        if (target is ScrollViewer viewer) target.SetValue(StateProperty, new ViewerActivity(viewer));
    }
    private sealed class ViewerActivity : IDisposable
    {
        private readonly ScrollViewer _viewer;
        private readonly DispatcherTimer _idle;
        public ViewerActivity(ScrollViewer viewer)
        {
            _viewer = viewer;
            _idle = new DispatcherTimer(DispatcherPriority.Background, viewer.Dispatcher) { Interval = TimeSpan.FromMilliseconds(1100) };
            _idle.Tick += Idle;
            viewer.ScrollChanged += Scrolled;
            viewer.PreviewMouseWheel += Wheel;
            viewer.AddHandler(Mouse.MouseMoveEvent, new MouseEventHandler(Moved), handledEventsToo: true);
            viewer.MouseLeave += Left;
            viewer.Unloaded += Unloaded;
            viewer.Loaded += Loaded;
        }
        private void Pulse()
        {
            _viewer.SetValue(ActiveProperty, true);
            _idle.Stop(); _idle.Start();
        }
        private void Scrolled(object sender, ScrollChangedEventArgs args)
        {
            if (ReferenceEquals(args.OriginalSource, _viewer) && (args.VerticalChange != 0 || args.HorizontalChange != 0)) Pulse();
        }
        private void Wheel(object sender, MouseWheelEventArgs args) => Pulse();
        private void Moved(object sender, MouseEventArgs args)
        {
            var point = args.GetPosition(_viewer);
            bool inside = point.X >= 0 && point.Y >= 0 && point.X <= _viewer.ActualWidth && point.Y <= _viewer.ActualHeight;
            bool near = inside && (_viewer.ComputedVerticalScrollBarVisibility == Visibility.Visible && point.X >= _viewer.ActualWidth - 24 ||
                _viewer.ComputedHorizontalScrollBarVisibility == Visibility.Visible && point.Y >= _viewer.ActualHeight - 24);
            // Hover persists until the pointer leaves. It must not expire with
            // the independent scroll-activity timer when the mouse stops moving.
            _viewer.SetValue(PointerNearProperty, near);
        }
        private void Loaded(object sender, RoutedEventArgs args) =>
            _viewer.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (_viewer.IsLoaded) Moved(_viewer, new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount));
            }));
        private void Left(object sender, MouseEventArgs args) => _viewer.SetValue(PointerNearProperty, false);
        private void Idle(object? sender, EventArgs args)
        {
            if (_viewer.IsMouseCaptureWithin) return;
            _idle.Stop();
            _viewer.SetValue(ActiveProperty, false);
        }
        private void Unloaded(object sender, RoutedEventArgs args)
        { _idle.Stop(); _viewer.SetValue(ActiveProperty, false); _viewer.SetValue(PointerNearProperty, false); }
        public void Dispose()
        {
            _idle.Stop(); _idle.Tick -= Idle;
            _viewer.ScrollChanged -= Scrolled; _viewer.PreviewMouseWheel -= Wheel;
            _viewer.RemoveHandler(Mouse.MouseMoveEvent, new MouseEventHandler(Moved));
            _viewer.MouseLeave -= Left; _viewer.Unloaded -= Unloaded; _viewer.Loaded -= Loaded;
            _viewer.SetValue(ActiveProperty, false);
            _viewer.SetValue(PointerNearProperty, false);
        }
    }
}
