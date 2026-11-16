using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using StreamHub.Host.Runtime;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace StreamHub.Host;

public partial class MainWindow
{
    private ReceiverSnapshot? _receiverSnapshot;
    private LocalBindingSnapshot? _bindingSnapshot;
    private string? _aliasDeviceId;
    private readonly DispatcherTimer _bindingCountdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _bindingCountdownStarted;
    private readonly ConnectionReadiness _readiness = new();

    private static string DeviceDisplayName(ControlDeviceSummary d) => string.IsNullOrWhiteSpace(d.Alias) ? d.Name : d.Alias;
    private static string DeviceTypeText(ControlDeviceSummary d) => d.DeviceType switch { "phone" => "手机", "tablet" => "平板", _ => "安卓设备" };
    private static string LastContact(ControlDeviceSummary d) => d.LastSeen is { } seen
        ? "最后联系 " + seen.ToLocalTime().ToString("MM-dd HH:mm") : "尚无联系记录";
    private static string ScopeText(ControlDeviceSummary d) => string.Join("、", new[] {
        (d.Scope & ControlScope.Remote) != 0 ? "远程控制" : null,
        (d.Scope & ControlScope.Tablet) != 0 ? "副屏" : null,
        (d.Scope & ControlScope.Files) != 0 ? "文件" : null,
        (d.Scope & ControlScope.Clipboard) != 0 ? "剪贴板" : null }.Where(x => x is not null));

    private bool DeviceIsOnline(ControlDeviceSummary device)
    {
        if (device.Revoked) return false;
        bool live = _status?.Remote.Streaming == true || _status?.Tablet.Streaming == true;
        if (live && _status?.Controller is { IndividuallyAuthenticated: true } controller && controller.CredentialId == device.Id) return true;
        // Last authenticated contact alone is not evidence of an online receiver.
        return _receiverSnapshot?.Receivers.Any(r => r.ClientId == device.ClientId && ReceiverIsFresh(r)) == true;
    }
    private static bool ReceiverIsFresh(TabletReceiver receiver)
    {
        var elapsed = DateTimeOffset.UtcNow - receiver.LastSeen;
        return elapsed >= TimeSpan.Zero && elapsed <= TimeSpan.FromSeconds(30);
    }

