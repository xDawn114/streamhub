using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using StreamHub.Host.Runtime;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;

namespace StreamHub.Host;

public partial class MainWindow
{
    private void CloseTransientPopovers()
    {
        if (DeviceDetailsPopover is null) return;
        DeviceDetailsPopover.IsOpen = FeedbackHistoryPopover.IsOpen = false;
        if (AccountPopover is not null) AccountPopover.IsOpen = AcceptancePopover.IsOpen = false;
        if (BoundDeviceDetailsPopover is not null) BoundDeviceDetailsPopover.IsOpen = false;
    }
    private async void OpenDeviceCards_Click(object sender, RoutedEventArgs args)
    {
        CloseTransientPopovers(); Pages.SelectedItem = HomePage;
        await RefreshDeviceCardsAsync();
    }
    private void OpenDeviceDetails_Click(object sender, RoutedEventArgs args)
    {
        bool open = !DeviceDetailsPopover.IsOpen;
        CloseTransientPopovers(); DeviceDetailsPopover.IsOpen = open;
    }
    private void OpenAccount_Click(object sender, RoutedEventArgs args)
    {
        CloseTransientPopovers(); Pages.SelectedItem = SettingsPage;
        SettingsCategories.SelectedItem = NetworkSettingsCategory;
    }
    internal void OpenAssistance() { CloseTransientPopovers(); Pages.SelectedItem = HomePage; }
    private void AssistanceNetwork_Click(object sender, RoutedEventArgs args)
    {
        OpenAccount_Click(sender, args);
        NetworkSetupExpander.IsExpanded = true;
        NetworkStateText.BringIntoView();
    }
    private async void OpenDevices_Click(object sender, RoutedEventArgs args) => await OpenBindingAsync();
    private void OpenDiagnostics_Click(object sender, RoutedEventArgs args) { CloseTransientPopovers(); Pages.SelectedItem = DiagnosticsPage; }
    private void OpenUpdates_Click(object sender, RoutedEventArgs args)
    {
        CloseTransientPopovers(); Pages.SelectedItem = SettingsPage; SettingsCategories.SelectedItem = MaintenanceSettingsCategory;
        CheckUpdate_Click(sender, args);
    }
    private void OpenHeaderMenu_Click(object sender, RoutedEventArgs args) => OpenMenu((Button)sender);
    private void OpenBindingMenu_Click(object sender, RoutedEventArgs args) => OpenMenu((Button)sender);
    private void TransientPopover_Opened(object? sender, EventArgs args)
    {
        if (sender is Popup { Child: UIElement content })
        {
            FocusInput.SetKeyboardMode(content, FocusInput.GetKeyboardMode(this));
            if (!content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))) content.Focus();
        }
    }
    private void TransientPopover_KeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || sender is not Popup popup) return;
        popup.IsOpen = false;
        popup.PlacementTarget?.Focus();
        args.Handled = true;
    }
    private static void OpenMenu(Button button)
    {
        if (button.ContextMenu is not { } menu) return;
        FocusInput.SetKeyboardMode(menu, FocusInput.GetKeyboardMode(button));
        menu.PlacementTarget = button;
        if (Window.GetWindow(button) is { } owner)
        {
            menu.MaxWidth = Math.Max(1, owner.ActualWidth - 16);
            menu.MaxHeight = Math.Max(1, owner.ActualHeight - 16);
            menu.Placement = PlacementMode.Custom;
            menu.CustomPopupPlacementCallback = (size, target, offset) =>
            {
                var origin = owner.TranslatePoint(new Point(), button);
                double left = origin.X + 8, top = origin.Y + 8;
                double right = origin.X + owner.ActualWidth - size.Width - 8;
                double bottom = origin.Y + owner.ActualHeight - size.Height - 8;
                double x = Math.Clamp(target.Width - size.Width, left, Math.Max(left, right));
                double y = target.Height + 4;
                if (y > bottom) y = -size.Height - 4;
                y = Math.Clamp(y, top, Math.Max(top, bottom));
                return [new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.None)];
            };
        }
        menu.IsOpen = true;
    }
    private void OpenLegacyPairing_Click(object sender, RoutedEventArgs args)
    {
        BindingDialog.Visibility = Visibility.Visible; BindingAdvanced.IsExpanded = true; LegacyPairingDetails.IsExpanded = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => LegacyPairingDetails.BringIntoView()));
    }

    private void RefreshServiceControls()
    {
        if (HomeSummaryText is null) return;
        RefreshUnifiedStatus();
        RenderUnifiedDevices();
        RenderFilesInlineState();
    }
    private async void BoundDevicesPage_Loaded(object sender, RoutedEventArgs args) => await ReadControlDevicesAsync();
    private void RenderBoundDevicesSummary() => RenderUnifiedDevices();
    private void OpenDevicePermissions_Click(object sender, RoutedEventArgs args)
    {
        ShowDevicePermissionsDialog();
        if (sender is FrameworkElement { Tag: string id }) {
            ControlDeviceBox.SelectedValue = id;
            var device = _controlDeviceList?.Devices.FirstOrDefault(d => d.Id == id);
            if (device is not null) FileDeviceBox.SelectedValue = device.ClientId;
        }
        FileAccessDetails.IsExpanded = true;

    }
}
