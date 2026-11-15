using System.Windows;
using System.Windows.Threading;

namespace StreamHub.Host;

public partial class MainWindow
{
    private void OpenFileAccessSettings_Click(object sender, RoutedEventArgs args)
    {
        ShowDevicePermissionsDialog();
        var device = _controlDeviceList?.Devices.FirstOrDefault(d => d.ClientId == FileDeviceBox.SelectedValue as string);
        if (device is not null) ControlDeviceBox.SelectedValue = device.Id;
        FileAccessDetails.IsExpanded = true;

    }

    private void OpenSecuritySettings_Click(object sender, RoutedEventArgs args)
    {
        Pages.SelectedItem = SettingsPage;
        SettingsCategories.SelectedIndex = 1;
    }
}
