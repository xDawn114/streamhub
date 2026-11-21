using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private static string DeviceTypeText(ControlDeviceSummary d) => d.DeviceType switch { "phone" => "手机", "tablet" => "平板", _ => "设备" };
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
        bool ending = _status?.Remote.BackendReleasePending == true;
        string state = _readiness.Observe(_status);
        bool assistance = _status?.Assistance?.State == "active";
        if (assistance) { live = true; state = _status!.Assistance!.Mode == "view" ? "临时协助 · 仅观看" : "临时协助 · 允许控制"; }
        bool assistanceReserved = _status?.Assistance?.State is "pending" or "starting" or "active" or "cleanup-pending";
        if (assistanceReserved && !assistance) state = _status!.Assistance!.State switch {
            "offered" => "临时协助 · 等待请求", "pending" => "临时协助 · 待确认",
            "starting" => "临时协助 · 准备画面", _ => "临时协助 · 正在清理" };
        ReadinessReasonButton.Visibility = _readiness.TimedOut && !assistanceReserved ? Visibility.Visible : Visibility.Collapsed;
        AcceptanceLabel.Text = _readiness.TimedOut && !assistanceReserved ? "远控未就绪" : state;
        AcceptanceDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            live || ending ? "WarningText" : _status?.Remote.Healthy == true && !_status.ConnectionsPaused ? "SuccessText" : "Muted");
        HomeDeviceNameText.Text = _status?.DeviceName ?? Environment.MachineName;
        HomeSummaryText.Text = state + " · " +
            (live ? (assistance ? _status!.Assistance!.Name : _status!.Controller?.Name ?? "已连接设备") + " 正在连接" : "当前无设备连接");
        if (_status is null) HomeSummaryText.Text = "后台服务未连接，暂时无法确认被控状态";
        HomePrivacyText.Visibility = _status?.PrivacyScreenActive == true ? Visibility.Visible : Visibility.Collapsed;
        HomePrivacyText.Text = "隐私屏正在生效 · Ctrl+Alt+F12 可恢复本机画面";
        DeviceStateLabel.Text = _status is null ? "状态未知" : state;
        PauseConnectionsButton.Content = _status?.ConnectionsPaused == true ? "恢复接受连接"
            : live || ending ? "断开并暂停连接" : "暂停接受连接";
        PauseConnectionsButton.IsEnabled = _status is not null && !_busy;
        DisconnectActiveButton.Visibility = live || ending ? Visibility.Visible : Visibility.Collapsed;
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
        AccountPrimaryButton.IsEnabled = !_busy;
    }

    private sealed record ReceiverAction(bool CanConnect, string Reason);
    private ReceiverAction GetReceiverAction(string clientId, bool allowed, bool duringAction = false)
    {
        if (_status is null) return new(false, "后台服务未连接，请稍后刷新");
        if (_status.ConnectionsPaused) return new(false, "电脑已暂停接受连接，请先在顶栏恢复");
        if (_status.Assistance?.State is "pending" or "starting" or "active" or "cleanup-pending")
            return new(false, "请先结束临时协助，再连接信任设备");
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
        var rows = devices.OrderBy(d => d.Revoked).ThenByDescending(d => d.LastSeen ?? d.CreatedAt)
            .Select(d => MakeDeviceCard(d.Id, d.ClientId, DeviceDisplayName(d), DeviceTypeText(d),
                d.DeviceType == "tablet" ? "\uE70A" : "\uE8EA", d, DeviceIsOnline(d), LastContact(d))).ToList();
        // Compatible receivers used to live only in the title bar. Never reintroduce
        // a revoked credential as a compatible device or list one receiver twice.
        if (_controlDeviceList?.LegacyAllowed == true)
            rows.AddRange((_receiverSnapshot?.Receivers ?? []).Where(ReceiverIsFresh)
                .Where(r => !devices.Any(d => d.ClientId == r.ClientId))
                .GroupBy(r => r.ClientId).Select(group => group.OrderByDescending(r => r.LastSeen).First())
                .Select(r => MakeDeviceCard("", r.ClientId, r.Name, "兼容设备", "\uE772", null, true,
                    "兼容连接 · 建议使用短码重新绑定")));
        if (_tabletRequest is {} request && TabletRequestRegistry.Active(request.State) &&
            !rows.Any(row => !row.Revoked && row.ClientId == request.ClientId))
            rows.Add(MakeDeviceCard("", request.ClientId, "当前副屏设备", "设备", "\uE772", null, false, "接收状态待确认"));
        SetDeviceItems(BoundDevicesList, rows.Where(d => !d.Revoked).ToArray());
        SetDeviceItems(HistoricalDevicesList, rows.Where(d => d.Revoked).ToArray());
        BoundDevicesEmptyText.Text = _controlDeviceList is null ? "设备列表暂不可用，请稍后刷新" : "还没有设备，点右侧“+ 绑定设备”添加";
        BoundDevicesEmptyText.Visibility = rows.Any(d => !d.Revoked) ? Visibility.Collapsed : Visibility.Visible;
        DeviceHistoryExpander.Header = $"历史设备（{rows.Count(d => d.Revoked)}）";
        DeviceHistoryExpander.Visibility = rows.Any(d => d.Revoked) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void SetDeviceItems(ItemsControl list, DeviceCard[] rows)
    {
        if (list.ItemsSource is DeviceCard[] current && current.SequenceEqual(rows)) return;

        var focusedButton = Keyboard.FocusedElement as Button;
        var focusedCard = focusedButton is not null && IsDescendantOf(focusedButton, list)
            ? focusedButton.DataContext as DeviceCard : null;
        string action = focusedCard is null ? "" : AutomationProperties.GetName(focusedButton!);
        if (string.IsNullOrWhiteSpace(action) && focusedCard is not null)
            action = focusedButton!.Content?.ToString() ?? "";

        list.ItemsSource = rows;
        if (focusedCard is null || string.IsNullOrWhiteSpace(action)) return;

        list.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!list.IsLoaded) return;
            list.UpdateLayout();
            var buttons = VisualDescendants(list).OfType<Button>()
                .Where(button => button.DataContext is DeviceCard card &&
                    card.Id == focusedCard.Id && card.ClientId == focusedCard.ClientId).ToArray();
            var target = buttons.FirstOrDefault(button => AutomationProperties.GetName(button) == action && button.IsEnabled)
                ?? buttons.FirstOrDefault(button => button.IsEnabled);
            target?.Focus();
        }));
    }

    private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in VisualDescendants(child)) yield return nested;
        }
    }

    private DeviceCard MakeDeviceCard(string id, string clientId, string name, string type, string icon,
        ControlDeviceSummary? device, bool online, string lastContact)
    {
        bool revoked = device?.Revoked == true;
        bool compatible = device is null && _controlDeviceList?.LegacyAllowed == true &&
            !_controlDeviceList.Devices.Any(d => d.ClientId == clientId);
        var request = !revoked && _tabletRequest?.ClientId == clientId ? _tabletRequest : null;
        bool active = request is not null && TabletRequestRegistry.Active(request.State);
        var action = GetReceiverAction(clientId, !revoked && (device is not null
            ? (device.Scope & ControlScope.Tablet) != 0 : compatible));
        bool files = !revoked && _status is not null && !_busy && (device is not null ||
            compatible && _fileSnapshot?.Devices.Any(d => d.ClientId == clientId) == true);
        return new(id, clientId, name, type, icon, device is not null, revoked, online,
            revoked ? "已撤销" : active ? request!.State == "connected" ? "副屏已连接" : "等待设备确认" : online ? "在线" : "未确认在线",
            lastContact, active ? !_busy && _status is not null : action.CanConnect,
            active ? request!.State == "connected" ? "结束此设备的副屏连接" : "取消此设备的副屏邀请" : action.Reason,
            files, device is not null ? "前往此设备的文件页" : "需要此兼容设备先连接文件服务，或使用短码重新绑定",
            active ? request!.Id : null, request?.State ?? "", request?.Message ?? "");
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
            bool live = current.Status.Remote.Streaming || current.Status.Tablet.Streaming || current.Status.Remote.BackendReleasePending;
            if (!paused && live && System.Windows.MessageBox.Show(this,
                "将断开当前主屏和副屏连接，并暂停接受连接。\n重启后仍保持暂停，需在电脑顶栏手动恢复。是否继续？",
                "断开并暂停连接", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await CommandAsync(new(paused ? "connections-resume" : "connections-pause"));
        });
    }
    private async void DisconnectActive_Click(object sender, RoutedEventArgs e)
    { CloseTransientPopovers(); await RunAction(() => CommandAsync(new("session-disconnect"))); }
    private void OpenTabletSettings_Click(object sender, RoutedEventArgs e)
    { CloseTransientPopovers(); Pages.SelectedItem = SettingsPage; SettingsCategories.SelectedItem = TabletSettingsCategory; }
    private bool _refreshingDeviceCards;
    private async void RefreshDeviceCards_Click(object sender, RoutedEventArgs e) => await RefreshDeviceCardsAsync();
    private async Task RefreshDeviceCardsAsync()
    {
        if (_refreshingDeviceCards) return;
        _refreshingDeviceCards = true; RefreshDeviceCardsButton.IsEnabled = false;
        try { await ReadControlDevicesAsync(); await RefreshReceiversAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowFeedback("设备刷新失败：" + error.Message, error: true); }
        finally { _refreshingDeviceCards = false; RefreshDeviceCardsButton.IsEnabled = true; }
    }
    private async void ConnectListedReceiver_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DeviceCard card }) return;
        if (card.RequestId is {} requestId)
        {
            await RunAction(async () =>
            {
                await RefreshReceiversAsync();
                if (_receiverSnapshot is null || _tabletRequest is not {} current || current.Id != requestId ||
                    current.ClientId != card.ClientId || !TabletRequestRegistry.Active(current.State))
                { ShowFeedback("这次副屏连接已变化，请查看更新后的设备卡片。", error: true); return; }
                await CommandAsync(new("tablet-request-cancel", RequestId: requestId));
                await RefreshReceiversAsync();
            });
        }
        else await InviteReceiverAsync(card.ClientId, card.CredentialId);
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
            : credentialId is null && _controlDeviceList?.LegacyAllowed == true &&
                !_controlDeviceList.Devices.Any(d => d.ClientId == clientId);
        var action = GetReceiverAction(clientId, allowed, duringAction: true);
        if (!action.CanConnect) { ShowFeedback(action.Reason, error: true); return; }
        var orientation = (string)((ComboBoxItem)OrientationBox.SelectedItem).Tag;
        bool automatic = orientation == "auto";
        var rotation = automatic ? 0 : int.Parse(orientation);
        var link = (string)((ComboBoxItem)RequestLinkBox.SelectedItem).Tag;
        var response = await _runtime.SendAsync(new("tablet-request", Profile: link, Orientation: rotation,
            AutoOrientation: automatic, ClientId: clientId), _closing.Token);
        ShowFeedback(response.Message, error: !response.Success);
        if (response.Success && response.PayloadJson is not null)
            RenderReceivers(JsonSerializer.Deserialize<ReceiverSnapshot>(response.PayloadJson, RuntimeProtocol.JsonOptions));
    });
    private async void OpenListedFiles_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DeviceCard card }) return;
        await RunAction(async () =>
        {
            await ReadControlDevicesAsync();
            var device = _controlDeviceList?.Devices.FirstOrDefault(d => d.Id == card.CredentialId &&
                d.ClientId == card.ClientId && !d.Revoked);
            bool compatible = card.CredentialId is null && _controlDeviceList?.LegacyAllowed == true &&
                !_controlDeviceList.Devices.Any(d => d.ClientId == card.ClientId);
            if (device is null && !compatible) { ShowFeedback("设备绑定已失效，请刷新列表。", error: true); return; }
            _fileTargetClientId = card.ClientId;
            await RefreshFilesAsync();
            FileDeviceBox.SelectedValue = card.ClientId;
            CloseTransientPopovers(); Pages.SelectedItem = FilesPage;
            if (FileDeviceBox.SelectedValue as string != card.ClientId)
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
        BindingCountdownText.Text = remaining > 0 ? $"有效期剩余 {remaining / 60:00}:{remaining % 60:00}" : "短码已过期，请重新生成";
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
        FileInlineEnableButton.Visibility = !enabled ? Visibility.Visible : Visibility.Collapsed;
        FileInlineAllowButton.Visibility = enabled && selected is { Allowed: false } ? Visibility.Visible : Visibility.Collapsed;
        FileInlineAllowButton.Content = "信任此设备";
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
