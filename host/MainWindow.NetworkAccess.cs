using System.Text.Json;
using System.Windows;
using StreamHub.Host.Networking;
using StreamHub.Host.Runtime;

namespace StreamHub.Host;

public partial class MainWindow
{
    private NetworkAccessState? _networkAccess;
    private bool _savingNetworkAccess, _readingNetworkAccess;
    private int _networkAccessRequest;

    private async void NetworkAccess_Loaded(object sender, RoutedEventArgs args) => await ReadNetworkAccessAsync();
    private async void NetworkAccess_Changed(object sender, RoutedEventArgs args)
    {
        if (_savingNetworkAccess || _networkAccess is null) return;
        bool requested = PublicLanAccessCheck.IsChecked == true;
        RenderNetworkAccess(_networkAccess);
        string prompt = requested
            ? "允许公共网络上的局域网设备访问远控和发现端口？设备仍需绑定；公共 Wi-Fi 上会增加暴露范围。"
            : "关闭公共网络上的局域网访问？电脑正在使用公共网络时，局域网直连会中断。";
        if (System.Windows.MessageBox.Show(this, prompt, requested ? "开启公共网络访问" : "关闭公共网络访问",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await ApplyNetworkAccessAsync(requested);
    }

    private void RenderNetworkAccess(NetworkAccessState access)
    {
        _networkAccess = access;
        PublicLanAccessCheck.IsChecked = access.AllowPublicLan && access.ConfigurationValid && access.FirewallApplied;
        PublicLanAccessCheck.IsEnabled = !_savingNetworkAccess && access.ConfigurationValid && access.FirewallApplied;
        SetNetworkAccessStatus(access.LastError is { Length: > 0 } error ? error :
            !access.ConfigurationValid ? "网络策略无法读取，请重试" :
            !access.FirewallApplied ? "网络规则尚未生效，请重试" :
            null,
            warning: access.LastError is { Length: > 0 } || !access.ConfigurationValid || !access.FirewallApplied);
    }

    private void SetNetworkAccessStatus(string? message, bool warning = false)
    {
        NetworkAccessStateText.Text = message ?? "";
        NetworkAccessStateText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        NetworkAccessStateText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, warning ? "WarningText" : "Muted");
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
            else { PublicLanAccessCheck.IsEnabled = false; SetNetworkAccessStatus(response.Message, warning: true); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { PublicLanAccessCheck.IsEnabled = false; SetNetworkAccessStatus("网络访问状态读取失败：" + error.Message, warning: true); }
        finally { _readingNetworkAccess = false; }
    }

    private async void RetryNetworkAccess_Click(object sender, RoutedEventArgs args) => await ReadNetworkAccessAsync();
    private async Task ApplyNetworkAccessAsync(bool enabled)
    {
        if (_savingNetworkAccess) return;
        _savingNetworkAccess = true; ++_networkAccessRequest; PublicLanAccessCheck.IsEnabled = false;
        SetNetworkAccessStatus("正在应用网络设置…");
        try
        {
            var response = await _runtime.SendAsync(new("network-access-save", AllowPublicLan: enabled), _closing.Token);
            if (response.PayloadJson is not null &&
                JsonSerializer.Deserialize<NetworkAccessState>(response.PayloadJson, RuntimeProtocol.JsonOptions) is {} access)
                _networkAccess = access;
            ShowFeedback(response.Message, error: !response.Success);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowFeedback("网络设置未确认：" + error.Message, error: true); }
        finally
        {
            _savingNetworkAccess = false;
            if (_networkAccess is {} confirmed) RenderNetworkAccess(confirmed);
            await ReadNetworkAccessAsync();
        }
    }
}
