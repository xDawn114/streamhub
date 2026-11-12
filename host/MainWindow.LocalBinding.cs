using System.Text.Json;
using System.Windows;
using StreamHub.Host.Runtime;

namespace StreamHub.Host;

public partial class MainWindow
{
    private bool _bindingRefreshing;
    private string? _bindingRequest;
    private async void DiscoveryDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        DiscoveryDiagnosticsText.Visibility=Visibility.Visible;
        DiscoveryDiagnosticsText.Text="正在读取附近发现状态…";
        try
        {
            var response=await _runtime.SendAsync(new("lan-discovery-status"),_closing.Token);
            var state=response.Success && response.PayloadJson is not null
                ?JsonSerializer.Deserialize<LanDiscoveryStatus>(response.PayloadJson,RuntimeProtocol.JsonOptions):null;
            DiscoveryDiagnosticsText.Text=state is null?"发现状态暂不可用，请确认后台服务已启动。":
                $"附近发现：{(state.Running?"运行中":"未运行")} · 网络接口 {state.Interfaces} · 已监听 {state.Joined}\n"
                +$"原始收包 {state.Received} · 有效查询 {state.Queries} · 直接回复 {state.UnicastReplies} · 广播公告 {state.MulticastReplies}\n"
                +$"过滤：非局域网 {state.OutsideLan} / 响应包 {state.Responses} / 其他服务 {state.OtherService} / 格式异常 {state.Malformed}\n"
                +$"暂缓：频率限制 {state.Throttled} / 无可用路由 {state.NoRoute} · 错误 {state.Errors}\n"
                +(state.LastError is null?"可与平板“查找详情”对照；计数从本次后台启动累计，点击本按钮刷新。":"最近错误："+state.LastError);
        }
        catch(OperationCanceledException){}
        catch(Exception){DiscoveryDiagnosticsText.Text="无法读取附近发现状态，请稍后重试。";}
    }
    private async Task RefreshLocalBindingAsync()
    {
        if (_bindingRefreshing || _closing.IsCancellationRequested) return;
        _bindingRefreshing = true;
        try
        {
            var response = await _runtime.SendAsync(new("binding-status"), _closing.Token);
            if (!response.Success || response.PayloadJson is null) return;
            var snapshot = JsonSerializer.Deserialize<LocalBindingSnapshot>(response.PayloadJson, RuntimeProtocol.JsonOptions);
            if (snapshot is null) return;
            BindingCodeText.Text = snapshot.Code ?? "尚未生成短码";
            var request = snapshot.Request;
            _bindingRequest = request?.State == "pending" ? request.Id : null;
            BindingApproveButton.Visibility = BindingRejectButton.Visibility = _bindingRequest is null ? Visibility.Collapsed : Visibility.Visible;
            BindingRequestText.Text = request?.State switch
            {
                "pending" => $"{request.Name} 请求绑定\n请与平板核对校验码：{request.Comparison}\n一致后才允许；不认识的设备请拒绝。",
                "approved" => "已允许，等待平板保存绑定。",
                "delivered" => "绑定凭据已交给平板，短码已失效。",
                "rejected" => "已拒绝该设备。",
                "expired" => "设备确认已过期，请重新生成短码。",
                _ => snapshot.ExpiresAt is { } expiry ? $"{expiry.ToLocalTime():HH:mm:ss}前有效；只能使用一次，需电脑确认。" : "短码三分钟有效，只能使用一次。"
            };
        }
        catch (OperationCanceledException) { }
        catch (Exception) { BindingRequestText.Text = "设备确认暂不可用，请检查后台服务。"; }
        finally { _bindingRefreshing = false; }
    }
    private async Task BindingActionAsync(string command)
    {
        await RunAction(async () =>
        {
            await CommandAsync(new(command, RequestId: _bindingRequest));
            await RefreshLocalBindingAsync();
        });
    }
    private async void StartLocalBinding_Click(object sender, RoutedEventArgs e) => await BindingActionAsync("binding-start");
    private async void CancelLocalBinding_Click(object sender, RoutedEventArgs e) => await BindingActionAsync("binding-cancel");
    private async void ApproveLocalBinding_Click(object sender, RoutedEventArgs e) => await BindingActionAsync("binding-approve");
    private async void RejectLocalBinding_Click(object sender, RoutedEventArgs e) => await BindingActionAsync("binding-reject");
}
