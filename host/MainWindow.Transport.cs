using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using StreamHub.Host.Networking;
using StreamHub.Host.Runtime;

namespace StreamHub.Host;

public partial class MainWindow
{
    private TransportSettings? _transportConfig;
    private bool _providerEditing, _renderingProvider;
    private string? _requestedProvider;
    private void ClearLoginUrl()
    {
        LoginUrlBox.Clear();
        LoginUrlBox.Visibility = CopyLoginButton.Visibility = LoginBrowserHint.Visibility = Visibility.Collapsed;
    }
    private void RenderTransport(TransportSettings? settings)
    {
        if (settings is null) return;
        settings = settings.Validate();
        if (_transportConfig != settings) ClearLoginUrl();
        _transportConfig = settings;
        ProviderSavedText.Text = ""; ProviderSavedText.Visibility = Visibility.Collapsed;
        RemoteNetworkActions.IsEnabled = settings.Transport.UsesAccount;
        if (_providerEditing) { UpdateProviderApply(); return; }
        _renderingProvider = true;
        try
        {
            ProviderBox.SelectedItem = ProviderBox.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == settings.Provider);
            ProviderServerBox.Text = settings.ServerUrl;
            ProviderManualBox.Text = settings.ManualAddresses;
            ShowProviderFields();
        }
        finally { _renderingProvider = false; UpdateProviderApply(); }
    }
    private void ShowProviderFields()
    {
        if (ProviderServerPanel is null || ProviderManualPanel is null) return;
        var id = (ProviderBox.SelectedItem as ComboBoxItem)?.Tag as string;
        ProviderServerPanel.Visibility = id == "self-hosted" ? Visibility.Visible : Visibility.Collapsed;
        ProviderManualPanel.Visibility = id == "manual" ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void ProviderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowProviderFields();
        UpdateProviderApply();
        if (_renderingProvider || !IsLoaded || _transportConfig is null) return;
        string? requested = (ProviderBox.SelectedItem as ComboBoxItem)?.Tag as string;
        if (requested is null || requested == _transportConfig.Provider) return;
        string address = requested == "self-hosted" ? ProviderServerBox.Text : ProviderManualBox.Text;
        _providerEditing = false;
        RenderTransport(_transportConfig);
        if (requested is "self-hosted" or "manual")
        {
            _requestedProvider = requested;
            TransportDialogTitle.Text = requested == "self-hosted" ? "配置自建通道" : "配置手动通道";
            TransportAddressLabel.Text = requested == "self-hosted" ? "服务区 HTTPS 地址" : "本机对外可达 IPv4 地址";
            TransportAddressBox.Text = address;
            TransportDialogError.Text = "";
            TransportDialog.Visibility = Visibility.Visible;
            TransportAddressBox.Focus();
        }
        else await ConfirmTransportAsync(new TransportSettings(requested, "", "").Validate());
    }
    private void ProviderInputChanged(object sender, TextChangedEventArgs e)
    {
        if (!_renderingProvider && IsLoaded) _providerEditing = true;
        UpdateProviderApply();
    }
    private void UpdateProviderApply()
    {
        if (ApplyProviderButton is null) return;
        var id = (ProviderBox.SelectedItem as ComboBoxItem)?.Tag as string;
        ApplyProviderButton.Visibility = id is "self-hosted" or "manual" ? Visibility.Visible : Visibility.Collapsed;
        ApplyProviderButton.Content = id == "self-hosted" ? "保存服务区地址" : "保存发布地址";
        try
        {
            var selected = new TransportSettings((ProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "",
                ProviderServerBox.Text.Trim(), ProviderManualBox.Text.Trim()).Validate();
            ApplyProviderButton.IsEnabled = _transportConfig is null || selected != _transportConfig;
        }
        catch (ArgumentException) { ApplyProviderButton.IsEnabled = false; }
    }
    private async void SaveProvider_Click(object sender, RoutedEventArgs e)
    {
        TransportSettings settings;
        try
        {
            settings = new TransportSettings((ProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "",
                ProviderServerBox.Text.Trim(), ProviderManualBox.Text.Trim()).Validate();
        }
        catch (ArgumentException error) { ProviderMessage.Text = error.Message; return; }
        await ConfirmTransportAsync(settings);
    }
    private void CloseTransportDialog_Click(object sender, RoutedEventArgs e) { TransportDialog.Visibility = Visibility.Collapsed; _requestedProvider = null; ProviderBox.Focus(); }
    private void TransportDialog_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) { CloseTransportDialog_Click(sender,e); e.Handled = true; }
    }
    private async void SaveTransportDialog_Click(object sender, RoutedEventArgs e)
    {
        if (_requestedProvider is null) return;
        TransportSettings settings;
        try { settings = new TransportSettings(_requestedProvider, _requestedProvider == "self-hosted" ? TransportAddressBox.Text.Trim() : "", _requestedProvider == "manual" ? TransportAddressBox.Text.Trim() : "").Validate(); }
        catch (ArgumentException error) { TransportDialogError.Text = error.Message; return; }
        CloseTransportDialog_Click(sender,e);
        await ConfirmTransportAsync(settings);
    }
    private async Task ConfirmTransportAsync(TransportSettings settings)
    {
        if (_status?.Network.Desired == true && System.Windows.MessageBox.Show(this,
            "切换通道会断开当前异地连接，电脑绑定和账号保留。", "切换连接通道？", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAction(async () =>
        {
            ClearLoginUrl();
            var response = await _runtime.SendAsync(new("network-provider", Provider: settings.Provider,
                ServerUrl: settings.ServerUrl, ManualAddresses: settings.ManualAddresses), _closing.Token);
            ProviderMessage.Text = response.Message; ShowFeedback(response.Message, error: !response.Success);
            _providerEditing = false;
            RenderTransport(response.Success ? settings : _transportConfig);
            if (!response.Success) return;
            _network = null;
            PeerList.ItemsSource = null;
        });
    }
    private async void NetworkLogout_Click(object sender, RoutedEventArgs e) => await RunAction(async () =>
    {
        ClearLoginUrl();
        await CommandAsync(new("network-logout"));
    });
}
