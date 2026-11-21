using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace StreamHub.Host;

internal static class AppMotion
{
    internal static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(130);
    internal static readonly TimeSpan Page = TimeSpan.FromMilliseconds(140);
    internal static readonly TimeSpan Subpage = TimeSpan.FromMilliseconds(180);
    internal static readonly TimeSpan Panel = TimeSpan.FromMilliseconds(140);
    internal static readonly TimeSpan Expand = TimeSpan.FromMilliseconds(160);
    internal static readonly TimeSpan Theme = TimeSpan.FromMilliseconds(180);

    internal static bool IsEnabled(bool userReduced) =>
        !userReduced && SystemParameters.ClientAreaAnimation && SystemParameters.MenuAnimation;

    internal static CubicEase EaseOut() => new() { EasingMode = EasingMode.EaseOut };

    internal static void AnimatePopup(FrameworkElement element, bool userReduced)
    {
        element.RenderTransformOrigin = new System.Windows.Point(0.5, 0);
        element.RenderTransform = new ScaleTransform(1, 1);
        element.Opacity = 1;
        if (!IsEnabled(userReduced)) return;

        if (element.RenderTransform is not ScaleTransform scale) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Panel)
        {
            EasingFunction = EaseOut(),
            FillBehavior = FillBehavior.Stop
        });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, Panel)
        {
            EasingFunction = EaseOut(),
            FillBehavior = FillBehavior.Stop
        });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, Panel)
        {
            EasingFunction = EaseOut(),
            FillBehavior = FillBehavior.Stop
        });
    }

    internal static void AnimateExpander(FrameworkElement body, bool expanded, bool userReduced)
    {
        const double expandedHeight = 4096;
        body.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
        body.BeginAnimation(UIElement.OpacityProperty, null);
        body.IsHitTestVisible = expanded;
        if (!IsEnabled(userReduced))
        {
            body.MaxHeight = expanded ? expandedHeight : 0;
            body.Opacity = expanded ? 1 : 0;
            return;
        }

        if (expanded)
        {
            body.MaxHeight = expandedHeight;
            body.Opacity = 1;
            body.BeginAnimation(FrameworkElement.MaxHeightProperty, new DoubleAnimation(0, expandedHeight, Expand)
            {
                EasingFunction = EaseOut(),
                FillBehavior = FillBehavior.Stop
            });
            body.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Expand)
            {
                EasingFunction = EaseOut(),
                FillBehavior = FillBehavior.Stop
            });
            return;
        }

        double currentHeight = Math.Max(1, body.ActualHeight);
        body.MaxHeight = 0;
        body.Opacity = 0;
        body.BeginAnimation(FrameworkElement.MaxHeightProperty, new DoubleAnimation(currentHeight, 0, Expand)
        {
            EasingFunction = EaseOut(),
            FillBehavior = FillBehavior.Stop
        });
        body.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, Expand)
        {
            EasingFunction = EaseOut(),
            FillBehavior = FillBehavior.Stop
        });
    }

    internal static void AnimateThemeSurface(FrameworkElement element, bool userReduced)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        if (!IsEnabled(userReduced)) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.92, 1, Theme)
        {
            EasingFunction = EaseOut(),
            FillBehavior = FillBehavior.Stop
        });
    }
}
