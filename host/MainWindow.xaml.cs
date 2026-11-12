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
        if (ProductVersion.IsInternal) Title = $"我控制 {ProductVersion.Name} · 内测版";
        InitializeUpdates();
        ComputerNameText.Text = Environment.MachineName;
        AutoStartCheck.IsChecked = GetAutoStart();
        _timer.Tick += async (_, _) => { if (IsVisible) await RefreshStatusAsync(); };
        Loaded += async (_, _) => { _timer.Start(); await RefreshStatusAsync(); };
    }

    public async void RefreshStatusFromTray() => await RefreshStatusAsync();
    public async void StopServicesFromTray() => await StopAllAsync();
    public async void RecoverPrivacyScreenFromTray() => await RecoverPrivacyScreenAsync();
    public void DisposeWindow() { _timer.Stop(); _updateTimer.Stop(); _updateOperation?.Cancel(); _closing.Cancel(); _closing.Dispose(); }

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
            RenderEngine(RemoteStateText, RemoteDetailText, _status?.Remote);
            RenderEngine(TabletStateText, TabletDetailText, _status?.Tablet);
            _renderingPrivacy = true;
            try
            {
                PrivacyModeCheck.IsChecked = _status?.PrivacyScreenEnabled == true;
                PrivacyModeCheck.IsEnabled = _status is not null && !_busy;
                PrivacyRecoverButton.IsEnabled = _status is not null && !_busy;
                PrivacyModeStateText.Text = _status?.PrivacyScreenMessage ?? "等待后台服务";
                PrivacyModeStateText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, 
                    _status?.PrivacyScreenActive == true ? "SuccessText" : "Muted");
            }
            finally { _renderingPrivacy = false; }
            HeaderState.Text = SessionStatusPresentation.Header(_status);
            HeaderState.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, _status?.Remote.Healthy == true ? "SuccessText" : "Muted");
            if (_status is null)
            {
                _network = null;
                NetworkStateText.Text = "等待后台服务";
                NetworkDetailText.Text = result.Message;
                PeerList.ItemsSource = null;
                PeerEmptyText.Visibility = Visibility.Visible;
            }
            var usb = local.Where(x => x.Usb).ToArray();
            UsbStateText.Text = usb.Length == 0 ? "当前未发现 USB 共享网卡。" : string.Join(Environment.NewLine, usb.Select(x => $"已发现 {x.Name}：{string.Join(" / ", x.Addresses)}"));
            DisplayStateText.Text = DisplayController.Describe();
            RefreshDevicePresentation();
            _ = RefreshLocalBindingAsync();
            if (!_detailsRefreshing && DateTimeOffset.UtcNow - _lastDetailsRefresh >= TimeSpan.FromSeconds(10))
                _ = RefreshDetailsAsync();
            var first = _addresses.FirstOrDefault() ?? "电脑IPv4地址";
            ConnectionHelpText.Text = $"远程主屏  {first}:48989\n平板副屏  {first}:49089";
            DiagnosticsText.Text = BuildDiagnostics(local, result);
            LastUpdated.Text = "更新于 " + DateTime.Now.ToString("HH:mm:ss");
            if (!_busy && (!result.Success || FeedbackText.Text.StartsWith("正在检查"))) FeedbackText.Text = result.Success ? "本机状态已更新。" : result.Message;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FeedbackText.Text = "读取状态失败，请重试：" + ex.Message; }
        finally { _refreshing = false; }
    }


    private async Task RefreshDetailsAsync()
    {
        _detailsRefreshing = true;
        _lastDetailsRefresh = DateTimeOffset.UtcNow;
        try
        {
            if (_status is not null)
            {
                var configResult = await _runtime.SendAsync(new("network-config"), _closing.Token);
                if (configResult.Success && configResult.PayloadJson is not null)
                    RenderTransport(JsonSerializer.Deserialize<TransportSettings>(configResult.PayloadJson, RuntimeProtocol.JsonOptions));
                else { _transportConfig = null; ClearLoginUrl(); RemoteNetworkActions.IsEnabled = false; ProviderSavedText.Text = configResult.Message; }
                var networkResult = await _runtime.SendAsync(new("network-status"), _closing.Token);
                _network = !string.IsNullOrEmpty(networkResult.PayloadJson)
                    ? JsonSerializer.Deserialize<NetworkStatus>(networkResult.PayloadJson, RuntimeProtocol.JsonOptions) : null;
                RenderNetwork(networkResult);
                await RefreshReceiversAsync();
                await RefreshFilesAsync();
            }
            var control = (System.Windows.Application.Current as App)?.ControlApi;
            if (control is not null) await control.RefreshAsync(_closing.Token);
            ControlApiStateText.Text = control?.IsRunning == true ? "后台设备接口已就绪，退出界面后仍可连接。请只将绑定码交给你信任的手机或平板。" : control?.LastError ?? "设备绑定接口尚未启动。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ControlApiStateText.Text = "设备详情刷新失败：" + ex.Message; }
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
        ClearLoginUrl();
        if (!string.IsNullOrWhiteSpace(_network?.BrowseUrl)) SetLoginUrl(_network.BrowseUrl);
    }

    private async Task RunAction(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        Pages.IsEnabled = false;
        FeedbackText.Text = "正在处理，请稍候…";
        try { await action(); }
        catch (Exception ex) { FeedbackText.Text = "操作未完成：" + ex.Message; }
        finally { _busy = false; Pages.IsEnabled = true; await RefreshStatusAsync(true); }
    }

    private async Task CommandAsync(RuntimeRequest request)
    {
        var result = await _runtime.SendAsync(request, _closing.Token);
        FeedbackText.Text = result.Message;
        if (!result.Success) FeedbackText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DangerText");
        else FeedbackText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "FeedbackTextBrush");
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshStatusAsync();
    private async void StartEngine_Click(object sender, RoutedEventArgs e)
    {
        var profile = (string)((Button)sender).Tag;
        if (profile == "tablet") { TabletTab.IsSelected = true; await RefreshStatusAsync(); return; }
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
    private async void StopEngine_Click(object sender, RoutedEventArgs e)
    {
        var profile = (string)((Button)sender).Tag;
        var engine = profile == "remote" ? _status?.Remote : _status?.Tablet;
        if (engine?.Streaming == true && MessageBox.Show("这会中断正在进行的串流，确认停止？", "停止串流", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAction(async () =>
        {
            if (profile == "tablet" && _tabletRequest is { } request && TabletRequestRegistry.Active(request.State))
                await _runtime.SendAsync(new("tablet-request-cancel", RequestId: request.Id), _closing.Token);
            var result = await _runtime.SendAsync(new("stop", profile), _closing.Token);
            FeedbackText.Text = result.Message;
            if (!result.Success)
            {
                FeedbackText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DangerText");
                return;
            }
            FeedbackText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "FeedbackTextBrush");
            // The service owns shutdown and verifies the virtual display is detached.
        });
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        var pin = PairPin.Text.Trim();
        if (pin.Length != 4 || pin.Any(c => c < '0' || c > '9')) { FeedbackText.Text = "请输入接收端显示的四位数字。"; PairPin.Focus(); return; }
        var profile = (string)((ComboBoxItem)PairMode.SelectedItem).Tag;
        await RunAction(async () => {
            var result = await _runtime.SendAsync(new("pair", profile, pin, PairName.Text.Trim()), _closing.Token);
            FeedbackText.Text = result.Message;
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
        catch (Exception ex) { FeedbackText.Text = ex.Message; }
    }

    private async void NetworkStart_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("启用内置连接？当前异地会话会中断。", "启用内置连接", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAction(() => CommandAsync(new("network-takeover")));
    }
    private async void NetworkRestore_Click(object sender, RoutedEventArgs e) => await RunAction(() => CommandAsync(new("network-restore")));
    private async void NetworkLogin_Click(object sender, RoutedEventArgs e) => await RunAction(async () =>
    {
        var response = await _runtime.SendAsync(new("network-login"), _closing.Token);
        FeedbackText.Text = response.Message;
        if (!response.Success || string.IsNullOrWhiteSpace(response.PayloadJson)) return;
        var status = JsonSerializer.Deserialize<NetworkStatus>(response.PayloadJson, RuntimeProtocol.JsonOptions);
        if (status?.LoggedIn == true) { FeedbackText.Text = "当前账号已登录。"; return; }
        var url = status?.BrowseUrl;
        if (string.IsNullOrWhiteSpace(url)) { FeedbackText.Text = "授权地址尚未就绪，请稍后再点登录账号。"; return; }
        if (!SetLoginUrl(url)) { FeedbackText.Text = "授权地址与已保存的连接通道不一致。"; return; }
        OpenTarget(LoginUrlBox.Text);
        FeedbackText.Text = "已打开浏览器授权页。完成授权后，登录状态会自动更新。";
    });
    private bool SetLoginUrl(string url)
    {
        ClearLoginUrl();
        if (_transportConfig is null) return false;
        var safe = HubTransports.AuthorizationUrl(_transportConfig, url);
        if (safe.Length == 0) return false;
        LoginUrlBox.Text = safe;
        LoginUrlBox.Visibility = CopyLoginButton.Visibility = Visibility.Visible;
        return true;
    }
    private void CopyLogin_Click(object sender, RoutedEventArgs e) => CopyText(LoginUrlBox.Text, "授权地址已复制，可在浏览器完成授权。");
    private void CopyAddress_Click(object sender, RoutedEventArgs e) => CopyText(string.Join(Environment.NewLine, _addresses.Select(x => x + ":48989")), "远程主屏连接地址已复制。");
    private void CopyText(string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value)) { FeedbackText.Text = "暂无可复制内容"; return; }
        try { System.Windows.Clipboard.SetText(value); FeedbackText.Text = message; }
        catch (Exception ex) { FeedbackText.Text = "无法访问剪贴板：" + ex.Message; }
    }

    private async Task RefreshReceiversAsync()
    {
        var response = await _runtime.SendAsync(new("receiver-list"), _closing.Token);
        if (!response.Success || response.PayloadJson is null) { ReceiverEmptyText.Text = response.Message; return; }
        RenderReceivers(JsonSerializer.Deserialize<ReceiverSnapshot>(response.PayloadJson, RuntimeProtocol.JsonOptions));
    }

    private void RenderReceivers(ReceiverSnapshot? snapshot)
    {
        if (snapshot is null) return;
        var selected = ReceiverBox.SelectedValue as string;
        ReceiverBox.ItemsSource = snapshot.Receivers;
        ReceiverBox.SelectedValue = selected;
        if (ReceiverBox.SelectedIndex < 0 && snapshot.Receivers.Length > 0) ReceiverBox.SelectedIndex = 0;
        ReceiverEmptyText.Text = snapshot.Receivers.Length == 0 ? "请在平板开启“接收电脑副屏请求”" : "分辨率和刷新率将按接收设备自动匹配";
        _tabletRequest = snapshot.Request;
        var active = _tabletRequest is not null && TabletRequestRegistry.Active(_tabletRequest.State);
        SendTabletRequestButton.IsEnabled = snapshot.Receivers.Length > 0 && !active;
        CancelTabletRequestButton.IsEnabled = active;
        CancelTabletRequestButton.Content = _tabletRequest?.State == "connected" ? "断开连接" : "取消请求";
        TabletRequestStateText.Text = _tabletRequest is null ? "尚未发起连接" : _tabletRequest.Message;
        TabletRequestStateText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, _tabletRequest?.State is "failed" or "rejected" or "expired" ? "DangerText" : "AccentText");
    }

    private async void SendTabletRequest_Click(object sender, RoutedEventArgs e)
    {
        if (ReceiverBox.SelectedValue is not string clientId) return;
        var rotation = int.Parse((string)((ComboBoxItem)OrientationBox.SelectedItem).Tag);
        var link = (string)((ComboBoxItem)RequestLinkBox.SelectedItem).Tag;
        await RunAction(async () => {
            var response = await _runtime.SendAsync(new("tablet-request", Profile: link, Orientation: rotation, ClientId: clientId), _closing.Token);
            FeedbackText.Text = response.Message;
            if (response.Success && response.PayloadJson is not null)
                RenderReceivers(JsonSerializer.Deserialize<ReceiverSnapshot>(response.PayloadJson, RuntimeProtocol.JsonOptions));
        });
    }

    private async void CancelTabletRequest_Click(object sender, RoutedEventArgs e)
    {
        if (_tabletRequest is null) return;
        await RunAction(() => CommandAsync(new("tablet-request-cancel", RequestId: _tabletRequest.Id)));
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
            FeedbackText.Text = AutoStartCheck.IsChecked == true ? "下次登录 Windows 后将显示托盘入口。" : "已关闭托盘自启，后台服务仍随系统启动。";
        }
        catch (Exception ex) { AutoStartCheck.IsChecked = GetAutoStart(); FeedbackText.Text = "无法保存启动设置：" + ex.Message; }
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
            else FeedbackText.Text = DisplayController.DisableExtended();
            var network = await _runtime.SendAsync(new("network-restore"), _closing.Token);
            if (!network.Success) failures.Add("异地连接：" + network.Message);
            if (failures.Count > 0)
            {
                FeedbackText.Text = "部分停止操作未完成：" + string.Join("；", failures);
                FeedbackText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DangerText");
            }
            else
            {
                FeedbackText.Text = "已停止主屏、副屏和异地连接，副屏显示已停用。";
                FeedbackText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "FeedbackTextBrush");
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
        try { File.WriteAllText(dialog.FileName, DiagnosticsText.Text, new UTF8Encoding(true)); FeedbackText.Text = "诊断摘要已保存到所选位置。"; }
        catch (Exception ex) { FeedbackText.Text = "保存失败：" + ex.Message; }
    }
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenTarget(Path.Combine(DataRoot, "logs"));
    private void OpenLicenses_Click(object sender, RoutedEventArgs e) => OpenTarget(Path.Combine(InstallRoot, "许可与来源.txt"));
    private void OpenTarget(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { FeedbackText.Text = "无法打开：" + ex.Message; }
    }
    private sealed record LocalLink(string Name, bool Usb, string[] Addresses);
    private static LocalLink[] GetLocalLinks() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .Select(n => new LocalLink(n.Name, (n.Description + " " + n.Name).Contains("RNDIS", StringComparison.OrdinalIgnoreCase)
            || n.Description.Contains("USB", StringComparison.OrdinalIgnoreCase) || n.Description.Contains("Remote NDIS", StringComparison.OrdinalIgnoreCase),
            UsableIpv4.Filter(n.GetIPProperties().UnicastAddresses.Select(a => a.Address.ToString()))))
        .Where(n => n.Addresses.Length > 0).ToArray();
}
