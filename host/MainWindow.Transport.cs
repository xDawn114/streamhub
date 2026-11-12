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
    private void ClearLoginUrl()
    {
        LoginUrlBox.Clear();
        LoginUrlBox.Visibility = CopyLoginButton.Visibility = Visibility.Collapsed;
    }
    private void RenderTransport(TransportSettings? settings)
    {
        if (settings is null) return;
        settings = settings.Validate();
        if (_transportConfig != settings) ClearLoginUrl();
        _transportConfig = settings;
        ProviderSavedText.Text = "当前通道：" + settings.Transport.Label;
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
    private void ProviderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowProviderFields();
        if (!_renderingProvider && IsLoaded) _providerEditing = true;
        UpdateProviderApply();
    }
    private void ProviderInputChanged(object sender, TextChangedEventArgs e)
    {
        if (!_renderingProvider && IsLoaded) _providerEditing = true;
        UpdateProviderApply();
    }
    private void UpdateProviderApply()
    {
        if (ApplyProviderButton is null) return;
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
        await RunAction(async () =>
        {
            ClearLoginUrl();
            var response = await _runtime.SendAsync(new("network-provider", Provider: settings.Provider,
                ServerUrl: settings.ServerUrl, ManualAddresses: settings.ManualAddresses), _closing.Token);
            ProviderMessage.Text = FeedbackText.Text = response.Message;
            if (!response.Success) return;
            _providerEditing = false;
            RenderTransport(settings);
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
