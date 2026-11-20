using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StreamHub.Host;
using StreamHub.Host.Runtime;
using StreamHub.Host.Networking;

static class Program
{
    [STAThread] static int Main(string[] args)
    {
        string root = Path.GetFullPath(args.Single());
        if (Directory.Exists(root)) throw new IOException("Use a new isolated preview directory.");
        Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string state = "pending";
        bool failStart = false;
        string sessionId = "fixture-session";
        var requests = new List<RuntimeRequest>();
        RuntimeStatus Status() => new("fixture", "示例电脑", new("remote", false, true, null, 48989, false, "协助占用"),
            new("tablet", false, false, null, 49089, false, "未启用"), new(false, false, null, "局域网"),
            "0.8.41", root, DateTimeOffset.UtcNow, Assistance: new(state,
                Code: state == "offered" ? "7F3K-92QX" : null, ExpiresAt: DateTimeOffset.Now.AddMinutes(10), SessionId: sessionId, Name: "教室轻薄本",
                Comparison: "A23F-94BD", Mode: state == "active" ? "view" : null,
                Message: state switch { "active" => "临时协助 · 仅观看", "offered" => "协助码已生成，等待连接", "pending" => "等待选择本次权限", _ => "临时协助已关闭" },
                Enabled: state is not ("idle" or "ended"), CanTrust: state == "pending",
                TrustedDevices: [new("known-laptop", "宿舍轻薄本", "control", DateTimeOffset.UtcNow, false, true)]));
        var runtime = new RuntimeClient((request, _) => {
            requests.Add(request);
            if (request.Command.StartsWith("assistance-approve-", StringComparison.Ordinal)) state = "active";
            if (request.Command == "assistance-enable" && request.AssistanceEnabled == true && failStart) return Task.FromResult(new RuntimeResponse(false, "模拟开启失败", Status()));
            if (request.Command == "assistance-enable") state = request.AssistanceEnabled == true ? "offered" : "ended";
            if (request.Command == "assistance-refresh") state = "offered";
            if (request.Command == "assistance-end") state = "ended";
            return Task.FromResult(new RuntimeResponse(true, "隔离界面数据", Status(),
            request.Command switch {
                "control-device-list" => JsonSerializer.Serialize(new ControlDeviceList(false, [new("tablet-credential", "test-tablet", "Test tablet", ControlScope.Tablet, false, DateTimeOffset.UtcNow)]), RuntimeProtocol.JsonOptions),
                "receiver-list" => JsonSerializer.Serialize(new ReceiverSnapshot([new("test-tablet", "Test tablet", 2560, 1600, 60, [0, 90, 180, 270], DateTimeOffset.UtcNow, true)], null), RuntimeProtocol.JsonOptions),
                "files-status" => JsonSerializer.Serialize(new TransferSnapshot(new(), [], []), RuntimeProtocol.JsonOptions),
                "network-config" => "{\"provider\":\"lan\",\"serverUrl\":\"\",\"manualAddresses\":\"\"}",
                "binding-status" => JsonSerializer.Serialize(new LocalBindingSnapshot(null, null, null), RuntimeProtocol.JsonOptions),
                _ => null
            }));
        });
        var window = new MainWindow(runtime, Path.Combine(root, "appearance.json")) {
            Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false
        };
        int count = 0; int exitCode = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () => {
            try
            {
                var pages = (TabControl)window.FindName("Pages");
                Check(pages.SelectedItem == window.FindName("AssistancePage"), "first visible launch opens assistance");
                Check(File.Exists(Path.Combine(root, "appearance.json")), "first visible launch persists without waiting for a settings edit");
                var returning = new MainWindow(new RuntimeClient((_, _) => Task.FromResult(new RuntimeResponse(true, "fixture", Status()))), Path.Combine(root, "appearance.json")) {
                    Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false
                };
                returning.Show(); returning.UpdateLayout();
                Check(((TabControl)returning.FindName("Pages")).SelectedItem == returning.FindName("HomePage"),
                    "returning desktop launch retains the normal home page");
                typeof(MainWindow).GetMethod("OpenAssistance", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(returning, null);
                Check(((TabControl)returning.FindName("Pages")).SelectedItem == returning.FindName("AssistancePage"),
                    "explicit installer navigation opens assistance in an existing window");
                returning.DisposeWindow(); returning.Close();
                string legacyPath = Path.Combine(root, "legacy-appearance.json");
                string legacySettings = "{\"Theme\":\"dark\",\"ReduceMotion\":true,\"TabletOrientation\":4,\"TabletLink\":2}";
                File.WriteAllText(legacyPath, legacySettings);
                var existing = new MainWindow(new RuntimeClient((_, _) => Task.FromResult(new RuntimeResponse(true, "fixture", Status()))), legacyPath) {
                    Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false
                };
                existing.Show(); existing.UpdateLayout();
                Check(((TabControl)existing.FindName("Pages")).SelectedItem == existing.FindName("HomePage") &&
                    File.ReadAllText(legacyPath) == legacySettings && ((ComboBox)existing.FindName("ThemeChoice")).SelectedIndex == 2,
                    "existing preferences and startup page survive the first-launch change");
                existing.DisposeWindow(); existing.Close();
                var privateStatus = typeof(MainWindow).GetField("_status", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var render = typeof(MainWindow).GetMethod("RenderAssistance", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var renderSummary = typeof(MainWindow).GetMethod("RefreshUnifiedStatus", BindingFlags.NonPublic | BindingFlags.Instance)!;
                typeof(MainWindow).GetField("_addresses", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, new[] { "192.168.1.10" });
                foreach (var sample in new[] { ("idle", 940d, 660d, 1), ("offered", 1080d, 760d, 1), ("pending", 940d, 660d, 1), ("active", 1080d, 760d, 2), ("offered", 940d, 660d, 2), ("pending", 1080d, 760d, 2) })
                {
                    state = sample.Item1;
                    window.Width = sample.Item2; window.Height = sample.Item3;
                    ((ComboBox)window.FindName("ThemeChoice")).SelectedIndex = sample.Item4;
                    privateStatus.SetValue(window, Status()); render.Invoke(window, null); renderSummary.Invoke(window, null);
                    pages.SelectedItem = window.FindName("AssistancePage"); window.UpdateLayout();
                    var pending = (FrameworkElement)window.FindName("AssistancePendingPanel");
                    Check(pending.Visibility == (state == "pending" ? Visibility.Visible : Visibility.Collapsed), "owner mode choice appears only for pending request");
                    bool reserved = state is "pending" or "active";
                    Check(((CheckBox)window.FindName("AssistanceEnabledToggle")).IsChecked == Status().Assistance!.Enabled, "switch reflects persisted assistance availability");
                    Check(((Button)window.FindName("AssistanceEndButton")).IsEnabled == reserved, "ending is available only for reserved session");
                    Check(((TextBlock)window.FindName("AssistanceHostState")).Text == Status().Assistance!.Message, "visible owner status matches actual mode");
                    Check(((TextBlock)window.FindName("AssistanceCodeText")).Text == (state == "offered" ? "7F3K-92QX" : state == "idle" ? "已关闭" : "等待可用"), "current code is immediately readable after login");
                    Check(((Button)window.FindName("AssistanceRefreshButton")).IsEnabled == (state == "offered"), "refresh is available only outside an active request");
                    Check(((ItemsControl)window.FindName("AssistanceHostTrustList")).Items.Count == 1, "local trusted device is visible with its permission");
                    Check(((FrameworkElement)window.FindName("AssistanceHostTrustGroup")).Visibility == Visibility.Visible, "trusted devices have a dedicated section");
                    if (reserved) Check(((TextBlock)window.FindName("AcceptanceLabel")).Text.Contains("临时协助"), "title status reflects reserved assistance instead of normal engine readiness");
                    var local = (FrameworkElement)window.FindName("AssistanceLocalPanel");
                    var remote = (FrameworkElement)window.FindName("AssistanceRemotePanel");
                    DependencyObject? ancestor = local;
                    while (ancestor is not null && ancestor is not ScrollViewer) ancestor = VisualTreeHelper.GetParent(ancestor);
                    Check(ancestor is ScrollViewer { Background: null } ||
                        ancestor is ScrollViewer { Background: SolidColorBrush { Color.A: 0 } },
                        "assistance uses the page background without an inset gray slab");
                    Check(Math.Abs(local.ActualWidth - remote.ActualWidth) < 1, "local and remote sections share one width");
                    var info = (FrameworkElement)window.FindName("AssistanceConnectionBox");
                    var code = (FrameworkElement)window.FindName("AssistanceJoinCodeBox");
                    Check(Math.Abs(info.TranslatePoint(new Point(), remote).Y - code.TranslatePoint(new Point(), remote).Y) < 1,
                        "remote address and code inputs align at both widths");
                    if (state is "idle" or "offered")
                    {
                        var connectButton = (Button)window.FindName("AssistanceJoinButton");
                        Check(connectButton.TranslatePoint(new Point(0, connectButton.ActualHeight), window).Y <= window.ActualHeight - 16,
                            "primary connection action remains visible at minimum window height");
                    }
                    if (state == "pending")
                    {
                        Check(((TextBlock)window.FindName("AssistanceComparisonText")).Text.Contains("A23F-94BD"), "comparison is visible beside explicit mode choices");
                        Check(pending.ActualWidth > 400, "pending layout retains readable width at minimum window size");
                        var view = (RadioButton)window.FindName("AssistanceViewPermission");
                        var control = (RadioButton)window.FindName("AssistanceControlPermission");
                        var allow = (Button)window.FindName("AssistanceAllowButton");
                        Check(view.IsChecked != true && control.IsChecked != true && !allow.IsEnabled, "new request has no selected permission");
                        Check(((CheckBox)window.FindName("AssistanceTrustDeviceCheck")).IsChecked != true,
                            "temporary requests do not silently become trusted");
                        view.IsChecked = true;
                        Check(allow.IsEnabled && control.IsChecked != true, "view selection enables explicit approval");
                        render.Invoke(window, null);
                        Check(view.IsChecked == true, "status refresh preserves selection for same request");
                        Check(!requests.Any(r => r.Command.StartsWith("assistance-approve-", StringComparison.Ordinal)), "choosing a permission alone does not grant access");
                        sessionId = Guid.NewGuid().ToString("N");
                        privateStatus.SetValue(window, Status()); render.Invoke(window, null);
                        Check(view.IsChecked != true && control.IsChecked != true && !allow.IsEnabled, "replacement request cannot inherit previous permission");
                    }
                    var content = (FrameworkElement)window.Content;
                    var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var image = File.Create(Path.Combine(root, state + (sample.Item4 == 1 ? "-light" : "-dark") + ".png")); encoder.Save(image);
                }
                pages.SelectedItem = window.FindName("HomePage"); window.UpdateLayout();
                var home = (FrameworkElement)window.FindName("HomeLocalBadge");
                Check(((Border)window.FindName("HomeDeviceCard")).Style == window.FindResource("DeviceCardFrame") && home.IsVisible,
                    "local computer uses the same card frame as bound devices");
                var contentPeer = UIElementAutomationPeer.CreatePeerForElement((UIElement)window.FindName("Pages"))!;
                Check(contentPeer.GetChildren()?.Count > 0, "page content remains exposed to automation");
                foreach (var mode in new[] { "view", "control" })
                {
                    state = "pending"; sessionId = "approve-" + mode;
                    privateStatus.SetValue(window, Status()); render.Invoke(window, null);
                    var choice = (RadioButton)window.FindName(mode == "view" ? "AssistanceViewPermission" : "AssistanceControlPermission");
                    choice.IsChecked = true;
                    ((CheckBox)window.FindName("AssistanceTrustDeviceCheck")).IsChecked = mode == "control";
                    ((Button)window.FindName("AssistanceAllowButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(requests.Last(r => r.Command.StartsWith("assistance-approve-", StringComparison.Ordinal)) is var approval &&
                        approval.Command == "assistance-approve-" + mode && approval.RequestId == sessionId &&
                        approval.TrustDevice == (mode == "control"), "explicit approval sends chosen permission, trust choice and current request: " + mode);
                }
                state = "idle"; privateStatus.SetValue(window, Status()); render.Invoke(window, null);
                int starts = requests.Count(r => r.Command == "assistance-enable");
                var toggle = (CheckBox)window.FindName("AssistanceEnabledToggle");
                toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(requests.Count(r => r.Command == "assistance-enable") == starts + 1 && toggle.IsChecked == true, "availability setting enables assistance and updates its state");
                ((Button)window.FindName("AssistanceCodeVisibilityButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(((TextBlock)window.FindName("AssistanceCodeText")).Text == "****-****", "eye button can hide the current code");
                ((Button)window.FindName("AssistanceRefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(requests.Last(r => r.Command == "assistance-refresh") is not null, "refresh has its own visible action");
                toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(requests.Last(r => r.Command == "assistance-enable").AssistanceEnabled == false && toggle.IsChecked == false, "availability off is saved independently of the active session");
                failStart = true; toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(toggle.IsChecked == false, "failed start restores actual disabled state");
                var initialize = typeof(MainWindow).GetMethod("InitializeAssistanceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
                await (Task)initialize.Invoke(window, null)!;
                int loginRequests = requests.Count(r => r.Command == "assistance-login");
                await (Task)initialize.Invoke(window, null)!;
                Check(loginRequests == 1 && requests.Count(r => r.Command == "assistance-login") == 1,
                    "owner UI registers one login without repeatedly rotating codes on refresh");
                string connection = "icontrol-help:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
                    new AssistanceConnection(1, ["192.168.1.20"], 49199, new string('A', 64)), RuntimeProtocol.JsonOptions));
                string invitation = AssistanceClient.CreateInvitation(connection, "7F3K-92QX");
                var importInvitation = typeof(MainWindow).GetMethod("ImportAssistanceInvitation", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var parsed = AssistanceClient.ParseConnection(invitation);
                Check(parsed.Addresses.Single() == "192.168.1.20" && parsed.Pin == new string('A', 64), "sharing preserves pinned host identity");
                ((TextBox)window.FindName("AssistanceConnectionBox")).Text = invitation;
                Check(((TextBox)window.FindName("AssistanceJoinCodeBox")).Text == "7F3K-92QX" && ((Button)window.FindName("AssistanceJoinButton")).IsEnabled,
                    "pasted invitation fills code and enables connection");
                int requestsBeforePaste = requests.Count;
                ((TextBox)window.FindName("AssistanceJoinCodeBox")).Text = "ABCD-EFGH";
                Check((bool)importInvitation.Invoke(window, [invitation])! &&
                    ((TextBox)window.FindName("AssistanceJoinCodeBox")).Text == "7F3K-92QX",
                    "pasting the same invitation restores its code after a manual edit");
                Check(!(bool)importInvitation.Invoke(window, ["192.168.1.20"])! &&
                    ((TextBox)window.FindName("AssistanceConnectionBox")).Text == invitation &&
                    ((TextBox)window.FindName("AssistanceJoinCodeBox")).Text == "7F3K-92QX",
                    "invalid clipboard text does not erase the current invitation");
                Check(requests.Count == requestsBeforePaste, "pasting never starts a session or grants a permission");
                ((TextBox)window.FindName("AssistanceConnectionBox")).Text = connection;
                Check(((TextBox)window.FindName("AssistanceJoinCodeBox")).Text == "" && !((Button)window.FindName("AssistanceJoinButton")).IsEnabled,
                    "changing shared host clears imported code and requires a new code");
                ((TextBox)window.FindName("AssistanceJoinCodeBox")).Text = "7F3K-92QX";
                Check(((Button)window.FindName("AssistanceJoinButton")).IsEnabled, "manual code uses the current parsed host");
                ((TextBox)window.FindName("AssistanceConnectionBox")).Text = "192.168.1.20";
                Check(!((Button)window.FindName("AssistanceJoinButton")).IsEnabled &&
                    ((TextBlock)window.FindName("AssistanceConnectionHint")).Visibility == Visibility.Visible,
                    "invalid host invalidates the cached invitation and displays inline feedback");
                ((TextBox)window.FindName("AssistanceConnectionBox")).Clear();
                Check(((TextBlock)window.FindName("AssistanceConnectionHint")).Visibility == Visibility.Collapsed,
                    "empty input has no persistent warning");
                ((TextBox)window.FindName("AssistanceConnectionBox")).Text = connection;
                Check(((Button)window.FindName("AssistanceJoinButton")).IsEnabled,
                    "valid host edit reuses the manually entered code");
                ((Button)window.FindName("AssistanceNetworkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(pages.SelectedItem == window.FindName("SettingsPage") &&
                    ((TabControl)window.FindName("SettingsCategories")).SelectedItem == window.FindName("NetworkSettingsCategory") &&
                    ((Expander)window.FindName("NetworkSetupExpander")).IsExpanded,
                    "network setup is reachable in one action with connection controls expanded");
                Check(!requests.Any(r => r.Command is "network-takeover" or "network-login"),
                    "opening network settings does not change the network or log in silently");
                var renderNetwork = typeof(MainWindow).GetMethod("RenderAssistanceNetwork", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var networkField = typeof(MainWindow).GetField("_network", BindingFlags.NonPublic | BindingFlags.Instance)!;
                privateStatus.SetValue(window, Status() with { Network = new(true, true, null, "fixture") });
                foreach (string networkState in new[] { "NeedsLogin", "NeedsMachineAuth", "Running" })
                {
                    networkField.SetValue(window, new NetworkStatus { Available = true, State = networkState,
                        LoggedIn = networkState == "Running", StateText = networkState == "NeedsMachineAuth" ? "等待账号管理员批准设备" : "fixture" });
                    renderNetwork.Invoke(window, null);
                    string text = ((TextBlock)window.FindName("AssistanceNetworkState")).Text;
                    Check(networkState switch {
                        "NeedsLogin" => text.Contains("未登录"),
                        "NeedsMachineAuth" => text.Contains("等待账号管理员批准"),
                        _ => text.Contains("已连接") && !text.Contains("直连")
                    }, "first-use network status describes the actual state: " + networkState);
                }
                networkField.SetValue(window, null);
                privateStatus.SetValue(window, Status()); render.Invoke(window, null);
                var invite = typeof(MainWindow).GetMethod("InviteReceiverAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
                ((ComboBox)window.FindName("OrientationBox")).SelectedIndex = 4;
                await (Task)invite.Invoke(window, ["test-tablet", "tablet-credential"])!;
                Check(requests.Last(r => r.Command == "tablet-request") is { AutoOrientation: true, ClientId: "test-tablet" },
                    "automatic option sends automatic orientation to the selected bound tablet");
                ((ComboBox)window.FindName("OrientationBox")).SelectedIndex = 1;
                await (Task)invite.Invoke(window, ["test-tablet", "tablet-credential"])!;
                Check(requests.Last(r => r.Command == "tablet-request") is { AutoOrientation: false, Orientation: 90 },
                    "fixed option keeps its angle and does not inherit automatic mode");
                File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new {
                    checkedAt = DateTimeOffset.Now, passed = true, count, installedServiceCalls = 0,
                    realInputSent = false, samples = new[] { "idle-light-940x660", "offered-light-1080x760", "pending-light-940x660", "active-dark-1080x760", "offered-dark-940x660", "pending-dark-1080x760" }
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"{count} assistance UI checks passed.");
            }
            catch (Exception error)
            {
                exitCode = 1; Console.WriteLine(error);
                File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = false, error = error.Message }));
            }
            finally { window.DisposeWindow(); app.Shutdown(); }
        }));
        app.Run(window); return exitCode;
    }
}
