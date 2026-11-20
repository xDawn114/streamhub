using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using StreamHub.Host.Runtime;
using Button = System.Windows.Controls.Button;

namespace StreamHub.Host;

public partial class MainWindow
{
    private AssistanceClient? _assistanceClient;
    private CancellationTokenSource? _assistancePeerLifetime;
    private string? _shownAssistanceRequest;
    private string? _assistancePermissionSession;
    private string? _displayedAssistanceCode, _importedAssistanceCode;
    private AssistanceConnection? _assistanceInvitation;
    private bool _assistanceCodeVisible = true;
    private bool _assistanceLoginRegistered;
    private bool _assistanceLoginPending;
    private readonly string _assistanceLoginId = Guid.NewGuid().ToString("D");
    private AssistancePeerStore? _assistancePeerStore;
    private AssistancePeerStore AssistancePeers => _assistancePeerStore ??= new(
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(UiPreferencesPath)!, "assistance-peers.json"));
    private sealed record AssistanceDeviceRow(string Id, string Name, string Detail);
    private void RenderAssistance()
    {
        if (AssistanceHostState is null) return;
        var state = _status?.Assistance;
        RenderAssistanceNetwork();
        AssistanceHostState.Text = state?.Message ?? "后台服务未连接";
        if (_displayedAssistanceCode != state?.Code)
        {
            _displayedAssistanceCode = state?.Code;
            _assistanceCodeVisible = true;
        }
        RenderAssistanceCode();
        AssistanceAddressText.Text = _addresses.Length == 0 ? "暂无可用地址" : string.Join(" / ", _addresses);
        AssistanceDeviceName.Text = _status?.DeviceName ?? Environment.MachineName;
        AssistanceExpiryText.Text = state?.State == "offered" && state.ExpiresAt is { } expiry
            ? "有效至 " + expiry.ToLocalTime().ToString("HH:mm") : "";
        bool reserved = state?.State is "pending" or "starting" or "active" or "cleanup-pending";
        AssistanceEnabledToggle.IsChecked = state?.Enabled == true;
        AssistanceEnabledToggle.IsEnabled = state is not null && state.State != "unavailable" && !_busy;
        AssistanceCopyButton.IsEnabled = state?.State == "offered" && !_busy;
        AssistanceRefreshButton.IsEnabled = state?.Enabled == true && !reserved && !_busy;
        AssistanceEndButton.IsEnabled = reserved && !_busy;
        AssistanceEndButton.Visibility = reserved ? Visibility.Visible : Visibility.Collapsed;
        bool pending = state?.State == "pending";
        AssistancePendingPanel.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        AssistancePendingPanel.IsEnabled = !_busy;
        if (!pending || _assistancePermissionSession != state?.SessionId)
        {
            _assistancePermissionSession = pending ? state?.SessionId : null;
            AssistanceViewPermission.IsChecked = AssistanceControlPermission.IsChecked = false;
            AssistanceTrustDeviceCheck.IsChecked = false;
        }
        AssistanceTrustDeviceCheck.Visibility = state?.CanTrust == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateAssistancePermissionAction();
        AssistancePeerText.Text = state?.Name + " 请求临时协助";
        AssistanceComparisonText.Text = "核对号 " + state?.Comparison;
        if (pending && _shownAssistanceRequest != state?.SessionId)
        {
            _shownAssistanceRequest = state?.SessionId;
            ShowFeedback("收到临时协助请求");
        }
        UpdateAssistanceJoinButton();
        RenderAssistanceTrustedDevices();
    }
    private void RenderAssistanceNetwork()
    {
        var transport = _network?.Transport.Transport ?? _transportConfig?.Transport;
        string label = transport?.Label ?? "异地连接";
        AssistanceNetworkState.Text = _status is null ? "等待后台服务" :
            transport?.UsesAccount == false ? label :
            _status.Network.Running != true ? "局域网 · 异地连接未启用" :
            _network is null ? "正在读取连接网络" :
            !_network.Available ? "局域网 · 异地连接暂不可用" :
            _network.LoggedIn && _network.State == "Running" ? label + " · 已连接" :
            _network.State == "NeedsLogin" ? "局域网 · 异地连接未登录" : label + " · " + _network.StateText;
    }
    private async Task InitializeAssistanceAsync()
    {
        if (_assistanceLoginRegistered || _assistanceLoginPending || _status is null) return;
        _assistanceLoginPending = true;
        try
        {
            var response = await _runtime.SendAsync(new("assistance-login", RequestId: _assistanceLoginId), _closing.Token);
            if (!response.Success) { ShowFeedback(response.Message, error: true); return; }
            _assistanceLoginRegistered = true;
            var updated = response.Status is not null ? response : await _runtime.GetStatusAsync(_closing.Token);
            if (updated.Success && updated.Status is not null) _status = updated.Status;
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception error) { ShowFeedback("协助码未能刷新：" + error.Message, error: true); }
        finally { _assistanceLoginPending = false; }
    }
    private void RenderAssistanceTrustedDevices()
    {
        var hostDevices = (_status?.Assistance?.TrustedDevices ?? []).Select(device => new AssistanceDeviceRow(
            device.Id, device.Name, device.AllowControl ? "允许控制 · 免协助码" : "仅观看 · 免协助码")).ToArray();
        AssistanceHostTrustList.ItemsSource = hostDevices;
        AssistanceHostTrustGroup.Visibility = hostDevices.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        try
        {
            var peers = AssistancePeers.List().Select(peer => new AssistanceDeviceRow(peer.Id, peer.Name,
                peer.Mode == "control" ? "允许控制 · 免协助码" : "仅观看 · 免协助码")).ToArray();
            AssistancePeerList.ItemsSource = peers;
            AssistancePeerList.IsEnabled = !_busy && _assistanceClient is null;
            AssistancePeerListGroup.Visibility = peers.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            AssistanceTrustEmptyText.Text = "暂无信任设备";
            AssistanceTrustEmptyText.Visibility = peers.Length + hostDevices.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or
            System.Security.Cryptography.CryptographicException or JsonException)
        {
            AssistancePeerList.ItemsSource = null;
            AssistancePeerListGroup.Visibility = Visibility.Collapsed;
            AssistanceTrustEmptyText.Text = "本机信任记录暂时无法读取";
            AssistanceTrustEmptyText.Visibility = Visibility.Visible;
        }
    }
    private async void AssistanceRevokeTrust_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as Button)?.Tag is not string id) return;
        await RunAction(async () => await CommandAsync(new("assistance-trust-revoke", ClientId: id)));
    }
    private async void AssistanceForgetPeer_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as Button)?.Tag is not string id) return;
        await RunAction(() => {
            var peer = AssistancePeers.List().FirstOrDefault(item => item.Id == id);
            if (peer is not null) AssistancePeers.Forget(peer);
            ShowFeedback("已移除本机记录");
            return Task.CompletedTask;
        });
    }
    private async void AssistanceEnabled_Click(object sender, RoutedEventArgs args)
    {
        bool enable = AssistanceEnabledToggle.IsChecked == true;
        await RunAction(async () => await CommandAsync(new("assistance-enable", AssistanceEnabled: enable)));
        RenderAssistance();
    }
    private async void AssistanceRefresh_Click(object sender, RoutedEventArgs args) =>
        await RunAction(async () => await CommandAsync(new("assistance-refresh")));
    private void RenderAssistanceCode()
    {
        bool hasCode = !string.IsNullOrEmpty(_displayedAssistanceCode);
        AssistanceCodeText.Text = hasCode ? _assistanceCodeVisible ? _displayedAssistanceCode! : "****-****" :
            _status?.Assistance?.Enabled == false ? "已关闭" : "等待可用";
        AssistanceCodeVisibilityButton.IsEnabled = hasCode;
        string label = _assistanceCodeVisible ? "隐藏协助码" : "显示协助码";
        AutomationProperties.SetName(AssistanceCodeVisibilityButton, label);
        AssistanceCodeVisibilityIcon.SetResourceReference(System.Windows.Shapes.Path.DataProperty,
            _assistanceCodeVisible ? "AssistanceEyeOffIcon" : "AssistanceEyeIcon");
    }
    private void AssistanceCodeVisibility_Click(object sender, RoutedEventArgs args)
    {
        _assistanceCodeVisible = !_assistanceCodeVisible;
        RenderAssistanceCode();
    }
    private async void AssistanceCopy_Click(object sender, RoutedEventArgs args)
    {
        string? code = _status?.Assistance?.Code;
        await RunAction(async () => {
            var response = await _runtime.SendAsync(new("assistance-connection-info"), _closing.Token);
            if (!response.Success) throw new InvalidOperationException(response.Message);
            if (code is null || _status?.Assistance?.State != "offered" || _status.Assistance.Code != code)
                throw new InvalidOperationException("协助码已失效，请重新开启临时协助。");
            using var data = JsonDocument.Parse(response.PayloadJson!);
            System.Windows.Clipboard.SetText(AssistanceClient.CreateInvitation(
                data.RootElement.GetProperty("connectionInfo").GetString()!, code));
            ShowFeedback("连接信息与协助码已复制");
        });
    }
    private void AssistanceConnection_Changed(object sender, TextChangedEventArgs args)
    {
        if (AssistanceJoinCodeBox is null) return;
        _assistanceInvitation = null;
        if (_importedAssistanceCode == AssistanceJoinCodeBox.Text) AssistanceJoinCodeBox.Clear();
        _importedAssistanceCode = null;
        try
        {
            var invitation = _assistanceInvitation = AssistanceClient.ParseConnection(AssistanceConnectionBox.Text.Trim());
            if (invitation.Code is not null && AssistanceClient.ValidCode(invitation.Code))
                AssistanceJoinCodeBox.Text = _importedAssistanceCode = invitation.Code;
        }
        catch (ArgumentException) { }
        UpdateAssistanceJoinButton();
    }
    private void AssistancePaste_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText()) { ShowFeedback("剪贴板中没有连接信息", error: true); return; }
            ImportAssistanceInvitation(System.Windows.Clipboard.GetText());
        }
        catch (System.Runtime.InteropServices.ExternalException) { ShowFeedback("暂时无法读取剪贴板，请重试", error: true); }
    }
    internal bool ImportAssistanceInvitation(string text)
    {
        try
        {
            if (text.Length > AssistanceConnectionBox.MaxLength) throw new ArgumentException();
            _ = AssistanceClient.ParseConnection(text.Trim());
        }
        catch (ArgumentException) { ShowFeedback("请复制对方的完整连接信息", error: true); return false; }
        string invitation = text.Trim();
        if (AssistanceConnectionBox.Text == invitation)
            AssistanceConnection_Changed(AssistanceConnectionBox, new TextChangedEventArgs(System.Windows.Controls.TextBox.TextChangedEvent, UndoAction.None));
        else AssistanceConnectionBox.Text = invitation;
        if (AssistanceJoinButton.IsEnabled) AssistanceJoinButton.Focus();
        else AssistanceJoinCodeBox.Focus();
        return true;
    }
    private void AssistanceJoinCode_Changed(object sender, TextChangedEventArgs args) => UpdateAssistanceJoinButton();
    private void UpdateAssistanceJoinButton()
    {
        if (AssistanceJoinButton is null || AssistanceConnectionBox is null || AssistanceJoinCodeBox is null) return;
        bool valid = _assistanceInvitation is not null;
        if (AssistanceConnectionHint is not null)
            AssistanceConnectionHint.Visibility = !valid && !string.IsNullOrWhiteSpace(AssistanceConnectionBox.Text) && _assistanceClient is null
                ? Visibility.Visible : Visibility.Collapsed;
        AssistanceJoinButton.IsEnabled = !_busy && _assistanceClient is null && valid && AssistanceClient.ValidCode(AssistanceJoinCodeBox.Text);
    }
    private void ShowAssistanceClientState(string text)
    {
        AssistanceClientState.Text = text;
        AssistanceClientState.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void AssistancePermission_Changed(object sender, RoutedEventArgs args) => UpdateAssistancePermissionAction();
    private void UpdateAssistancePermissionAction()
    {
        if (AssistanceAllowButton is null) return;
        AssistanceAllowButton.IsEnabled = !_busy && _status?.Assistance?.State == "pending" &&
            (AssistanceViewPermission.IsChecked == true || AssistanceControlPermission.IsChecked == true);
    }
    private async void AssistanceApprove_Click(object sender, RoutedEventArgs args)
    {
        string? mode = AssistanceControlPermission.IsChecked == true ? "control" :
            AssistanceViewPermission.IsChecked == true ? "view" : null;
        string? session = _assistancePermissionSession;
        await RunAction(async () => {
            var pending = _status?.Assistance;
            if (pending?.State != "pending" || pending.SessionId != session)
                throw new InvalidOperationException("请求已结束，请刷新。");
            if (mode is null) throw new InvalidOperationException("请选择本次权限。");
            await CommandAsync(new(mode == "control" ? "assistance-approve-control" : "assistance-approve-view",
                RequestId: session, TrustDevice: AssistanceTrustDeviceCheck.IsChecked == true));
        });
    }
    private async void AssistanceEnd_Click(object sender, RoutedEventArgs args) => await RunAction(async () =>
        await CommandAsync(new("assistance-end", RequestId: _status?.Assistance?.SessionId)));
    private async void AssistanceJoin_Click(object sender, RoutedEventArgs args) => await JoinAssistanceAsync();
    private async void AssistanceTrustedJoin_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as Button)?.Tag is string id) await JoinAssistanceAsync(id);
    }
    private async Task JoinAssistanceAsync(string? trustedPeerId = null)
    {
        if (_assistanceClient is not null) return;
        AssistanceClient client;
        try { client = new AssistanceClient(store: AssistancePeers); }
        catch (Exception error)
        {
            ShowAssistanceClientState("无法读取本机信任记录：" + error.Message);
            return;
        }
        _assistanceClient = client;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _assistancePeerLifetime = lifetime;
        AssistanceJoinButton.IsEnabled = false; AssistancePeerCancelButton.IsEnabled = true;
        AssistancePeerList.IsEnabled = false;
        AssistanceConnectionBox.IsEnabled = AssistanceJoinCodeBox.IsEnabled = false;
        AssistancePasteButton.IsEnabled = false;
        ShowAssistanceClientState("正在连接…");
        try
        {
            AssistanceClient.CleanupPreviousSessions();
            var reply = trustedPeerId is not null
                ? await client.JoinTrustedAsync(trustedPeerId, lifetime.Token)
                : await client.JoinAsync(AssistanceConnectionBox.Text, AssistanceJoinCodeBox.Text, lifetime.Token);
            AssistanceJoinCodeBox.Clear();
            ShowAssistanceClientState(reply.State == "pending" ? "等待被控电脑确认 · 核对号 " + reply.Comparison : "正在准备画面");
            while (reply.State is "pending" or "starting")
            {
                await Task.Delay(2000, lifetime.Token);
                reply = await client.PollAsync(lifetime.Token);
                if (reply.State == "starting") ShowAssistanceClientState("正在准备画面 · 核对号 " + reply.Comparison);
            }
            if (reply.State != "active") throw new InvalidOperationException("被控端拒绝了请求，或请求已过期。");
            await client.StreamAsync(reply, () => {
                AssistancePeerCancelButton.Content = "断开";
                ShowAssistanceClientState("已获准：" + (reply.Mode == "view" ? "仅观看" : "允许控制") + " · 核对号 " + reply.Comparison);
            }, lifetime.Token);
            ShowAssistanceClientState("协助已结束");
        }
        catch (OperationCanceledException) { ShowAssistanceClientState("已取消协助"); }
        catch (Exception error) { ShowAssistanceClientState("协助未完成：" + error.Message); }
        finally
        {
            try
            {
                await client.DisposeAsync();
                if (client.CleanupWarning is not null) ShowAssistanceClientState(client.CleanupWarning);
            }
            catch (Exception) { ShowAssistanceClientState("协助收尾未完成，请在被控电脑确认已断开；下次启动会重试清理。"); }
            _assistanceClient = null; _assistancePeerLifetime = null;
            AssistancePeerCancelButton.IsEnabled = false;
            AssistancePeerCancelButton.Content = "取消";
            AssistanceConnectionBox.IsEnabled = AssistanceJoinCodeBox.IsEnabled = true;
            AssistancePasteButton.IsEnabled = true;
            UpdateAssistanceJoinButton();
            RenderAssistanceTrustedDevices();
        }
    }
    private void AssistancePeerCancel_Click(object sender, RoutedEventArgs args) => _assistancePeerLifetime?.Cancel();
}
