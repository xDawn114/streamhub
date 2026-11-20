using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using StreamHub.Host;
using StreamHub.Host.Networking;
using StreamHub.Host.Runtime;

internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        string root = Path.GetFullPath(args.Single()); Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var checks = new List<string>(); var requests = new List<RuntimeRequest>();
        var policy = new HostPolicySnapshot(Revision: 5, AutomaticLoginAvailable: true);
        var access = new NetworkAccessState(false, true, true, true, true, false, [], [], "fixture", null);
        bool failPolicy = false, failAccess = false;
        bool failTransport = false;
        var transport = new TransportSettings("lan");
        TaskCompletionSource<RuntimeResponse>? pendingAccess = null;
        void Check(bool result, string name) { if (!result) throw new Exception(name); checks.Add(name); }
        foreach (string state in new[] { "remote-verifying", "remote-streaming", "both-streaming" })
            Check(SessionNotificationPolicy.Transition("idle", state) is null, "main desktop streaming has no captured informational toast: " + state);
        Check(SessionNotificationPolicy.Transition("idle", "tablet-streaming") is not null, "secondary display notice remains available");
        Check(SessionNotificationPolicy.Transition("remote-streaming", "idle") is not null, "ended stream notice remains available");
        var runtime = new RuntimeClient((request, _) =>
        {
            requests.Add(request);
            if (request.Command == "network-provider")
            {
                bool failure = failTransport; failTransport = false;
                if (!failure) transport = new TransportSettings(request.Provider!,request.ServerUrl!,request.ManualAddresses!).Validate();
                return Task.FromResult(new RuntimeResponse(!failure,failure ? "模拟通道失败" : "通道已应用"));
            }
            if (request.Command == "host-policy-save")
            {
                Check(request.PolicyRevision == policy.Revision, "policy change uses confirmed revision");
                bool failure = failPolicy; failPolicy = false;
                policy = policy with { Revision = policy.Revision + 1, LockOnExit = failure ? policy.LockOnExit : request.LockOnExit ?? policy.LockOnExit };
                return Task.FromResult(new RuntimeResponse(!failure, failure ? "模拟策略冲突" : "策略已应用", PayloadJson: JsonSerializer.Serialize(policy, RuntimeProtocol.JsonOptions)));
            }
            if (request.Command == "network-access-save")
            {
                if (failAccess) { failAccess = false; return Task.FromResult(new RuntimeResponse(false, "模拟网络应用失败")); }
                access = access with { AllowPublicLan = request.AllowPublicLan == true };
                pendingAccess = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return pendingAccess.Task;
            }
            string? payload = request.Command switch
            {
                "host-policy-status" => JsonSerializer.Serialize(policy, RuntimeProtocol.JsonOptions),
                "network-access-status" => JsonSerializer.Serialize(access, RuntimeProtocol.JsonOptions),
                "network-config" => JsonSerializer.Serialize(transport, RuntimeProtocol.JsonOptions),
                "control-device-list" => JsonSerializer.Serialize(new ControlDeviceList(false, []), RuntimeProtocol.JsonOptions),
                "files-status" => JsonSerializer.Serialize(new TransferSnapshot(new(), [], []), RuntimeProtocol.JsonOptions),
                "binding-status" => JsonSerializer.Serialize(new LocalBindingSnapshot(null, null, null), RuntimeProtocol.JsonOptions),
                _ => null
            };
            var status = request.Command == "status" ? new RuntimeStatus("fixture", "示例电脑",
                new("remote", true, true, null, 48989, true, "待机"), new("tablet", false, false, null, 49089, false, "停止"),
                new(false, false, null, "停止"), "fixture", root, DateTimeOffset.UtcNow, HostPolicy: policy) : null;
            return Task.FromResult(new RuntimeResponse(true, "读取完成", status, payload));
        });
        string preferences = Path.Combine(root, "preferences.json");
        File.WriteAllText(preferences, "{\"Theme\":\"light\",\"ReduceMotion\":true}");
        var window = new MainWindow(runtime, preferences) { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        T N<T>(string name) where T : class => (T)window.FindName(name);
        T F<T>(string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        Task Invoke(string method, params object[] values) => (Task)typeof(MainWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, values)!;
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            try
            {
                N<TabControl>("Pages").SelectedItem = N<TabItem>("SettingsPage");
                var tabs = N<TabControl>("SettingsCategories"); tabs.SelectedIndex = 1;
                await Invoke("ReadHostPolicySettingsAsync");
                var toggle = F<CheckBox>("_hostPolicyLock");
                toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(policy.LockOnExit && toggle.IsChecked == true, "policy switch applies without save button");
                failPolicy = true; toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(policy.LockOnExit && toggle.IsChecked == true, "failed policy change reverts to confirmed state");
                Check(N<TextBlock>("FeedbackText").Text.Contains("模拟策略冲突"), "failed policy change remains visible");
                await Invoke("ReadNetworkAccessAsync");
                var publicNetwork = N<CheckBox>("PublicLanAccessCheck");
                Task saving = Invoke("ApplyNetworkAccessAsync", true);
                Check(!publicNetwork.IsEnabled && publicNetwork.IsChecked == false, "network switch waits for confirmed apply");
                pendingAccess!.SetResult(new RuntimeResponse(true, "已应用", PayloadJson: JsonSerializer.Serialize(access, RuntimeProtocol.JsonOptions)));
                await saving;
                Check(publicNetwork.IsChecked == true && publicNetwork.IsEnabled, "network switch shows applied state");
                Check(!N<TextBlock>("NetworkAccessStateText").Text.Contains("待确认"), "legacy notice does not contradict active switch");
                failAccess = true; await Invoke("ApplyNetworkAccessAsync", false);
                Check(publicNetwork.IsChecked == true, "failed network change restores confirmed state");
                Check(N<TextBlock>("FeedbackText").Text.Contains("模拟网络应用失败"), "network failure remains visible");
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(tabs)!;
                var tabPeers = peer.GetChildren().Where(p => p.GetAutomationControlType() == AutomationControlType.TabItem).ToList();
                Check(tabPeers.Count == 6, "six tabs exposed to UI Automation");
                foreach (var tabPeer in tabPeers)
                {
                    Check(!string.IsNullOrWhiteSpace(tabPeer.GetName()), "accessible tab has a name");
                    var selection = (ISelectionItemProvider)tabPeer.GetPattern(PatternInterface.SelectionItem);
                    selection.Select();
                    Check(selection.IsSelected && ((TabItem)tabs.SelectedItem).Focusable, "accessible tab supports selection and keyboard focus");
                }
                Check(ReferenceEquals(tabs.Items[tabs.Items.Count-1], N<TabItem>("MaintenanceSettingsCategory")), "maintenance tab is last");
                N<ComboBox>("OrientationBox").SelectedIndex = 1;
                Check(JsonDocument.Parse(File.ReadAllText(preferences)).RootElement.GetProperty("TabletOrientation").GetInt32() == 1, "computer orientation saves immediately");
                N<ComboBox>("OrientationBox").SelectedIndex = 4;
                Check(JsonDocument.Parse(File.ReadAllText(preferences)).RootElement.GetProperty("TabletOrientation").GetInt32() == 4 &&
                    (string)((ComboBoxItem)N<ComboBox>("OrientationBox").SelectedItem).Tag == "auto", "automatic orientation persists separately from the four unchanged fixed indices");
                string previous = File.ReadAllText(preferences);
                Directory.CreateDirectory(preferences + ".tmp");
                N<ComboBox>("OrientationBox").SelectedIndex = 2;
                Check(N<ComboBox>("OrientationBox").SelectedIndex == 4 && File.ReadAllText(preferences) == previous, "failed preference write restores automatic orientation");
                Directory.Delete(preferences + ".tmp");
                Check(!requests.Any(x => x.Command is "tablet-request" or "network-provider"), "layout and direction changes do not start connections");
                N<ComboBox>("ProviderBox").SelectedIndex = 0;
                await Task.Delay(50);
                Check(transport.Provider == "tailscale" && N<Button>("ApplyProviderButton").Visibility == Visibility.Collapsed, "provider without inputs applies immediately");
                failTransport = true;
                N<ComboBox>("ProviderBox").SelectedIndex = 2;
                await Task.Delay(50);
                Check(transport.Provider == "tailscale" && N<ComboBox>("ProviderBox").SelectedIndex == 0, "failed provider change restores confirmed choice");
                N<ComboBox>("ProviderBox").SelectedIndex = 1;
                Check(N<Grid>("TransportDialog").Visibility == Visibility.Visible && N<ComboBox>("ProviderBox").SelectedIndex == 0, "address provider keeps confirmed choice until save");
                N<TextBox>("TransportAddressBox").Text = "http://example.invalid";
                typeof(MainWindow).GetMethod("SaveTransportDialog_Click",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,[window,new RoutedEventArgs()]);
                Check(N<TextBlock>("TransportDialogError").Text.Contains("HTTPS") && transport.Provider == "tailscale", "invalid provider input cannot change configuration");
                typeof(MainWindow).GetMethod("CloseTransportDialog_Click",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,[window,new RoutedEventArgs()]);
                foreach(int theme in new[]{1,2})
                {
                    N<ComboBox>("ThemeChoice").SelectedIndex = theme;
                    var button = new Button { Content = "禁用按钮", Style = (Style)window.FindResource("Primary"), IsEnabled = false };
                    var surface = (Grid)window.Content; surface.Children.Add(button); window.UpdateLayout();
                    var text = ((SolidColorBrush)button.Foreground).Color;
                    button.ApplyTemplate();
                    var background = ((SolidColorBrush)((Border)button.Template.FindName("Frame",button)).Background).Color;
                    double a = Luminance(text), b = Luminance(background);
                    Check((Math.Max(a,b)+.05)/(Math.Min(a,b)+.05)>=3,"disabled primary button contrast meets 3:1");
                    surface.Children.Remove(button);
                }
                File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = true, checks, installedServiceCalls = 0, operatingSystemInputSent = false }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"PASS {checks.Count} UI consistency checks");
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = false, error = error.ToString(), checks }));
                Console.Error.WriteLine(error); Environment.ExitCode = 1;
            }
            finally { window.DisposeWindow(); window.Close(); app.Shutdown(); }
        }));
        window.Show(); app.Run();
    }
    static double Luminance(Color color)
    {
        double Channel(byte channel) { double value=channel/255d; return value<=.04045 ? value/12.92 : Math.Pow((value+.055)/1.055,2.4); }
        return .2126*Channel(color.R)+.7152*Channel(color.G)+.0722*Channel(color.B);
    }
}
