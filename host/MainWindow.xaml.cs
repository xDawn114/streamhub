using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using StreamHub.Host.Display;
using StreamHub.Host.Networking;
using StreamHub.Host.Runtime;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace StreamHub.Host;

public partial class MainWindow : Window
{
    private readonly RuntimeClient _runtime;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly CancellationTokenSource _closing = new();
    private RuntimeStatus? _status;
    private NetworkStatus? _network;
    private bool _refreshing, _busy;
    private bool _renderingPrivacy;
    private bool _detailsRefreshing;
    private DateTimeOffset _lastDetailsRefresh;
    private string _lastTraySessionState = "initial";
    partial void RefreshDevicePresentation();
    private string[] _addresses = [];
    private TabletConnectionRequest? _tabletRequest;
    private static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StreamHub");
    private static readonly string InstallRoot = AppContext.BaseDirectory;

    public MainWindow() : this(new RuntimeClient()) { }
    public MainWindow(RuntimeClient runtime, string? uiPreferencesPath = null)
    {
        _runtime = runtime;
        _uiPreferencesPath = uiPreferencesPath ?? DefaultUiPreferencesPath;
        InitializeComponent();
        InitializeDesktopFeedback();
        Title = "我控制";
        InitializeUpdates();
        ComputerNameText.Text = Environment.MachineName;
        AutoStartCheck.IsChecked = GetAutoStart();
        _timer.Tick += async (_, _) => { if (IsVisible) await RefreshStatusAsync(); };
        Loaded += async (_, _) => {
            // Navigation's two-way selection bindings can clear the implicit
            // first-tab selection during initialization. Establish it explicitly.
            if (Pages.SelectedItem is null) Pages.SelectedItem = HomePage;
            _timer.Start(); await RefreshStatusAsync();
        };
    }

