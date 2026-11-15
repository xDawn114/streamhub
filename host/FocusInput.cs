using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using KeyEventHandler = System.Windows.Input.KeyEventHandler;

namespace StreamHub.Host;

/// <summary>Keeps keyboard focus visible without retaining a ring after pointer input.</summary>
public static class FocusInput
{
    public static readonly DependencyProperty KeyboardModeProperty = DependencyProperty.RegisterAttached(
        "KeyboardMode", typeof(bool), typeof(FocusInput),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetKeyboardMode(DependencyObject target) => (bool)target.GetValue(KeyboardModeProperty);
    public static void SetKeyboardMode(DependencyObject target, bool value) => target.SetValue(KeyboardModeProperty, value);

    public static readonly DependencyProperty TrackProperty = DependencyProperty.RegisterAttached(
        "Track", typeof(bool), typeof(FocusInput), new PropertyMetadata(false, TrackChanged));
    public static bool GetTrack(DependencyObject target) => (bool)target.GetValue(TrackProperty);
    public static void SetTrack(DependencyObject target, bool value) => target.SetValue(TrackProperty, value);

    private static void TrackChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not UIElement scope) return;
        if ((bool)args.NewValue)
        {
            scope.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(KeyDown), true);
            scope.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(PointerDown), true);
            scope.AddHandler(UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(TouchDown), true);
            scope.AddHandler(Stylus.PreviewStylusDownEvent, new StylusDownEventHandler(StylusDown), true);
        }
        else
        {
            scope.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(KeyDown));
            scope.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(PointerDown));
            scope.RemoveHandler(UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(TouchDown));
            scope.RemoveHandler(Stylus.PreviewStylusDownEvent, new StylusDownEventHandler(StylusDown));
            scope.ClearValue(KeyboardModeProperty);
        }
    }

    private static void KeyDown(object sender, KeyEventArgs args)
    {
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        if (key is Key.Tab or Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End)
            SetKeyboardMode((DependencyObject)sender, true);
    }
    private static void PointerDown(object sender, MouseButtonEventArgs args) => SetKeyboardMode((DependencyObject)sender, false);
    private static void TouchDown(object? sender, TouchEventArgs args) => SetKeyboardMode((DependencyObject)sender!, false);
    private static void StylusDown(object sender, StylusDownEventArgs args) => SetKeyboardMode((DependencyObject)sender, false);
}
