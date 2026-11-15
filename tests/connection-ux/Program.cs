using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StreamHub.Host;
using StreamHub.Host.Runtime;

internal static class Program
{
    [STAThread] private static void Main(string[] args)
    {
        string root = Path.GetFullPath(args.Single());
        Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var checks = new List<string>();
        var calls = new List<RuntimeRequest>();
        bool paused = false, remote = false, tablet = false, available = true;
        BrowseRoot[] roots = [];
        var engine = new EngineStatus("remote", true, true, null, 48989, true, "就绪");
        var runtime = new RuntimeClient((request, _) =>
        {
            calls.Add(request);
            if (request.Command == "status" && !available) return Task.FromResult(new RuntimeResponse(false, "隔离后台不可用"));
            if (request.Command == "connections-pause") { paused = true; remote = tablet = false; }
            if (request.Command == "connections-resume") paused = false;
            string? payload = request.Command switch
            {
                "network-config" => "{\"provider\":\"lan\",\"serverUrl\":\"\",\"manualAddresses\":\"\"}",
                "control-device-list" => JsonSerializer.Serialize(new ControlDeviceList(false, []), RuntimeProtocol.JsonOptions),
                "binding-status" => JsonSerializer.Serialize(new LocalBindingSnapshot(null, null, null), RuntimeProtocol.JsonOptions),
                "receiver-list" => JsonSerializer.Serialize(new ReceiverSnapshot([], null), RuntimeProtocol.JsonOptions),
                "host-policy-status" => JsonSerializer.Serialize(new HostPolicySnapshot(), RuntimeProtocol.JsonOptions),
                "files-status" => JsonSerializer.Serialize(new TransferSnapshot(new(BrowseRoots: roots), [], []), RuntimeProtocol.JsonOptions),
                _ => null
            };
            return Task.FromResult(new RuntimeResponse(true, "隔离状态已更新", new RuntimeStatus("fixture", "测试电脑",
                engine with { Streaming = remote, Desired = !paused, Healthy = !paused },
                engine with { Profile = "tablet", Streaming = tablet, Desired = tablet, Healthy = tablet },
                new(false, false, null, "未启用"), "fixture", root, DateTimeOffset.UnixEpoch, ConnectionsPaused: paused), payload));
        });
        string preferences = Path.Combine(root, "preferences.json");
        File.WriteAllText(preferences, "{\"Theme\":\"light\",\"ReduceMotion\":true}");
        var window = new MainWindow(runtime, preferences) { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, Width = 940, Height = 660 };
        T N<T>(string name) where T : class => (T)window.FindName(name);
        void Invoke(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); }
        int Pauses() => calls.Count(c => c.Command == "connections-pause");
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                Pump();
                Invoke("PauseConnections_Click"); Pump();
                Check(paused && Pauses() == 1, "idle pause needs no confirmation");
                Invoke("PauseConnections_Click"); Pump();
                Check(!paused && calls.Count(c => c.Command == "connections-resume") == 1, "resume needs no confirmation");

                // Leave the UI snapshot idle: the click must read the newly active session.
                remote = true;
                var cancel = RespondToConfirmation(window, 7);
                Invoke("PauseConnections_Click"); Pump();
                var confirmation = cancel.GetAwaiter().GetResult();
                Check(confirmation.DefaultButton == 7, "live pause defaults to No");
                Check(confirmation.Text.Contains("主屏和副屏") && confirmation.Text.Contains("重启后仍保持暂停"), "confirmation describes both disconnection and persistence");
                Check(!paused && remote && Pauses() == 1, "cancel preserves the active main session and sends no pause");

                remote = false; tablet = true;
                var tabletCancel = RespondToConfirmation(window, 7);
                Invoke("PauseConnections_Click"); Pump(); tabletCancel.GetAwaiter().GetResult();
                Check(!paused && tablet && Pauses() == 1, "secondary-only session also requires confirmation");

                remote = true;
                var accept = RespondToConfirmation(window, 6);
                Invoke("PauseConnections_Click"); Pump(); accept.GetAwaiter().GetResult();
                Check(paused && !remote && !tablet && Pauses() == 2, "accept sends exactly one pause for active sessions");
                Invoke("PauseConnections_Click"); Pump();

                // A stale enabled button must not issue a state-changing command on read failure.
                available = false;
                int mutations = calls.Count(c => c.Command is "connections-pause" or "connections-resume");
                Invoke("PauseConnections_Click"); Pump();
                Check(mutations == calls.Count(c => c.Command is "connections-pause" or "connections-resume"), "unavailable fresh status causes no mutation");
                available = true; Invoke("RefreshButton_Click"); Pump();

