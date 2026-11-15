using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using StreamHub.Host;
using StreamHub.Host.Networking;
using StreamHub.Host.Runtime;

internal static class Program
{
    private static int _exitCode;
    [STAThread] static int Main(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        if (!System.Text.RegularExpressions.Regex.IsMatch(root, @"^D:\\Deepseek\\build\\stage\d+-tests\\desktop\\", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new ArgumentException("Isolated preview directory required.");
        Directory.CreateDirectory(root);
        bool interactive = args.Contains("--interactive");
        string mode = "streaming";
        int changes = 0;
        var runtime = new RuntimeClient((request, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (request.Command == "power-settings") { mode = request.PreventSleepMode!; changes++; }
            var status = new RuntimeStatus("preview-device", "示例电脑",
                new("remote", true, true, 1, 48989, true, "等待设备连接"),
                new("tablet", false, false, null, 49089, false, "副屏未启用"),
                new(false, false, null, "仅局域网"), "0.8.12", root, DateTimeOffset.Now,
                PreventSleepMode: mode, SleepPreventionActive: mode == "plugged-in",
                SleepPreventionMessage: PowerAwakePolicy.Describe(mode, mode == "plugged-in", false),
                HostPolicy: new(Revision: 7, LockOnExit: true, AutomaticLoginAvailable: true));
            string? payload = request.Command switch
            {
                "files-status" => JsonSerializer.Serialize(new TransferSnapshot(new(), [], []), RuntimeProtocol.JsonOptions),
                "network-config" => "{\"provider\":\"lan\",\"serverUrl\":\"\",\"manualAddresses\":\"\"}",
                "binding-status" => JsonSerializer.Serialize(new LocalBindingSnapshot("012345", DateTimeOffset.Now.AddMinutes(3), null), RuntimeProtocol.JsonOptions),
                "control-info" => JsonSerializer.Serialize(new ControlApiStatus(true, true, null, null), RuntimeProtocol.JsonOptions),
                "host-policy-status" => JsonSerializer.Serialize(status.HostPolicy, RuntimeProtocol.JsonOptions),
                "network-access-status" => JsonSerializer.Serialize(new NetworkAccessState(false, false, false, true, true,
                    true, ["示例安全隧道"], ["100.64.1.1"], "局域网访问仅限专用和域网络。", null), RuntimeProtocol.JsonOptions),
                _ => null
            };
            return Task.FromResult(new RuntimeResponse(true, "本地界面预览，使用示例数据", status, payload));
        });
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new MainWindow(runtime, Path.Combine(root, "appearance.json"))
        { Title = "iControl · 本机界面预览", ShowActivated = interactive, ShowInTaskbar = interactive };
        if (!interactive) { window.Left = -20000; window.Top = -20000; }
        window.Closed += (_, _) => window.DisposeWindow();
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Task.Delay(150);
                void Check(bool passed, string message) { if (!passed) throw new Exception(message); }
                void Capture(string name)
                {
                    window.UpdateLayout();
                    var content = (FrameworkElement)window.Content;
                    var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(root, name)); png.Save(output);
                }
                var pages = (TabControl)window.FindName("Pages");
                // An offscreen WPF window has no compositor animation clock.
                // Verify reduced motion and layout here; native mouse/DWM behavior needs device acceptance.
                ((CheckBox)window.FindName("ReduceMotionCheck")).IsChecked = true;
                ScrollActivity.SetReduceMotion(window, true);
                var theme = (ComboBox)window.FindName("ThemeChoice");
                var settings = (TabItem)window.FindName("SettingsPage");
                Check(WindowChrome.GetWindowChrome(window)?.CaptionHeight == 48 && window.WindowStyle == WindowStyle.None,
                    "integrated draggable title bar missing");
                foreach (var size in new[] { (940d, 660d), (1080d, 760d) })
                {
                    window.Width = size.Item1; window.Height = size.Item2; window.UpdateLayout();
                    var title = (FrameworkElement)window.FindName("TitleBar");
                    var version = (FrameworkElement)window.FindName("BrandVersion");
                    var captions = (FrameworkElement)window.FindName("CaptionButtons");
                    Check(version.TranslatePoint(new Point(version.ActualWidth, 0), title).X + 12 < captions.TranslatePoint(new Point(), title).X,
                        "brand overlaps window buttons at minimum window size");
                    Check(title.ActualHeight == 48, "title bar changed height");
                    var close = (Button)window.FindName("CloseWindowButton");
                    var surface = (FrameworkElement)window.FindName("WindowSurface");
                    Check(Math.Abs(close.ActualHeight - title.ActualHeight) < .5 && Math.Abs(close.TranslatePoint(new Point(), title).Y) < .5,
                        "close highlight does not fill title bar vertically");
                    Check(Math.Abs(close.TranslatePoint(new Point(close.ActualWidth, 0), title).X - title.ActualWidth) < .5,
                        "close highlight leaves a gap at the right edge");
                    Check(surface.Clip is RectangleGeometry { RadiusX: 10, RadiusY: 10 } clip && Math.Abs(clip.Rect.Width - surface.ActualWidth) < .5,
                        "restored client surface is not clipped to the rounded window bounds");
                }
                window.Width = 1080; window.Height = 760;
                pages.SelectedItem = settings; window.UpdateLayout();
                var viewer = (ScrollViewer)settings.Content;
                Check(viewer.ScrollableHeight > 0, "settings must overflow for scrollbar verification");
                var bar = (ScrollBar)viewer.Template.FindName("PART_VerticalScrollBar", viewer);
                Check(bar.ActualWidth >= 14 && bar.ActualWidth <= 18, "thin scrollbar lost its larger hit area");
                double viewportWidth = viewer.ViewportWidth;
                viewer.ScrollToVerticalOffset(150); await Task.Delay(180);
                Check(ScrollActivity.GetActive(viewer) && bar.Opacity > .95,
                    $"scrolling does not reveal scrollbar: viewerEnabled={ScrollActivity.GetEnabled(viewer)}, viewerActive={ScrollActivity.GetActive(viewer)}, barEnabled={ScrollActivity.GetEnabled(bar)}, barActive={ScrollActivity.GetActive(bar)}, opacity={bar.Opacity}, offset={viewer.VerticalOffset}");
                Check(Math.Abs(viewer.ViewportWidth - viewportWidth) < .5, "scrollbar shifts page contents");
                await Task.Delay(1300);
                Check(!ScrollActivity.GetActive(viewer) && bar.Opacity < .05, "idle scrollbar did not hide");
                viewer.SetValue(ScrollActivity.PointerNearProperty, true);
                viewer.ScrollToVerticalOffset(180); await Task.Delay(1400);
                for (int i = 0; ScrollActivity.GetActive(viewer) && i < 15; i++) await Task.Delay(100);
                Check(!ScrollActivity.GetActive(viewer) && bar.Opacity > .95,
                    $"stationary edge hover disappeared with idle timer: active={ScrollActivity.GetActive(viewer)}, nearViewer={ScrollActivity.GetPointerNear(viewer)}, nearBar={ScrollActivity.GetPointerNear(bar)}, opacity={bar.Opacity}");
                var track = (Track)bar.Template.FindName("PART_Track", bar);
                var ink = (Border)track.Thumb.Template.FindName("Ink", track.Thumb);
                Check(ink.ActualWidth >= 6.5, "edge hover did not widen the scrollbar");
                viewer.SetValue(ScrollActivity.PointerNearProperty, false); await Task.Delay(180);
                Check(bar.Opacity < .05, "scrollbar did not hide after hover ended");
                foreach (int selection in new[] { 1, 2 })
                {
                    theme.SelectedIndex = selection; pages.SelectedIndex = 0;
                    ((TextBlock)window.FindName("AddressText")).Text = "本机地址 192.0.2.10";
                    window.UpdateLayout(); await Task.Delay(160);
                    Capture("home-" + (selection == 1 ? "light" : "dark") + ".png");
                    pages.SelectedItem = settings; viewer.ScrollToTop(); window.UpdateLayout(); await Task.Delay(160);
                    Capture("settings-" + (selection == 1 ? "light" : "dark") + ".png");
                    var login = (Expander)window.FindName("LoginSettingsSection");
                    login.IsExpanded = true; window.UpdateLayout();
                    var loginPanel = (ContentControl)window.FindName("HostPolicySettings");
                    loginPanel.BringIntoView(); await Task.Delay(80);
                    Check(loginPanel.Content is FrameworkElement { ActualHeight: > 100 }, "computer login settings panel is empty");
                    Capture("login-" + (selection == 1 ? "light" : "dark") + ".png");
                    var files = (Expander)window.FindName("FileAccessDetails");
                    files.IsExpanded = true; window.UpdateLayout();
                    var sendDevice = (ComboBox)window.FindName("FileDeviceBox");
                    var settingsDevice = (ComboBox)window.FindName("SettingsFileDeviceBox");
                    sendDevice.ItemsSource = new[] { new { ClientId = "preview-a", Label = "示例平板" }, new { ClientId = "preview-b", Label = "示例手机" } };
                    sendDevice.SelectedValue = "preview-a"; window.UpdateLayout();
                    Check(settingsDevice.SelectedValue as string == "preview-a", "file permissions selector lost the sending device");
                    settingsDevice.SelectedValue = "preview-b"; window.UpdateLayout();
                    Check(sendDevice.SelectedValue as string == "preview-b", "file permissions selection does not update the sending device");
                    settingsDevice.BringIntoView(); await Task.Delay(80);
                    Capture("files-" + (selection == 1 ? "light" : "dark") + ".png");
                    ((FrameworkElement)window.FindName("NetworkAccessStateText")).BringIntoView(); await Task.Delay(80);
                    Capture("network-" + (selection == 1 ? "light" : "dark") + ".png");
                    login.IsExpanded = false; files.IsExpanded = false;
                }
                File.WriteAllText(Path.Combine(root, "chrome-checks.json"), JsonSerializer.Serialize(new
                { passed = true, titleHeight = 48, cornerRadius = 10, closeCoversCaption = true, sizes = new[] {"940x660", "1080x760"}, scrollbar = "idle hides; stationary edge hover survives idle and widens; stable viewport", expandedSettings = true, fileSelectionTwoWay = true, serviceCalls = 0 }));
                if (interactive) { theme.SelectedIndex = 1; pages.SelectedIndex = 0; }
            }
            catch (Exception error)
            {
                _exitCode = 1;
                File.WriteAllText(Path.Combine(root, "chrome-checks.json"), JsonSerializer.Serialize(new {passed=false, error=error.ToString()}));
            }
            finally { if (!interactive) app.Shutdown(); }
        };
        app.Run(window);
        return _exitCode;
    }
}