    public async void RefreshStatusFromTray() => await RefreshStatusAsync();
    public async void StopServicesFromTray() => await StopAllAsync();
    public async void RecoverPrivacyScreenFromTray() => await RecoverPrivacyScreenAsync();
    public async void DisconnectSessionFromTray() => await RunAction(async () =>
        await CommandAsync(new("session-disconnect")));
    public void DisposeWindow() { _bindingCountdown.Stop(); _feedbackTimer.Stop(); CloseTransientPopovers(); _timer.Stop(); _updateTimer.Stop(); _updateOperation?.Cancel(); _closing.Cancel(); _closing.Dispose(); }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (System.Windows.Application.Current is App app && !app.IsExiting) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }

    private async Task RefreshStatusAsync(bool force = false)
    {
        if (_refreshing || (_busy && !force) || _closing.IsCancellationRequested) return;
        _refreshing = true;
        try
        {
            var result = await _runtime.GetStatusAsync(_closing.Token);
            _status = result.Success ? result.Status : null;
            await InitializeAssistanceAsync();
            RefreshPowerSettings();
            RefreshHostPolicySettings(_status?.HostPolicy);
            if (Pages.SelectedItem == SettingsPage) _ = ReadNetworkAccessAsync();
            var sessionState = SessionNotificationPolicy.State(_status);
            var sessionNotice = SessionNotificationPolicy.Transition(_lastTraySessionState, sessionState);
            _lastTraySessionState = sessionState;
            if (sessionNotice is not null && System.Windows.Application.Current is App app)
                app.ShowSessionNotification(sessionNotice.Title, sessionNotice.Message);
            var local = GetLocalLinks();
            _addresses = local.SelectMany(x => x.Addresses).Distinct().ToArray();
            ComputerNameText.Text = _status?.DeviceName ?? Environment.MachineName;
            DeviceIdText.Text = _status is null ? "后台服务未连接" : $"设备标识  {_status.DeviceId}";
            AddressText.Text = _addresses.Length == 0 ? "尚未发现可用的本机 IPv4 地址" : "本机地址  " + string.Join("  /  ", _addresses);
            _renderingPrivacy = true;
            try
            {
                PrivacyModeCheck.IsChecked = _status?.PrivacyScreenEnabled == true;
                PrivacyModeCheck.IsEnabled = _status is not null && !_busy;
                PrivacyRecoverButton.IsEnabled = _status?.PrivacyScreenActive == true && !_busy;
            }
            finally { _renderingPrivacy = false; }
            if (_status is null)
            {
                _network = null;
                NetworkStateText.Text = "等待后台服务";
                NetworkDetailText.Text = result.Message;
                PeerList.ItemsSource = null;
                PeerEmptyText.Visibility = Visibility.Visible;
            }
            RefreshDevicePresentation();
            RenderAssistance();
            _ = RefreshLocalBindingAsync();
            if (!_detailsRefreshing && DateTimeOffset.UtcNow - _lastDetailsRefresh >= TimeSpan.FromSeconds(10))
                _ = RefreshDetailsAsync();
            var first = _addresses.FirstOrDefault() ?? "电脑IPv4地址";
            ConnectionHelpText.Text = $"远程主屏  {first}:48989\n平板副屏  {first}:49089";
            DiagnosticsText.Text = BuildDiagnostics(local, result);
            if (!_busy) UpdateStatusFeedback(result.Success, result.Message);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowFeedback("读取状态失败，请重试：" + ex.Message, error: true); }
        finally { _refreshing = false; RefreshServiceControls(); }
    }


    private async Task RefreshDetailsAsync()
    {
        _detailsRefreshing = true;
        _lastDetailsRefresh = DateTimeOffset.UtcNow;
        try
        {
            if (_status is not null)
            {
                await RefreshFilesAsync();
                await ReadControlDevicesAsync();
                var configResult = await _runtime.SendAsync(new("network-config"), _closing.Token);
                if (configResult.Success && configResult.PayloadJson is not null)
                    RenderTransport(JsonSerializer.Deserialize<TransportSettings>(configResult.PayloadJson, RuntimeProtocol.JsonOptions));
                else { _transportConfig = null; ClearLoginUrl(); RemoteNetworkActions.IsEnabled = false; ProviderSavedText.Text = configResult.Message; ProviderSavedText.Visibility = Visibility.Visible; }
                var networkResult = await _runtime.SendAsync(new("network-status"), _closing.Token);
                _network = !string.IsNullOrEmpty(networkResult.PayloadJson)
                    ? JsonSerializer.Deserialize<NetworkStatus>(networkResult.PayloadJson, RuntimeProtocol.JsonOptions) : null;
                RenderNetwork(networkResult);
                await RefreshReceiversAsync();
            }
            var control = (System.Windows.Application.Current as App)?.ControlApi;
            if (control is not null) await control.RefreshAsync(_closing.Token);
            ControlApiStateText.Text = control?.IsRunning == true ? "" : control?.LastError ?? "设备绑定接口尚未启动。";
            ControlApiStateText.Visibility = string.IsNullOrWhiteSpace(ControlApiStateText.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ControlApiStateText.Text = "设备详情刷新失败：" + ex.Message; ControlApiStateText.Visibility = Visibility.Visible; }
        finally { _detailsRefreshing = false; }
    }

    private static void RenderEngine(TextBlock title, TextBlock detail, EngineStatus? engine)
    {
        title.Text = SessionStatusPresentation.Engine(engine);
        detail.Text = engine?.Message ?? "服务不可用";
    }

    private void RenderNetwork(RuntimeResponse response)
    {
        NetworkStateText.Text = _network?.StateText ?? (_status?.Network.Running == true ? "网络内核启动中" : "内置连接尚未启用");
        var lines = new List<string>();
        if (_network?.LoggedIn == true) lines.Add("已登录  " + (string.IsNullOrWhiteSpace(_network.UserName) ? _network.LoginName : _network.UserName));
        if (_network?.Addresses.Length > 0) lines.Add("远程地址  " + string.Join(" / ", UsableIpv4.Filter(_network.Addresses)));
        if (_network?.Health.Length > 0) lines.AddRange(_network.Health);
        if (!string.IsNullOrWhiteSpace(_network?.Error)) lines.Add(_network.Error);
        if (lines.Count == 0) lines.Add(_status?.Network.Message ?? response.Message);
        NetworkDetailText.Text = string.Join(Environment.NewLine, lines);
        PeerList.ItemsSource = _network?.Peers.Select(p => new { p.Name, Address = string.Join(" / ", UsableIpv4.Filter(p.Addresses)), State = p.Online ? "在线 · " + p.RouteText : "离线" }).ToArray();
        PeerEmptyText.Visibility = _network?.Peers.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshServiceControls();
        ClearLoginUrl();
        if (!string.IsNullOrWhiteSpace(_network?.BrowseUrl)) SetLoginUrl(_network.BrowseUrl);
        RefreshNetworkLoginButton();
        if (!_networkLoginInProgress && _network?.LoggedIn == true && _network.State == "Running")
            NetworkLoginProgressText.Visibility = Visibility.Collapsed;
        RenderAssistanceNetwork();
    }

    private async Task RunAction(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        Pages.IsEnabled = false;
        ShowFeedback("正在处理，请稍候…", persistent: true, remember: false);
        RefreshServiceControls();
        try { await action(); }
        catch (Exception ex) { ShowFeedback("操作未完成：" + ex.Message, error: true); }
        finally { _busy = false; Pages.IsEnabled = true; if (FeedbackText.Text == "正在处理，请稍候…") DismissFeedback(); await RefreshStatusAsync(true); }
    }

    private async Task CommandAsync(RuntimeRequest request)
    {
        var result = await _runtime.SendAsync(request, _closing.Token);
        ShowFeedback(result.Message, error: !result.Success);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) { await RefreshStatusAsync(); if (_status is not null) ShowFeedback("本机状态已更新。"); }
    private async void StartEngine_Click(object sender, RoutedEventArgs e)
    {
        var profile = (string)((Button)sender).Tag;
        if (profile == "tablet") { OpenDeviceCards_Click(sender, e); await RefreshStatusAsync(); return; }
        await RunAction(async () =>
        {
            await CommandAsync(new("start", profile));
        });
    }
    private async void PrivacyMode_Click(object sender, RoutedEventArgs e)
    {
        if (_renderingPrivacy || _status is null) return;
        var enabled = PrivacyModeCheck.IsChecked == true;
        if (enabled &&
            MessageBox.Show("远控连接出画后，本机屏幕会显示黑屏。按 Ctrl+Alt+F12 可立即恢复本机屏幕并关闭隐私保护。确定开启？", "远控隐私保护",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            _renderingPrivacy = true;
            PrivacyModeCheck.IsChecked = false;
            _renderingPrivacy = false;
            return;
        }
        await RunAction(() => CommandAsync(new("privacy-set", PrivacyEnabled: enabled)));
    }
    private async void PrivacyRecoverButton_Click(object sender, RoutedEventArgs e) => await RecoverPrivacyScreenAsync();
    private async Task RecoverPrivacyScreenAsync() => await RunAction(() => CommandAsync(new("privacy-recover")));
    private async void StopEngine_Click(object sender, RoutedEventArgs e) => await StopProfileAsync((string)((Button)sender).Tag);
    private async Task StopProfileAsync(string profile)
    {
        var engine = profile == "remote" ? _status?.Remote : _status?.Tablet;
        if (engine?.Streaming == true && MessageBox.Show("这会中断正在进行的串流，确认停止？", "停止串流", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { RefreshServiceControls(); return; }
        await RunAction(async () =>
        {
            if (profile == "tablet" && _tabletRequest is { } request && TabletRequestRegistry.Active(request.State))
                await _runtime.SendAsync(new("tablet-request-cancel", RequestId: request.Id), _closing.Token);
            var result = await _runtime.SendAsync(new("stop", profile), _closing.Token);
            ShowFeedback(result.Message, error: !result.Success);
            if (!result.Success)
            {
                return;
            }
            // The service owns shutdown and verifies the virtual display is detached.
        });
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        var pin = PairPin.Text.Trim();
        if (pin.Length != 4 || pin.Any(c => c < '0' || c > '9')) { ShowFeedback("请输入接收端显示的四位数字。", error: true); PairPin.Focus(); return; }
        var profile = (string)((ComboBoxItem)PairMode.SelectedItem).Tag;
        await RunAction(async () => {
            var result = await _runtime.SendAsync(new("pair", profile, pin, PairName.Text.Trim()), _closing.Token);
            ShowFeedback(result.Message, error: !result.Success);
            if (result.Success) PairPin.Clear();
        });
    }
    private void CopyDeviceBinding_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var control = (System.Windows.Application.Current as App)?.ControlApi ?? throw new InvalidOperationException("设备绑定接口尚未启动。");
            CopyText(control.GetInvitation(), "设备绑定码已复制");
        }
        catch (Exception ex) { ShowFeedback(ex.Message, error: true); }
    }

    private async Task EnableNetworkAsync()
    {
        OpenAccount_Click(this, new RoutedEventArgs());
        await RunAction(LoginNetworkAsync);
    }
    private async void NetworkRestore_Click(object sender, RoutedEventArgs e) => await RunAction(() => CommandAsync(new("network-restore")));
    private async void NetworkLogin_Click(object sender, RoutedEventArgs e) => await RunAction(LoginNetworkAsync);
    private Task LoginNetworkAsync() => RunNetworkLoginAsync(NetworkHandover.IsLegacyRunning, () =>
        MessageBox.Show(this, "登录需要切换到内置连接，当前 Tailscale 连接会中断。是否继续？", "切换连接",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes);

    private bool _networkLoginInProgress;
    private async Task RunNetworkLoginAsync(Func<bool> takeoverRequired, Func<bool> confirmTakeover)
    {
        if (_networkLoginInProgress) return;
        _networkLoginInProgress = true;
        ClearLoginUrl();
        try
        {
            SetNetworkLoginProgress("正在准备连接…");
            var config = await _runtime.SendAsync(new("network-config"), _closing.Token);
            if (!config.Success) { SetNetworkLoginProgress(config.Message, error: true); return; }
            var settings = JsonSerializer.Deserialize<TransportSettings>(config.PayloadJson ?? "null", RuntimeProtocol.JsonOptions);
            if (settings is null) { SetNetworkLoginProgress("未能读取连接方式，请重试。", error: true); return; }
            RenderTransport(settings);
            if (!settings.Transport.UsesAccount) { SetNetworkLoginProgress("当前连接方式无需登录账号。"); return; }
            var current = await _runtime.GetStatusAsync(_closing.Token);
            if (!current.Success || current.Status is null) { SetNetworkLoginProgress(current.Message, error: true); return; }
            _status = current.Status;
            if (!current.Status.Network.Running)
            {
                _network = null;
                bool takeover = takeoverRequired();
                if (takeover && !confirmTakeover()) { SetNetworkLoginProgress("已取消登录。"); return; }
                SetNetworkLoginProgress("正在启动内置连接…");
                var started = await _runtime.SendAsync(new(takeover ? "network-takeover" : "network-start"), _closing.Token);
                if (!started.Success) { SetNetworkLoginProgress(started.Message, error: true); return; }
            }
            SetNetworkLoginProgress("正在获取登录链接…");
            var response = await _runtime.SendAsync(new("network-login"), _closing.Token);
            if (!response.Success) { SetNetworkLoginProgress(response.Message, error: true); return; }
            var status = JsonSerializer.Deserialize<NetworkStatus>(response.PayloadJson ?? "null", RuntimeProtocol.JsonOptions);
            if (status is null) { SetNetworkLoginProgress("未能取得登录链接，请重试。", error: true); return; }
            _network = status;
            if (status.LoggedIn && status.State == "Running") { SetNetworkLoginProgress("当前账号已登录。"); return; }
            if (string.IsNullOrWhiteSpace(status.BrowseUrl)) { SetNetworkLoginProgress("暂时无法获取登录链接，请稍后重试。", error: true); return; }
            if (!SetLoginUrl(status.BrowseUrl)) { SetNetworkLoginProgress("登录链接与当前连接方式不匹配，请重试。", error: true); return; }
            OpenTarget(LoginUrlBox.Text);
            SetNetworkLoginProgress("等待浏览器完成登录。");
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception error) { SetNetworkLoginProgress("登录未完成：" + error.Message, error: true); }
        finally { _networkLoginInProgress = false; RefreshNetworkLoginButton(); }
    }
    private void SetNetworkLoginProgress(string message, bool error = false)
    {
        if (error) _network = null;
        NetworkLoginProgressText.Text = message;
        NetworkLoginProgressText.SetResourceReference(TextBlock.ForegroundProperty, error ? "DangerText" : "Muted");
        NetworkLoginProgressText.Visibility = Visibility.Visible;
        if (error) ShowFeedback(message, error: true);
        RefreshNetworkLoginButton();
    }
    private void RefreshNetworkLoginButton()
    {
        bool loggedIn = _transportConfig?.Transport.UsesAccount == true && _network?.LoggedIn == true &&
            _network.State == "Running";
        NetworkLoginButton.Content = _networkLoginInProgress ? "正在登录…" : loggedIn ? "已登录" : "登录账号";
        NetworkLoginButton.IsEnabled = !_networkLoginInProgress && !loggedIn && _transportConfig?.Transport.UsesAccount == true;
    }
    private bool SetLoginUrl(string url)
    {
        ClearLoginUrl();
        if (_transportConfig is null) return false;
        var safe = HubTransports.AuthorizationUrl(_transportConfig, url);
        if (safe.Length == 0) return false;
        LoginUrlBox.Text = safe;
        LoginUrlBox.Visibility = CopyLoginButton.Visibility = LoginBrowserHint.Visibility = Visibility.Visible;
        return true;
    }
    private void CopyLogin_Click(object sender, RoutedEventArgs e) => CopyText(LoginUrlBox.Text, "授权地址已复制，可在浏览器完成授权。");
    private void CopyAddress_Click(object sender, RoutedEventArgs e) => CopyText(string.Join(Environment.NewLine, _addresses.Select(x => x + ":48989")), "远程主屏连接地址已复制。");
    private void CopyText(string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value)) { ShowFeedback("暂无可复制内容"); return; }
        try { System.Windows.Clipboard.SetText(value); ShowFeedback(message); }
        catch (Exception ex) { ShowFeedback("无法访问剪贴板：" + ex.Message, error: true); }
    }

    private async Task RefreshReceiversAsync()
    {
        var response = await _runtime.SendAsync(new("receiver-list"), _closing.Token);
        if (!response.Success || response.PayloadJson is null)
        {
            _receiverSnapshot = null;
            RenderUnifiedDevices();
            return;
        }
        RenderReceivers(JsonSerializer.Deserialize<ReceiverSnapshot>(response.PayloadJson, RuntimeProtocol.JsonOptions));
    }

    private void RenderReceivers(ReceiverSnapshot? snapshot)
    {
        if (snapshot is null) return;
        _receiverSnapshot = snapshot;
        _tabletRequest = snapshot.Request;
        RefreshServiceControls();
    }

    private void OpenDisplaySettings_Click(object sender, RoutedEventArgs e) => OpenTarget("ms-settings:display");

    private static bool GetAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("我控制") is string || key?.GetValue("梨子控制") is string || key?.GetValue("串流中心") is string;
    }
    private void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            key.DeleteValue("StreamHub", false);
            key.DeleteValue("串流中心", false);
            key.DeleteValue("梨子控制", false);
            if (AutoStartCheck.IsChecked == true) key.SetValue("我控制", "\"" + Environment.ProcessPath + "\" --background");
            else key.DeleteValue("我控制", false);
            ShowFeedback(AutoStartCheck.IsChecked == true ? "下次登录 Windows 后将显示托盘入口。" : "已关闭托盘自启，后台服务仍随系统启动。");
        }
        catch (Exception ex) { AutoStartCheck.IsChecked = GetAutoStart(); ShowFeedback("无法保存启动设置：" + ex.Message, error: true); }
    }
    private async void StopAll_Click(object sender, RoutedEventArgs e) => await StopAllAsync();
    private async Task StopAllAsync()
    {
        if (MessageBox.Show("停止主屏、副屏和异地连接？", "停止远程服务", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAction(async () => {
            var failures = new List<string>();
            var remote = await _runtime.SendAsync(new("stop", "remote"), _closing.Token);
            if (!remote.Success) failures.Add("主屏：" + remote.Message);
            var tablet = await _runtime.SendAsync(new("stop", "tablet"), _closing.Token);
            if (!tablet.Success) failures.Add("副屏：" + tablet.Message);
            else ShowFeedback(DisplayController.DisableExtended());
            var network = await _runtime.SendAsync(new("network-restore"), _closing.Token);
            if (!network.Success) failures.Add("异地连接：" + network.Message);
            if (failures.Count > 0)
            {
                ShowFeedback("部分停止操作未完成：" + string.Join("；", failures), error: true);
            }
            else
            {
                ShowFeedback("已停止主屏、副屏和异地连接，副屏显示已停用。");
            }
        });
    }

    private string BuildDiagnostics(LocalLink[] links, RuntimeResponse result)
    {
        var lines = new List<string> { $"我控制 · 检查时间 {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}", "安装位置：" + InstallRoot,
            "后台服务：" + (_status is null ? result.Message : "运行中，版本 " + _status.Version),
            "主屏串流文件：" + (File.Exists(Path.Combine(InstallRoot, "components", "streaming", "remote", "sunshine.exe")) ? "存在" : "缺失"),
            "副屏串流文件：" + (File.Exists(Path.Combine(InstallRoot, "components", "streaming", "tablet", "sunshine.exe")) ? "存在" : "缺失"),
            "内置网络文件：" + (File.Exists(Path.Combine(InstallRoot, "components", "network", "tailscaled.exe")) ? "存在" : "缺失"),
            "主屏：" + (_status?.Remote.Message ?? "不可用"), "副屏：" + (_status?.Tablet.Message ?? "不可用"),
            "异地连接：" + (_network?.StateText ?? _status?.Network.Message ?? "不可用"),
            "远控隐私保护：" + (_status?.PrivacyScreenMessage ?? "不可用"), "显示：" + DisplayController.Describe() };
        foreach (var link in links) lines.Add($"{(link.Usb ? "USB 网络" : "本机网络")}：{link.Name}  {string.Join(" / ", link.Addresses)}");
        lines.Add("诊断摘要不包含账号密钥、配对码或授权链接。");
        return string.Join(Environment.NewLine, lines);
    }
    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "保存诊断摘要", FileName = "我控制-诊断-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt", Filter = "文本文件|*.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, DiagnosticsText.Text, new UTF8Encoding(true)); ShowFeedback("诊断摘要已保存到所选位置。"); }
        catch (Exception ex) { ShowFeedback("保存失败：" + ex.Message, error: true); }
    }
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenTarget(Path.Combine(DataRoot, "logs"));
    private void OpenLicenses_Click(object sender, RoutedEventArgs e) => OpenTarget(Path.Combine(InstallRoot, "许可与来源.txt"));
    private void OpenTarget(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { ShowFeedback("无法打开：" + ex.Message, error: true); }
    }
    private sealed record LocalLink(string Name, bool Usb, string[] Addresses);
    private static LocalLink[] GetLocalLinks() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .Select(n => new LocalLink(n.Name, (n.Description + " " + n.Name).Contains("RNDIS", StringComparison.OrdinalIgnoreCase)
            || n.Description.Contains("USB", StringComparison.OrdinalIgnoreCase) || n.Description.Contains("Remote NDIS", StringComparison.OrdinalIgnoreCase),
            UsableIpv4.Filter(n.GetIPProperties().UnicastAddresses.Select(a => a.Address.ToString()))))
        .Where(n => n.Addresses.Length > 0).ToArray();
}
