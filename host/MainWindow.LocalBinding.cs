using System.Text.Json;
using System.Windows;
using StreamHub.Host.Runtime;

namespace StreamHub.Host;

public partial class MainWindow
{
    private bool _bindingRefreshing;
    private string? _bindingRequest;
    private string[] BindingAddresses() => _addresses.Where(address =>
    {
        if (!System.Net.IPAddress.TryParse(address, out var ip)) return false;
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 ||
            bytes[0] == 172 && bytes[1] is >= 16 and <= 31);
    }).ToArray();
    private void CopyBindingAddress_Click(object sender, RoutedEventArgs e) =>
        CopyText(string.Join(Environment.NewLine, BindingAddresses()), "电脑 IPv4 已复制。");
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
            _bindingSnapshot = snapshot;
            RenderBindingCountdown();
            var addresses = BindingAddresses();
            BindingAddressText.Text = addresses.Length == 0 ? "暂无局域网 IPv4" : string.Join(Environment.NewLine, addresses);
            CopyBindingAddressButton.IsEnabled = addresses.Length > 0;
            BindingCodeText.Text = snapshot.Code ?? "生成短码以添加设备";
            BindingCodeText.FontSize = snapshot.Code is null ? 14 : 26;
            BindingCodeText.FontWeight = snapshot.Code is null ? FontWeights.Normal : FontWeights.SemiBold;
            BindingCodeText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, snapshot.Code is null ? "Muted" : "TextPrimary");
            var request = snapshot.Request;
            if (request?.State == "delivered" && BindingDialog.Visibility == Visibility.Visible)
            { BindingDialog.Visibility = Visibility.Collapsed; _bindingCountdown.Stop(); _ = ReadControlDevicesAsync(); }
            _bindingRequest = request?.State == "pending" ? request.Id : null;
            bool confirming = request?.State is "pending" or "approved";
            BindingEntryPanel.Visibility = confirming ? Visibility.Collapsed : Visibility.Visible;
            BindingGenerateButton.Visibility = confirming ? Visibility.Collapsed : Visibility.Visible;
            BindingGenerateButton.Content = snapshot.Code is null ? "生成短码" : "重新生成短码";
            CancelBindingButton.Visibility = snapshot.Code is null || confirming ? Visibility.Collapsed : Visibility.Visible;
            BindingComparisonPanel.Visibility = _bindingRequest is null ? Visibility.Collapsed : Visibility.Visible;
            BindingComparisonText.Text = _bindingRequest is null ? "" : request!.Comparison;
            BindingApproveButton.Visibility = BindingRejectButton.Visibility = _bindingRequest is null ? Visibility.Collapsed : Visibility.Visible;
            BindingRequestText.Text = request?.State switch
            {
                "pending" => $"{request.Name} 请求绑定",
                "approved" => "已允许，等待平板保存绑定。",
                "delivered" => "绑定成功。",
                "rejected" => "已拒绝该设备。",
                "expired" => "设备确认已过期，请重新生成短码。",
                _ => "在平板上填写电脑 IPv4 和六位短码。"
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