                Invoke("OpenFileAccessSettings_Click"); Pump();
                Check(N<TextBlock>("BrowseRootsEmptyText").IsVisible && N<ListBox>("BrowseRootList").Visibility == Visibility.Collapsed, "empty grant list shows text instead of blank panel");
                roots = [new("fixture-root", "样本目录", root, "fixture-owner")];
                typeof(MainWindow).GetMethod("RefreshFilesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null); Pump();
                Check(N<TextBlock>("BrowseRootsEmptyText").Visibility == Visibility.Collapsed && N<ListBox>("BrowseRootList").IsVisible, "adding a root replaces empty state with the list");
                roots = [];
                typeof(MainWindow).GetMethod("RefreshFilesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null); Pump();
                Check(N<TextBlock>("BrowseRootsEmptyText").IsVisible, "removing the last root restores empty state");
                Save((FrameworkElement)window.Content, Path.Combine(root, "empty-roots.png"));
                Invoke("CloseDevicePermissions_Click"); Pump();

                foreach (int theme in new[] { 1, 2 })
                {
                    N<ComboBox>("ThemeChoice").SelectedIndex = theme;
                    Invoke("OpenHelp_Click"); Pump();
                    var help = N<Grid>("HelpDialog");
                    Check(help.IsVisible && N<StackPanel>("HelpContent").Children.OfType<TextBlock>().All(t => t.FontFamily.Source == window.FontFamily.Source) && app.Windows.Count == 1, $"theme {theme}: manual shares main window and font");
                    Check(N<ScrollViewer>("HelpScroller").ScrollableWidth < 1 && N<ScrollViewer>("HelpScroller").ViewportHeight > 0, $"theme {theme}: manual fits minimum window and scrolls vertically");
                    Check(N<StackPanel>("HelpContent").Children.OfType<TextBlock>().Any(t => t.Text.Contains("重启后仍保持暂停") && t.Text.Contains("当前主屏和副屏")), $"theme {theme}: manual states pause consequences");
                    Save((FrameworkElement)window.Content, Path.Combine(root, $"manual-{theme}.png"));
                    var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    N<Button>("HelpCloseButton").RaiseEvent(escape); Pump();
                    Check(help.Visibility == Visibility.Collapsed && escape.Handled, $"theme {theme}: Escape closes manual");
                }
                Check(N<StackPanel>("HelpContent").Children.Count == 14, "reopening manual does not duplicate content");
                File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = true, count = checks.Count, checks, installedServiceCalls = 0, globalKeyboardOrMouseInput = false }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"PASS {checks.Count} connection UX checks.");
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = false, error = error.ToString() }));
                Console.Error.WriteLine(error); Environment.ExitCode = 1;
            }
            finally { window.DisposeWindow(); window.Close(); app.Shutdown(); }
        }));
        window.Show(); app.Run();
    }

    // Only address a native dialog owned by this isolated test window. No desktop input is sent.
    private static Task<(int DefaultButton, string Text)> RespondToConfirmation(Window owner, int response)
    {
        nint ownerHandle = new WindowInteropHelper(owner).Handle;
        uint thread = GetWindowThreadProcessId(ownerHandle, out _);
        return Task.Run(() =>
        {
            nint dialog = 0;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (dialog == 0 && DateTime.UtcNow < deadline)
            {
                EnumThreadWindows(thread, (handle, _) =>
                {
                    var name = new StringBuilder(64); GetClassName(handle, name, name.Capacity);
                    if (GetWindow(handle, 4) == ownerHandle && name.ToString() == "#32770") dialog = handle;
                    return true;
                }, 0);
                if (dialog == 0) Thread.Sleep(10);
            }
            if (dialog == 0) throw new Exception("Expected owned pause confirmation was not shown.");
            var text = new StringBuilder(1024); GetWindowText(GetDlgItem(dialog, 65535), text, text.Capacity);
            int defaultButton = (int)(SendMessage(dialog, 0x400, 0, 0).ToInt64() & 0xffff);
            PostMessage(dialog, 0x111, response, GetDlgItem(dialog, response));
            return (defaultButton, text.ToString());
        });
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Save(FrameworkElement element, string path)
    {
        element.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private delegate bool EnumWindow(nint handle, nint state);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EnumWindow callback, nint state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint process);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint handle, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint handle, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint handle, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(nint handle, uint message, nint wparam, nint lparam);
}
