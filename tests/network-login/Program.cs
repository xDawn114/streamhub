using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StreamHub.Host;
using StreamHub.Host.Networking;
using StreamHub.Host.Runtime;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string root = Path.GetFullPath(args.Single());
        Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var checks = new List<string>();
        var calls = new List<string>();
        bool running = false, startFails = false, statusFails = false;
        int confirmations = 0;
        var settings = new TransportSettings();
        var network = new NetworkStatus { Available = true, LoggedIn = true, State = "Running" };
        TaskCompletionSource<RuntimeResponse>? pendingStart = null;
        var engine = new EngineStatus("remote", true, true, null, 48989, true, "ready");
        var runtime = new RuntimeClient((request, _) =>
        {
            calls.Add(request.Command);
            if (request.Command == "status" && statusFails) return Task.FromResult(new RuntimeResponse(false, "后台暂不可用"));
            if (request.Command is "network-start" or "network-takeover")
            {
                if (pendingStart is not null) return pendingStart.Task;
                if (startFails) return Task.FromResult(new RuntimeResponse(false, "启动连接失败，请重试。"));
                running = true;
            }
            string? payload = request.Command switch
            {
                "network-config" => JsonSerializer.Serialize(settings, RuntimeProtocol.JsonOptions),
                "network-login" or "network-status" => JsonSerializer.Serialize(network, RuntimeProtocol.JsonOptions),
                "control-device-list" => JsonSerializer.Serialize(new ControlDeviceList(false, []), RuntimeProtocol.JsonOptions),
                "binding-status" => JsonSerializer.Serialize(new LocalBindingSnapshot(null, null, null), RuntimeProtocol.JsonOptions),
                "receiver-list" => JsonSerializer.Serialize(new ReceiverSnapshot([], null), RuntimeProtocol.JsonOptions),
                "files-status" => JsonSerializer.Serialize(new TransferSnapshot(new(), [], []), RuntimeProtocol.JsonOptions),
                _ => null
            };
            return Task.FromResult(new RuntimeResponse(true, "fixture", new RuntimeStatus("fixture", "fixture",
                engine, engine with { Profile = "tablet" }, new(running, running, null, "fixture"),
                "fixture", root, DateTimeOffset.UnixEpoch), payload));
        });
        var window = new MainWindow(runtime, Path.Combine(root, "preferences.json"))
        {
            Left = -20000, Top = -20000, Width = 940, Height = 660,
            ShowActivated = false, ShowInTaskbar = false
        };
        T N<T>(string name) where T : class => (T)window.FindName(name);
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); }
        Task Login(bool takeover = false, bool approve = true) => (Task)typeof(MainWindow)
            .GetMethod("RunNetworkLoginAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [new Func<bool>(() => takeover), new Func<bool>(() => { confirmations++; return approve; })])!;
        void Finish(Task task)
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!task.IsCompleted && DateTime.UtcNow < until) Pump();
            if (!task.IsCompleted) throw new TimeoutException("Isolated login check stalled");
            task.GetAwaiter().GetResult();
        }
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                Pump();
                N<TabControl>("Pages").SelectedItem = N<TabItem>("SettingsPage");
                N<TabControl>("SettingsCategories").SelectedItem = N<TabItem>("NetworkSettingsCategory");
                N<Expander>("NetworkSetupExpander").IsExpanded = false;
                Pump();
                Check(N<Button>("NetworkLoginButton").IsVisible, "login remains visible while more operations are collapsed");

                calls.Clear(); Finish(Login());
                Check(calls.IndexOf("network-start") >= 0 && calls.IndexOf("network-start") < calls.IndexOf("network-login"), "first login starts the component before requesting authorization");
                Check(confirmations == 0 && !calls.Contains("network-takeover"), "first login without an external network does not request takeover");
                Check((string)N<Button>("NetworkLoginButton").Content == "已登录", "completed login shows the confirmed state");

                calls.Clear(); Finish(Login(takeover: true));
                Check(!calls.Contains("network-start") && !calls.Contains("network-takeover") && confirmations == 0, "healthy running component is not restarted or taken over");

                running = false; calls.Clear(); Finish(Login(takeover: true, approve: false));
                Check(confirmations == 1 && !calls.Contains("network-takeover") && !calls.Contains("network-login"), "declining takeover leaves the external connection untouched");
                Check(N<TextBlock>("NetworkLoginProgressText").Text == "已取消登录。", "declining has local feedback");
                calls.Clear(); Finish(Login(takeover: true));
                Check(confirmations == 2 && calls.Contains("network-takeover") && calls.Contains("network-login"), "approved takeover prepares the network and then logs in");

                running = false; startFails = true; calls.Clear(); Finish(Login());
                Check(!calls.Contains("network-login") && N<TextBlock>("NetworkLoginProgressText").Text.Contains("启动连接失败") && N<Button>("NetworkLoginButton").IsEnabled, "failed startup keeps an actionable error and a usable retry button");
                startFails = false; statusFails = true; calls.Clear(); Finish(Login());
                Check(!calls.Contains("network-start") && !calls.Contains("network-login"), "unknown live status performs no network mutation");
                statusFails = false; settings = new("lan"); calls.Clear(); Finish(Login());
                Check(!calls.Contains("network-start") && !calls.Contains("network-login") && !N<Button>("NetworkLoginButton").IsEnabled, "local connection needs no account or network startup");

                settings = new(); running = false; calls.Clear();
                pendingStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var pending = Login();
                Check(!pending.IsCompleted && !N<Button>("NetworkLoginButton").IsEnabled && N<TextBlock>("NetworkLoginProgressText").Text.Contains("正在启动"), "startup immediately displays progress and disables login");
                Finish(Login());
                Check(calls.Count(c => c == "network-start") == 1, "repeated login while starting does not duplicate the request");
                running = true; pendingStart.SetResult(new(true, "ready")); Finish(pending); pendingStart = null;

                network = new() { Available = true, State = "NeedsLogin", BrowseUrl = "https://foreign.example/unauthorized" };
                Finish(Login());
                Check(N<TextBlock>("NetworkLoginProgressText").Text.Contains("不匹配") && !N<TextBox>("LoginUrlBox").IsVisible, "unexpected authorization origin is rejected without opening a browser");
                network = new() { Available = true, State = "NeedsLogin" };
                Finish(Login());
                Check(N<TextBlock>("NetworkLoginProgressText").Text.Contains("暂时无法获取"), "missing authorization URL leaves a retryable error");

                foreach (Exception failure in new Exception[] { new TimeoutException("local-tailscaled.sock:80"), new TaskCanceledException("local-tailscaled.sock:80"), new HttpRequestException("local-tailscaled.sock:80") })
                {
                    using var http = new HttpClient(new FailingApi(failure)) { BaseAddress = new Uri("http://memory/localapi/v0/") };
                    using var client = (EmbeddedNetworkClient)typeof(EmbeddedNetworkClient)
                        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(HttpClient)], null)!.Invoke([http]);
                    try { Finish(client.BeginLoginAsync()); throw new Exception("Expected connection failure"); }
                    catch (InvalidOperationException error)
                    {
                        Check(!error.Message.Contains("local-tailscaled") && error.InnerException == failure, "transport error is translated and original diagnostic detail is preserved: " + failure.GetType().Name);
                    }
                }

                foreach (int theme in new[] { 1, 2 })
                {
                    N<ComboBox>("ThemeChoice").SelectedIndex = theme;
                    foreach (string name in new[] { "NetworkSetupExpander", "LoginSettingsSection" })
                    {
                        N<TabControl>("SettingsCategories").SelectedIndex = name == "NetworkSetupExpander" ? 3 : 1;
                        var expander = N<Expander>(name);
                        expander.IsExpanded = false; Pump(); expander.ApplyTemplate();
                        var toggle = (ToggleButton)expander.Template.FindName("HeaderToggle", expander); toggle.ApplyTemplate();
                        var label = (TextBlock)toggle.Template.FindName("ExpansionLabel", toggle);
                        Check(toggle.ActualHeight >= 44 && label.Text == "展开", $"theme {theme}/{name}: clear full-row collapsed control");
                        toggle.IsChecked = true; Pump();
                        Check(expander.IsExpanded && label.Text == "收起", $"theme {theme}/{name}: activation updates the state and content");
                    }
                    N<TabControl>("SettingsCategories").SelectedItem = N<TabItem>("NetworkSettingsCategory");
                    Save((FrameworkElement)window.Content, Path.Combine(root, $"network-{theme}.png"));
                }
                File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = true, count = checks.Count, checks, liveServiceCalls = 0 }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"PASS {checks.Count} isolated network-login checks");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { window.Close(); app.Shutdown(); }
        }));
        window.Show(); app.Run();
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Save(FrameworkElement view, string path)
    {
        view.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private sealed class FailingApi(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromException<HttpResponseMessage>(failure);
    }
}
