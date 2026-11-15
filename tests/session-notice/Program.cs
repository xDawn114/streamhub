using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using StreamHub.Host;
using StreamHub.Host.Runtime;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var checks = new List<string>();
        string output = Path.GetFullPath(args.Single());
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        int reads = 0;
        ControllerSession controller = new("fixture-one", "示例平板", "局域网 / USB", true);
        var runtime = new RuntimeClient((_, _) =>
        {
            reads++;
            return Task.FromResult(new RuntimeResponse(true, "isolated fixture", new RuntimeStatus("fixture", "fixture",
                new("remote", true, true, null, 48989, true, "streaming", Streaming: true),
                new("tablet", false, false, null, 49089, false, "stopped"), new(false, false, null, "stopped"),
                "fixture", "fixture", DateTimeOffset.UtcNow, Controller: controller)));
        });
        var type = typeof(MainWindow).Assembly.GetType("StreamHub.Host.SessionNoticeWindow", true)!;
        var window = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [runtime], null)!;
        void Check(bool passed, string label) { if (!passed) throw new Exception(label); checks.Add(label); }
        try
        {
            var hide = (DispatcherTimer)type.GetField("_hideTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var poll = (DispatcherTimer)type.GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var refresh = type.GetMethod("RefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            window.Show(); window.Left = -20000; window.Top = -20000;
            Check(window.IsVisible && hide.Interval == TimeSpan.FromSeconds(5), "connection initially shows a five-second notice");
            Check(!window.ShowActivated && !window.ShowInTaskbar, "notice remains nonactivating and absent from taskbar");
            Pump(5300);
            Check(!window.IsVisible && poll.IsEnabled && reads >= 3, "notice hides after five seconds while helper keeps polling");
            ((Task)refresh.Invoke(window, [])!).GetAwaiter().GetResult();
            Check(!window.IsVisible, "unchanged session does not reopen hidden notice");
            controller = new("fixture-two", "另一台平板", "局域网 / USB", true);
            hide.Interval = TimeSpan.FromMilliseconds(50);
            ((Task)refresh.Invoke(window, [])!).GetAwaiter().GetResult(); window.Left = -20000; window.Top = -20000;
            Check(window.IsVisible && hide.IsEnabled, "changed authenticated controller receives a fresh bounded notice");
            Pump(100);
            Check(!window.IsVisible && poll.IsEnabled, "reopened notice hides again without exiting helper");
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, count = checks.Count, checks, realServiceCalls = 0, operatingSystemInputSent = false }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {checks.Count} isolated notice lifecycle checks.");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = false, error = error.ToString() }));
            Console.Error.WriteLine(error);
            return 1;
        }
        finally { window.Close(); app.Shutdown(); }
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
