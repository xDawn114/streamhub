using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StreamHub.Host;
using StreamHub.Host.Networking;
using StreamHub.Host.Runtime;

internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
        bool observe = args.Contains("--observe-baseline");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var checks = new List<string>(); var samples = new List<object>(); var requests = new List<string>();
        var access = new NetworkAccessState(true, true, true, true, true, false, [], [], "旧策略迁移说明", null);
        var policy = new HostPolicySnapshot();
        File.WriteAllText(Path.Combine(root, "preferences.json"), "{\"Theme\":\"light\",\"ReduceMotion\":true}");
        var runtime = new RuntimeClient((request, _) =>
        {
            requests.Add(request.Command);
            string? payload = request.Command switch
            {
                "network-access-status" => JsonSerializer.Serialize(access, RuntimeProtocol.JsonOptions),
                "host-policy-status" => JsonSerializer.Serialize(policy, RuntimeProtocol.JsonOptions),
                "control-device-list" => JsonSerializer.Serialize(new ControlDeviceList(false, []), RuntimeProtocol.JsonOptions),
                "files-status" => JsonSerializer.Serialize(new TransferSnapshot(new(), [], []), RuntimeProtocol.JsonOptions),
                "network-config" => "{\"provider\":\"lan\",\"serverUrl\":\"\",\"manualAddresses\":\"\"}",
                "binding-status" => JsonSerializer.Serialize(new LocalBindingSnapshot(null, null, null), RuntimeProtocol.JsonOptions),
                _ => null
            };
            var status = new RuntimeStatus("fixture", "示例电脑", new("remote", true, true, null, 48989, true, "待机"),
                new("tablet", false, false, null, 49089, false, "停止"), new(false, false, null, "停止"),
                "fixture", root, DateTimeOffset.UtcNow, HostPolicy: policy);
            return Task.FromResult(new RuntimeResponse(true, "读取完成", status, payload));
        });
        var window = new MainWindow(runtime, Path.Combine(root, "preferences.json"))
            { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        T N<T>(string name) where T : class => (T)window.FindName(name);
        void Check(bool result, string label) { if (!result) throw new Exception(label); checks.Add(label); }
        void RenderAccess() => typeof(MainWindow).GetMethod("RenderNetworkAccess", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [access]);
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                N<TabControl>("Pages").SelectedItem = N<TabItem>("SettingsPage");
                var categories = N<TabControl>("SettingsCategories"); var expander = N<Expander>("LoginSettingsSection");
                foreach (int theme in new[] { 1, 2 })
                {
                    N<ComboBox>("ThemeChoice").SelectedIndex = theme;
                    foreach (var size in new[] { (940, 660), (1080, 760), (1380, 900) })
                    {
                        window.Width = size.Item1; window.Height = size.Item2; categories.SelectedIndex = 1; Pump();
                        var viewer = (ScrollViewer)((TabItem)categories.SelectedItem).Content;
                        double? initialX = null, initialWidth = null; bool overflow = false;
                        foreach (bool expanded in new[] { false, true, false, true, false })
                        {
                            expander.IsExpanded = expanded; Pump();
                            // Exercise a tall expanded body even at the largest viewport.
                            foreach (var detail in Descendants<Expander>(N<ContentControl>("HostPolicySettings"))) detail.IsExpanded = expanded;
                            viewer.ScrollToTop(); Pump(); window.UpdateLayout();
                            var control = N<CheckBox>("PrivacyModeCheck");
                            double x = control.TranslatePoint(new Point(), window).X;
                            initialX ??= x; initialWidth ??= viewer.ViewportWidth;
                            overflow |= viewer.ScrollableHeight > 0;
                            samples.Add(new { theme, width = size.Item1, height = size.Item2, expanded, x,
                                controlWidth = control.ActualWidth, viewport = viewer.ViewportWidth, overflow = viewer.ScrollableHeight,
                                overlay = ScrollActivity.GetEnabled(viewer) });
                            if (!observe)
                            {
                                Check(Math.Abs(x - initialX.Value) < .5, $"privacy switch stable: {theme}/{size}/{expanded}");
                                Check(Math.Abs(viewer.ViewportWidth - initialWidth.Value) < .5, $"viewport stable: {theme}/{size}/{expanded}");
                                Check(viewer.ScrollableWidth < .5, "security page has no horizontal overflow");
                                Check(ScrollActivity.GetEnabled(viewer), "settings inherit overlay scrollbar behavior");
                                if (expanded)
                                {
                                    viewer.ScrollToBottom(); Pump();
                                    var last = Descendants<FrameworkElement>(N<ContentControl>("HostPolicySettings")).Last();
                                    Check(last.TransformToAncestor(viewer).TransformBounds(new Rect(last.RenderSize)).Bottom <= viewer.ActualHeight + 1,
                                        "expanded policy footer remains reachable");
                                    viewer.ScrollToTop(); Pump();
                                }
                            }
                            if (size == (940, 660) && (expanded || samples.Count == 1)) Save(window, Path.Combine(root, $"security-{theme}-{expanded}.png"));
                        }
                        if (size == (940, 660)) Check(overflow, "regression crosses vertical scrollbar threshold");
                    }
                    window.Width = 1080; window.Height = 760;
                    foreach (int category in new[] { 2, 3, 4, 5 })
                    {
                        categories.SelectedIndex = category; Pump();
                        var viewer = (ScrollViewer)((TabItem)categories.SelectedItem).Content;
                        Check(viewer.ScrollableWidth < .5, "settings page fits viewport");
                        Save(window, Path.Combine(root, $"settings-{theme}-{category}.png"));
                    }
                }
                if (!observe)
                {
                    Check(N<Image>("SoftwareIcon").Source is BitmapSource bitmap && bitmap.PixelWidth >= 24, "official icon is embedded and decoded");
                    categories.SelectedIndex = 3; RenderAccess(); Pump();
                    access = access with { LastError = "模拟防火墙应用失败" }; RenderAccess(); Pump();
                    Check(N<TextBlock>("NetworkAccessStateText").IsVisible && N<TextBlock>("NetworkAccessStateText").Text.Contains("模拟防火墙应用失败"), "real network errors stay visible");
                    Check(N<Button>("SaveNetworkAccessButton").IsEnabled, "network save remains reachable");
                    Check(!requests.Any(x => x is "host-policy-save" or "host-policy-password-save" or "network-access-save" or "privacy-screen-set"), "layout operations do not mutate policies");
                }
                File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = true, observe, checks, samples,
                    installedServiceCalls = 0, operatingSystemInputSent = false }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"PASS {checks.Count} settings layout checks; baseline={observe}");
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = false, error = error.ToString(), samples }));
                Console.Error.WriteLine(error); Environment.ExitCode = 1;
            }
            finally { window.DisposeWindow(); window.Close(); app.Shutdown(); }
        }));
        window.Show(); app.Run();
    }
    static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T result) yield return result;
            foreach (var next in Descendants<T>(child)) yield return next;
        }
    }
    static void Pump()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    static void Save(Window window, string path)
    {
        window.UpdateLayout(); var element = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
