using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using StreamHub.Host.Runtime;

namespace StreamHub.Host;

public partial class MainWindow
{
    private ControlDeviceList? _controlDeviceList;
    private bool _renderingControlDevices, _savingControlScope;
    private int _controlScopeVersion;
    private async void ControlDevices_Loaded(object sender, RoutedEventArgs args) => await ReadControlDevicesAsync();
    private void RefreshControlDevices_Click(object sender, RoutedEventArgs args) => _ = RunAction(ReadControlDevicesAsync);
    private async Task ReadControlDevicesAsync()
    {
        int version = _controlScopeVersion;
        var result = await _runtime.SendAsync(new("control-device-list"), _closing.Token);
        if (_savingControlScope || version != _controlScopeVersion) return;
        if (!result.Success || result.PayloadJson is null)
        {
            _controlDeviceList = null;
            RenderUnifiedDevices();
            ControlDeviceStateText.Text = result.Message;
            return;
        }
        _controlDeviceList = JsonSerializer.Deserialize<ControlDeviceList>(result.PayloadJson, RuntimeProtocol.JsonOptions);
        if (_controlDeviceList is null) return;
        string? selected = ControlDeviceBox.SelectedValue as string;
        _renderingControlDevices = true;
        try
        {
            ControlDeviceBox.ItemsSource = _controlDeviceList.Devices.Select(d => new { d.Id,
                Label = DeviceDisplayName(d) + (d.Revoked ? "（已撤销）" : "") + " · ID " + d.Id[^6..] }).ToArray();
            ControlDeviceBox.SelectedValue = selected;
            if (ControlDeviceBox.SelectedIndex < 0 && ControlDeviceBox.Items.Count > 0) ControlDeviceBox.SelectedIndex = 0;
            AllowLegacyBindingCheck.IsChecked = _controlDeviceList.LegacyAllowed;
            ControlDeviceStateText.Text = _controlDeviceList.LegacyAllowed
                ? "旧版共享绑定仍可连接，无法按设备区分。请逐台用短码重新绑定，再关闭兼容；手工复制的旧绑定码也属于共享绑定。"
                : "旧版共享绑定已关闭；仅接受独立设备凭据。文件读写和删除权限仍需分别授权。";
        }
        finally { _renderingControlDevices = false; }
        RenderControlDevice();
        RenderBoundDevicesSummary();
    }
    private ControlDeviceSummary? SelectedControlDevice() => _controlDeviceList?.Devices.FirstOrDefault(d => d.Id == ControlDeviceBox.SelectedValue as string);
    private void ControlDevice_Changed(object sender, SelectionChangedEventArgs args)
    { if (!_renderingControlDevices) RenderControlDevice(); }
    private void RenderControlDevice()
    {
        var device = SelectedControlDevice();
        ControlRemoteCheck.IsChecked = (device?.Scope & ControlScope.Remote) != 0 && device is not null;
        ControlTabletCheck.IsChecked = (device?.Scope & ControlScope.Tablet) != 0 && device is not null;
        ControlFilesCheck.IsChecked = (device?.Scope & ControlScope.Files) != 0 && device is not null;
        ControlClipboardCheck.IsChecked = (device?.Scope & ControlScope.Clipboard) != 0 && device is not null;
        ControlScopePanel.IsEnabled = device is { Revoked: false };
    }
    private void SaveControlScope_Click(object sender, RoutedEventArgs args)
    {
        var device = SelectedControlDevice();
        if (_busy || _savingControlScope || device is null || device.Revoked) { RenderControlDevice(); return; }
        ControlScope scope = (ControlRemoteCheck.IsChecked == true ? ControlScope.Remote : 0) |
            (ControlTabletCheck.IsChecked == true ? ControlScope.Tablet : 0) |
            (ControlFilesCheck.IsChecked == true ? ControlScope.Files : 0) |
            (ControlClipboardCheck.IsChecked == true ? ControlScope.Clipboard : 0);
        if (scope == device.Scope) return;
        _ = RunAction(async () =>
        {
            if ((device.Scope & ~scope) != 0 && System.Windows.MessageBox.Show(this,
                "关闭权限会结束相关连接，继续？", "设备权限", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) { RenderControlDevice(); return; }
            _savingControlScope = true; _controlScopeVersion++;
            ControlScopePanel.IsEnabled = ControlDeviceBox.IsEnabled = false;
            try
            {
                var result = await _runtime.SendAsync(new("control-device-scope", RequestId:device.Id, DeviceScope:(int)scope), _closing.Token);
                if (result.Success && _controlDeviceList is not null)
                    _controlDeviceList = _controlDeviceList with { Devices = _controlDeviceList.Devices.Select(d => d.Id == device.Id ? d with { Scope = scope } : d).ToArray() };
                ShowFeedback(result.Success ? "权限已保存" : "权限保存失败：" + result.Message, error: !result.Success);
            }
            finally
            {
                _savingControlScope = false;
                RenderControlDevice(); ControlDeviceBox.IsEnabled = true;
            }
            await ReadControlDevicesAsync();
            await RefreshFilesAsync();
        });
    }
    private void RevokeControlDevice_Click(object sender, RoutedEventArgs args) => _ = RunAction(async () =>
    {
        var device = SelectedControlDevice(); if (device is null || device.Revoked) return;
        if (System.Windows.MessageBox.Show(this, "撤销“" + device.Name + "”的独立凭据与已关联媒体配对，并结束相关连接？恢复使用需在电脑确认后重新绑定。旧版共享绑定需单独关闭。",
            "撤销设备", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await CommandAsync(new("control-device-revoke", RequestId:device.Id)); await ReadControlDevicesAsync();
    });
    private void SaveLegacyBinding_Click(object sender, RoutedEventArgs args)
    {
        if (_busy) { AllowLegacyBindingCheck.IsChecked = _controlDeviceList?.LegacyAllowed == true; return; }
        bool allow = AllowLegacyBindingCheck.IsChecked == true;
        _ = RunAction(async () =>
        {
        try
        {
        if (System.Windows.MessageBox.Show(this, allow
                ? "重新允许使用旧版共享绑定码的设备？这些连接无法按设备识别和单独撤销。"
                : "关闭旧版共享绑定？尚未用短码重新绑定的手机和平板将无法通过控制接口连接。旧媒体配对需在设备页单独清理，关闭兼容不代表清除了所有历史媒体配对。",
                "旧版兼容策略", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        { await ReadControlDevicesAsync(); return; }
        await CommandAsync(new("control-legacy-policy", LegacyBindingAllowed:allow)); await ReadControlDevicesAsync();
        }
        finally { AllowLegacyBindingCheck.IsChecked = _controlDeviceList?.LegacyAllowed == true; }
        });
    }
    private void RotateControlDevice_Click(object sender, RoutedEventArgs args) => _ = RunAction(async () =>
    {
        await OpenBindingAsync();
        ShowFeedback("请在同一手机或平板用短码重新绑定，并在电脑核对确认；确认后该设备的旧独立凭据会失效。");
    });
}