    private void RefreshUnifiedStatus()
    {
        if (AcceptanceButton is null) return;
        bool live = _status?.Remote.Streaming == true || _status?.Tablet.Streaming == true;
        string state = _readiness.Observe(_status);
        ReadinessReasonButton.Visibility = _readiness.TimedOut ? Visibility.Visible : Visibility.Collapsed;
        AcceptanceLabel.Text = _readiness.TimedOut ? "远控未就绪" : state;
        AcceptanceDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            live ? "WarningText" : _status?.Remote.Healthy == true && !_status.ConnectionsPaused ? "SuccessText" : "Muted");
        HomeSummaryText.Text = (_status?.DeviceName ?? Environment.MachineName) + " · " + state + " · " +
            (live ? (_status!.Controller?.Name ?? "已连接设备") + " 正在连接" : "当前无设备连接");
        if (_status is null) HomeSummaryText.Text = "后台服务未连接，暂时无法确认被控状态";
        HomePrivacyText.Visibility = _status?.PrivacyScreenActive == true ? Visibility.Visible : Visibility.Collapsed;
        HomePrivacyText.Text = "隐私屏正在生效 · Ctrl+Alt+F12 可恢复本机画面";
        DeviceStateLabel.Text = _status is null ? "状态未知" : state;
        PauseConnectionsButton.Content = _status?.ConnectionsPaused == true ? "恢复接受连接"
            : live ? "断开并暂停连接" : "暂停接受连接";
        PauseConnectionsButton.IsEnabled = _status is not null && !_busy;
        DisconnectActiveButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        bool networkReady = _status?.Network.Running == true && _network?.LoggedIn == true && _network.State == "Running";
        bool networkFailed = _status?.Network.Desired == true && !_status.Network.Running;
        string networkState = _status is null ? "状态未知" : networkFailed ? "异常" : networkReady ? "已连接"
            : _status.Network.Running ? "等待登录或连接" : "未启用";
        AccountConnectionText.Text = "局域网：" + (_status is null ? "未知" : _addresses.Length > 0 ? "本机地址可用" : "暂无可用地址")
            + "\n内置异地连接：" + networkState;
        AccountConnectionDetailText.Text = _status is null ? "请确认后台服务正在运行。"
            : networkFailed ? _status.Network.Message
            : !_status.Network.Running ? ""
            : !string.IsNullOrWhiteSpace(_network?.Error) ? _network.Error
            : _network?.Health.Length > 0 ? string.Join("；", _network.Health)
            : "";
        AccountConnectionDetailText.Visibility = string.IsNullOrWhiteSpace(AccountConnectionDetailText.Text)
            ? Visibility.Collapsed : Visibility.Visible;
        AccountPrimaryButton.Content = networkReady ? "查看连接详情" : _status?.Network.Running == true ? "登录连接账号"
            : networkFailed ? "重试异地连接" : "启用异地连接";
        AccountPrimaryButton.IsEnabled = _status is not null && !_busy;
        AccountButton.ToolTip = "网络连接 · 异地：" + networkState;
    }

    private sealed record ReceiverAction(bool CanConnect, string Reason);
    private ReceiverAction GetReceiverAction(string clientId, bool allowed, bool duringAction = false)
    {
        if (_status is null) return new(false, "后台服务未连接，请稍后刷新");
        if (_status.ConnectionsPaused) return new(false, "电脑已暂停接受连接，请先在顶栏恢复");
        if (!allowed) return new(false, "未授权副屏，请在此设备的“权限”中调整");
        if (_tabletRequest is { } request && TabletRequestRegistry.Active(request.State))
            return new(false, "已有副屏邀请或连接，请先取消或断开");
        if (_receiverSnapshot is null) return new(false, "接收状态暂不可用，请刷新后重试");
        if (!_receiverSnapshot.Receivers.Any(r => r.ClientId == clientId && ReceiverIsFresh(r)))
            return new(false, "未收到响应：无法区分离线、网络不通或接收开关关闭");
        if (_busy && !duringAction) return new(false, "正在处理其他操作");
        return new(true, "向此设备发送副屏邀请");
    }

    private void RenderUnifiedDevices()
    {
        if (BoundDevicesList is null) return;
        var devices = _controlDeviceList?.Devices ?? [];
        var rows = devices.OrderBy(d => d.Revoked).ThenByDescending(d => d.LastSeen ?? d.CreatedAt).Select(d => {
            bool online = DeviceIsOnline(d);
            var receiver = GetReceiverAction(d.ClientId, !d.Revoked && (d.Scope & ControlScope.Tablet) != 0);
            return new {
                d.Id, d.ClientId, Name = DeviceDisplayName(d), TypeText = DeviceTypeText(d), d.Revoked,
                LastContact = LastContact(d), ShortId = d.Id.Length > 6 ? d.Id[^6..] : d.Id,
                Online = online, OnlineText = d.Revoked ? "已撤销" : online ? "在线" : "未确认在线",
                ActionVisibility = d.Revoked ? Visibility.Collapsed : Visibility.Visible,
                HistoryActionVisibility = d.Revoked ? Visibility.Visible : Visibility.Collapsed,
                CanConnect = receiver.CanConnect, ConnectReason = receiver.Reason,
                CanOpenFiles = !d.Revoked && _status is not null && !_busy,
                FileReason = "前往此设备的文件页",
                Icon = d.DeviceType == "tablet" ? "\uE70A" : "\uE8EA"
            };
        }).ToArray();
        BoundDevicesList.ItemsSource = rows.Where(d => !d.Revoked).ToArray();
        HistoricalDevicesList.ItemsSource = rows.Where(d => d.Revoked).ToArray();
        BoundDevicesEmptyText.Text = _controlDeviceList is null ? "设备列表暂不可用，请稍后刷新" : "还没有有效绑定，点右侧“+ 绑定设备”添加";
        BoundDevicesEmptyText.Visibility = rows.Any(d => !d.Revoked) ? Visibility.Collapsed : Visibility.Visible;
        DeviceHistoryExpander.Header = $"历史设备（{rows.Count(d => d.Revoked)}）";
        DeviceHistoryExpander.Visibility = rows.Any(d => d.Revoked) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderReceiverChoices()
    {
        if (TabletReceiverList is null) return;
        var receivers = _receiverSnapshot?.Receivers.Where(ReceiverIsFresh).ToArray() ?? [];
        var devices = _controlDeviceList?.Devices.Where(d => !d.Revoked).ToArray() ?? [];
        bool active = _tabletRequest is { } request && TabletRequestRegistry.Active(request.State);
        var rows = devices.Select(d => {
            var action = GetReceiverAction(d.ClientId, (d.Scope & ControlScope.Tablet) != 0);
            return new { d.ClientId, Name = DeviceDisplayName(d), CanConnect = action.CanConnect, Detail = action.Reason };
        }).ToList();
        if (_controlDeviceList?.LegacyAllowed == true)
            rows.AddRange(receivers.Where(r => !devices.Any(d => d.ClientId == r.ClientId)).Select(r => {
                var action = GetReceiverAction(r.ClientId, true);
                return new { r.ClientId, r.Name, CanConnect = action.CanConnect, Detail = action.Reason };
            }));
        TabletReceiverList.ItemsSource = rows;
        ReceiverEmptyText.Text = rows.Count > 0 ? "" : _receiverSnapshot is null ? "接收状态暂不可用，请刷新后重试。"
            : "还没有可用设备";
        ReceiverEmptyText.Visibility = string.IsNullOrWhiteSpace(ReceiverEmptyText.Text) ? Visibility.Collapsed : Visibility.Visible;
        CancelTabletRequestButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        CancelTabletRequestButton.Content = _tabletRequest?.State == "connected" ? "断开副屏" : "取消邀请";
        CancelTabletRequestButton.IsEnabled = active && !_busy;
        RenderFilesInlineState();
    }

    private void OpenAcceptance_Click(object sender, RoutedEventArgs e)
    { bool open = !AcceptancePopover.IsOpen; CloseTransientPopovers(); AcceptancePopover.IsOpen = open; }
    private void OpenAccountPopover_Click(object sender, RoutedEventArgs e)
    { bool open = !AccountPopover.IsOpen; CloseTransientPopovers(); RefreshUnifiedStatus(); AccountPopover.IsOpen = open; }
    private async void PauseConnections_Click(object sender, RoutedEventArgs e)
    {
        if (_status is null || _busy) return;
        CloseTransientPopovers();
        await RunAction(async () =>
        {
            // A session may have started since the last periodic refresh.
            var current = await _runtime.GetStatusAsync(_closing.Token);
            if (!current.Success || current.Status is null)
            {
                ShowFeedback("无法确认当前连接状态，请稍后重试。", error: true);
                return;
            }
            bool paused = current.Status.ConnectionsPaused;
            bool live = current.Status.Remote.Streaming || current.Status.Tablet.Streaming;
            if (!paused && live && System.Windows.MessageBox.Show(this,
                "将断开当前主屏和副屏连接，并暂停接受连接。\n重启后仍保持暂停，需在电脑顶栏手动恢复。是否继续？",
                "断开并暂停连接", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await CommandAsync(new(paused ? "connections-resume" : "connections-pause"));
        });
    }
    private async void DisconnectActive_Click(object sender, RoutedEventArgs e)
    { CloseTransientPopovers(); await RunAction(() => CommandAsync(new("session-disconnect"))); }
    private void AccountPrimary_Click(object sender, RoutedEventArgs e)
    {
        CloseTransientPopovers();
        if (_status?.Network.Running != true) NetworkStart_Click(sender, e);
        else if (_network?.LoggedIn != true) { Pages.SelectedItem = AccountPage; NetworkLogin_Click(sender, e); }
        else OpenAccount_Click(sender, e);
    }
    private void OpenTabletSettings_Click(object sender, RoutedEventArgs e)
    { CloseTransientPopovers(); Pages.SelectedItem = SettingsPage; SettingsCategories.SelectedItem = TabletSettingsCategory; }
    private async void RefreshReceiverList_Click(object sender, RoutedEventArgs e)
    { await ReadControlDevicesAsync(); await RefreshReceiversAsync(); }
    private async void ConnectReceiver_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string clientId }) await InviteReceiverAsync(clientId, null);
    }
    private async void ConnectListedReceiver_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var device = _controlDeviceList?.Devices.FirstOrDefault(d => d.Id == id && !d.Revoked);
        if (device is not null) await InviteReceiverAsync(device.ClientId, id);
    }
    private Task InviteReceiverAsync(string clientId, string? credentialId) => RunAction(async () =>
    {
        // Recheck this exact credential and receiver, never reuse a different dropdown selection.
        var current = await _runtime.GetStatusAsync(_closing.Token);
        _status = current.Success ? current.Status : null;
        await ReadControlDevicesAsync();
        await RefreshReceiversAsync();
        var device = _controlDeviceList?.Devices.FirstOrDefault(d =>
            d.ClientId == clientId && !d.Revoked && (credentialId is null || d.Id == credentialId));
        bool allowed = device is not null ? (device.Scope & ControlScope.Tablet) != 0
            : credentialId is null && _controlDeviceList?.LegacyAllowed == true;
        var action = GetReceiverAction(clientId, allowed, duringAction: true);
        if (!action.CanConnect) { ShowFeedback(action.Reason, error: true); return; }
        var rotation = int.Parse((string)((ComboBoxItem)OrientationBox.SelectedItem).Tag);
        var link = (string)((ComboBoxItem)RequestLinkBox.SelectedItem).Tag;
        var response = await _runtime.SendAsync(new("tablet-request", Profile: link, Orientation: rotation, ClientId: clientId), _closing.Token);
        ShowFeedback(response.Message, error: !response.Success);
        if (response.Success && response.PayloadJson is not null)
            RenderReceivers(JsonSerializer.Deserialize<ReceiverSnapshot>(response.PayloadJson, RuntimeProtocol.JsonOptions));
    });
    private async void OpenListedFiles_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        await RunAction(async () =>
        {
            await ReadControlDevicesAsync();
            var device = _controlDeviceList?.Devices.FirstOrDefault(d => d.Id == id && !d.Revoked);
            if (device is null) { ShowFeedback("设备绑定已失效，请刷新列表。", error: true); return; }
            _fileTargetClientId = device.ClientId;
            await RefreshFilesAsync();
            FileDeviceBox.SelectedValue = device.ClientId;
            CloseTransientPopovers(); Pages.SelectedItem = FilesPage;
            if (FileDeviceBox.SelectedValue as string != device.ClientId)
                ShowFeedback("此设备的文件状态暂不可用，请稍后刷新。", error: true);
        });
    }

    private async Task OpenBindingAsync()
    {
        CloseTransientPopovers();
        BindingDialog.Visibility = Visibility.Visible;
        if (!_bindingCountdownStarted)
        {
            _bindingCountdownStarted = true;
            _bindingCountdown.Tick += (_, _) => RenderBindingCountdown();
            Closed += (_, _) => _bindingCountdown.Stop();
        }
        _bindingCountdown.Start();
        await RefreshLocalBindingAsync();
        if (_bindingSnapshot?.Code is null) await BindingActionAsync("binding-start");
        BindingDialog.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
    private void RenderBindingCountdown()
    {
        if (BindingCountdownText is null) return;
        var expiry = _bindingSnapshot?.Request is { State: "pending" or "approved" } pending
            ? pending.ExpiresAt : _bindingSnapshot?.ExpiresAt;
        int remaining = expiry is null ? 0 : Math.Max(0, (int)Math.Ceiling((expiry.Value - DateTimeOffset.UtcNow).TotalSeconds));
        BindingCountdownText.Text = remaining > 0 ? $"有效期剩余 {remaining / 60:00}:{remaining % 60:00} · 一次性六位短码" : "短码未生成或已过期，请重新生成";
    }
    private async void CloseBinding_Click(object sender, RoutedEventArgs e) => await CloseBindingAsync();
    private async Task CloseBindingAsync()
    {
        try
        {
            var response = await _runtime.SendAsync(new("binding-cancel"), _closing.Token);
            if (!response.Success) { ShowFeedback(response.Message, error: true); return; }
            BindingDialog.Visibility = Visibility.Collapsed;
            _bindingCountdown.Stop();
            _bindingSnapshot = null; _bindingRequest = null;
            await ReadControlDevicesAsync();
            HomeNavigation.Focus();
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex) { ShowFeedback("关闭绑定失败：" + ex.Message, error: true); }
    }
    private async void BindingDialog_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { e.Handled = true; await CloseBindingAsync(); } }
    private void OpenDeviceAlias_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        var device = _controlDeviceList?.Devices.FirstOrDefault(d => d.Id == id);
        if (device is null) return;
        _aliasDeviceId = id; AliasDeviceText.Text = device.Name + " · ID " + id[^6..];
        DeviceAliasBox.Text = device.Alias ?? ""; AliasDialog.Visibility = Visibility.Visible;
        DeviceAliasBox.Focus(); DeviceAliasBox.SelectAll();
    }
    private async void SaveDeviceAlias_Click(object sender, RoutedEventArgs e)
    {
        if (_aliasDeviceId is null) return;
        await RunAction(async () => {
            var result = await _runtime.SendAsync(new("control-device-rename", RequestId: _aliasDeviceId, Name: DeviceAliasBox.Text), _closing.Token);
            ShowFeedback(result.Message, error: !result.Success);
            if (result.Success) { AliasDialog.Visibility = Visibility.Collapsed; await ReadControlDevicesAsync(); }
        });
    }
    private void CloseDeviceAlias_Click(object sender, RoutedEventArgs e) => AliasDialog.Visibility = Visibility.Collapsed;
    private void AliasDialog_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { e.Handled = true; AliasDialog.Visibility = Visibility.Collapsed; } }

    private void GrantListedFileDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string clientId } || _fileSnapshot?.Devices.Any(d => d.ClientId == clientId) != true) return;
        FileDeviceBox.SelectedValue = clientId;
        GrantFiles_Click(new Button { Tag = "allow" }, e);
    }

    private void RenderFilesInlineState()
    {
        if (FileInlineStateText is null) return;
        var selected = _fileSnapshot?.Devices.FirstOrDefault(d => d.ClientId == FileDeviceBox.SelectedValue as string);
        bool enabled = _fileSnapshot?.Settings.Enabled == true;
        SendFileButton.IsEnabled = enabled && selected?.Allowed == true;
        string reason = _fileSnapshot is null ? "文件状态暂不可用" : !enabled ? "文件传输未开启，请在设备权限中开启"
            : selected is null ? "请选择传输设备" : selected.Allowed ? "" : selected.UsesDevicePermissions ? "此设备的文件权限未开启" : "所选设备待允许，请核对文件授权";
        FileInlineStateText.Text = reason;
        FileInlineStateText.Visibility = reason.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SendFileButton.ToolTip = reason.Length > 0 ? reason : "选择电脑文件并发送";
        FileInlineEnableButton.Visibility = !enabled ? Visibility.Visible : Visibility.Collapsed;
        FileInlineAllowButton.Visibility = enabled && selected is { Allowed: false } ? Visibility.Visible : Visibility.Collapsed;
        FileInlineAllowButton.Content = selected?.UsesDevicePermissions == true ? "设备文件权限" : "核对文件授权";
        bool tasks = _fileSnapshot?.Items.Length > 0;
        FileTasksEmptyText.Text = "暂无传输任务";
        FileTasksEmptyText.Visibility = tasks ? Visibility.Collapsed : Visibility.Visible;
        FileTasksEmptyState.Visibility = tasks ? Visibility.Collapsed : Visibility.Visible;
        FileTaskList.Visibility = FileTaskActions.Visibility = tasks ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenDeviceMenu_Click(object sender, RoutedEventArgs e)
    { if (sender is Button button) OpenMenu(button); }
    private async void DeleteDeviceHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        await RunAction(async () =>
        {
            await ReadControlDevicesAsync();
            var device = _controlDeviceList?.Devices.FirstOrDefault(d => d.Id == id);
            if (device is not { Revoked: true }) return;
            if (System.Windows.MessageBox.Show(this, $"删除“{DeviceDisplayName(device)}”的历史记录？\n该设备仍保持撤销。",
                "删除历史设备", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await CommandAsync(new("control-device-delete-history", RequestId: id));
            await ReadControlDevicesAsync();
        });
    }
    private void OpenBoundDeviceDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } element) return;
        var anchor = element is MenuItem item && ItemsControl.ItemsControlFromItemContainer(item) is ContextMenu menu
            ? menu.PlacementTarget : element;
        if (anchor is null) return;
        var device = _controlDeviceList?.Devices.FirstOrDefault(d => d.Id == id);
        if (device is null) return;
        bool open = !BoundDeviceDetailsPopover.IsOpen || BoundDeviceDetailsPopover.PlacementTarget != anchor;
        CloseTransientPopovers();
        BoundDeviceNameText.Text = DeviceDisplayName(device);
        BoundDeviceDetailText.Text = DeviceTypeText(device) + " · " + (device.Revoked ? "已撤销" : "已授权")
            + "\n" + LastContact(device) + "\nID " + device.Id
            + (string.IsNullOrEmpty(device.Alias) ? "" : "\n原名称 " + device.Name);
        BoundDeviceDetailsPopover.PlacementTarget = anchor;
        BoundDeviceDetailsPopover.IsOpen = open;
    }
}
