using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace StreamHub.Host;

public partial class MainWindow
{
    private void ChromeSource_Initialized(object? sender, EventArgs args) => UpdateWindowCorners();
    private void ChromeSize_Changed(object sender, SizeChangedEventArgs args) => UpdateWindowCorners();

    private void UpdateWindowCorners()
    {
        if (WindowSurface is null) return;
        bool rounded = WindowState == WindowState.Normal;
        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome is not null) chrome.CornerRadius = new CornerRadius(rounded ? 10 : 0);
        // The close-button hover reaches the top/right edge, then follows the
        // same client-surface corner as the restored window.
        WindowSurface.Clip = rounded ? new RectangleGeometry(
            new Rect(0, 0, WindowSurface.ActualWidth, WindowSurface.ActualHeight), 10, 10) : null;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            int preference = rounded ? 2 : 1; // DWMWCP_ROUND / DWMWCP_DONOTROUND
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero) _ = DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private void MinimizeWindow_Click(object sender, RoutedEventArgs args) => SystemCommands.MinimizeWindow(this);
    private void MaximizeWindow_Click(object sender, RoutedEventArgs args)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void CloseWindow_Click(object sender, RoutedEventArgs args) => Close();
    private void ChromeState_Changed(object? sender, EventArgs args)
    {
        UpdateWindowCorners();
        if (MaximizeGlyph is null) return;
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Text = maximized ? "\uE923" : "\uE922";
        string label = maximized ? "还原窗口" : "最大化窗口";
        MaximizeWindowButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(MaximizeWindowButton, label);
    }
}
