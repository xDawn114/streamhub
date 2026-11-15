using System.Text.Json;
using System.Windows;
using StreamHub.Host.Networking;
using StreamHub.Host.Runtime;

namespace StreamHub.Host;

public partial class MainWindow
{
    private NetworkAccessState? _networkAccess;
    private bool _networkAccessDirty, _savingNetworkAccess, _readingNetworkAccess;
    private int _networkAccessRequest;

    private async void NetworkAccess_Loaded(object sender, RoutedEventArgs args) => await ReadNetworkAccessAsync();
    private void NetworkAccess_Changed(object sender, RoutedEventArgs args) => _networkAccessDirty = true;

    private void RenderNetworkAccess(NetworkAccessState access)
    {
        _networkAccess = access;
        if (!_networkAccessDirty) PublicLanAccessCheck.IsChecked = access.AllowPublicLan;
        NetworkAccessStateText.Text = access.Message +
            (access.LastError is { Length: > 0 } error ? "\n" + error : "") +
            (access.VpnReady ? "\n异地连接通过已核实的安全隧道放行。" : "\n安全隧道就绪后会自动应用异地连接规则。");
        NetworkAccessStateText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            access.LastError is not null || access.MigrationNoticePending ? "WarningText" : "Muted");
    }

    private async Task ReadNetworkAccessAsync()
    {
        if (_savingNetworkAccess || _readingNetworkAccess) return;
        _readingNetworkAccess = true;
        int generation = ++_networkAccessRequest;
        try
        {
            var response = await _runtime.SendAsync(new("network-access-status"), _closing.Token);
            if (generation != _networkAccessRequest) return;
            if (response.Success && response.PayloadJson is not null &&
                JsonSerializer.Deserialize<NetworkAccessState>(response.PayloadJson, RuntimeProtocol.JsonOptions) is {} access)
                RenderNetworkAccess(access);
            else NetworkAccessStateText.Text = response.Message;
        }
        catch (OperationCanceledException) { }
        catch (JsonException) { NetworkAccessStateText.Text = "网络访问状态无法读取，请确认电脑后台版本。"; }
        finally { _readingNetworkAccess = false; }
    }

    private async void SaveNetworkAccess_Click(object sender, RoutedEventArgs args)
    {
        if (_savingNetworkAccess) return;
        bool enabled = PublicLanAccessCheck.IsChecked == true;
        string prompt = enabled
            ? "允许公共网络上的局域网设备访问远控和发现端口？设备仍需绑定认证，但在学校、酒店等公共 Wi-Fi 上会增加暴露范围。"
            : "关闭公共网络上的局域网访问？若电脑当前使用公共网络，局域网直连会中断；已核实的异地安全隧道仍按独立规则放行。";
        if ((_networkAccess?.AllowPublicLan != enabled || _networkAccess?.MigrationNoticePending == true) &&
            System.Windows.MessageBox.Show(this, prompt, "保存网络访问策略", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _savingNetworkAccess = true; ++_networkAccessRequest; SaveNetworkAccessButton.IsEnabled = false;
        try
        {
            var response = await _runtime.SendAsync(new("network-access-save", AllowPublicLan: enabled), _closing.Token);
            if (response.Success) _networkAccessDirty = false;
            if (response.PayloadJson is not null &&
                JsonSerializer.Deserialize<NetworkAccessState>(response.PayloadJson, RuntimeProtocol.JsonOptions) is {} access)
                RenderNetworkAccess(access);
            ShowFeedback(response.Message, error: !response.Success);
        }
        catch (OperationCanceledException) { }
        catch (JsonException) { NetworkAccessStateText.Text = "未能确认网络访问保存结果，请重新读取状态。"; }
        finally { _savingNetworkAccess = false; SaveNetworkAccessButton.IsEnabled = true; }
    }
}
