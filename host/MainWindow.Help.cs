using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;

namespace StreamHub.Host;

public partial class MainWindow
{
    private void OpenHelp_Click(object sender, RoutedEventArgs e)
    {
        CloseTransientPopovers();
        if (HelpContent.Children.Count == 0)
        {
            foreach (var (title, body) in new[] {
                ("添加电脑", "电脑点“+ 绑定设备”。安卓点“+ 添加电脑”，选择附近电脑，输入六位短码；核对两端校验码后，在电脑确认。短码三分钟有效。"),
                ("建立连接", "同一网络可直接连接；外出前先在两端配置同一异地通道并登录。安卓选择电脑，点“远程控制”。"),
                ("使用副屏", "安卓开启“接收电脑副屏请求”。电脑顶栏点“副屏”，选择设备，在安卓确认；安卓也可主动选择“用作扩展屏”。"),
                ("传文件", "电脑在设备的“权限”里允许文件访问，并选择可浏览目录。安卓点“文件”，浏览电脑文件后下载，或发送本机文件；删除权限单独开启。"),
                ("键盘与安全", "Ctrl+Alt+Tab 切换电脑窗口。需要 Alt+Tab 或 Win+Tab 时，在安卓输入设置手动启用可选辅助功能。Ctrl+Alt+F12 恢复隐私屏。"),
                ("暂停与断开", "“断开本次连接”结束当前主屏和副屏，电脑仍接受连接。“暂停接受连接”会阻止重连，重启后仍保持；有活动会话时入口显示“断开并暂停连接”，并先确认。暂停后可在同一入口恢复。"),
                ("连接失败", "先看电脑是否接受连接、两端网络是否可达、绑定是否有效。自动查找不到时，可用电脑地址与六位短码添加；详细原因在“诊断与维护”查看。") })
            {
                HelpContent.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) });
                HelpContent.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap });
            }
        }
        HelpDialog.Visibility = Visibility.Visible;
        UpdateHelpContentWidth();
        HelpScroller.ScrollToTop();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => HelpCloseButton.Focus()));
    }
    private void HelpScroller_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateHelpContentWidth();
    private void UpdateHelpContentWidth()
    {
        if (HelpScroller is null || HelpContent is null || HelpScroller.ViewportWidth <= 0) return;
        double width = Math.Max(0, HelpScroller.ViewportWidth - 8);
        HelpContent.Width = width;
        foreach (var text in HelpContent.Children.OfType<TextBlock>()) text.MaxWidth = width;
    }
    private void CloseHelp_Click(object sender, RoutedEventArgs e)
    {
        HelpDialog.Visibility = Visibility.Collapsed;
        HeaderMenuButton.Focus();
    }
    private void HelpDialog_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseHelp_Click(sender, e); e.Handled = true; }
    }
    private void OpenReadinessReason_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(this, "远控引擎在45秒内未就绪。\n" + (_status?.Remote.Message ?? "后台服务未连接"), "连接未就绪");
        OpenDiagnostics_Click(sender, e);
    }
    private void ShowDevicePermissionsDialog() {
        DevicePermissionsDialog.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            DevicePermissionsDialog.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))));
    }
    private void CloseDevicePermissions_Click(object sender, RoutedEventArgs e) {
        DevicePermissionsDialog.Visibility = Visibility.Collapsed;
        if(Pages.SelectedItem==FilesPage)FileDeviceBox.Focus();else HomeNavigation.Focus();
    }
    private void DevicePermissions_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    { if (e.Key == Key.Escape) { CloseDevicePermissions_Click(sender,e); e.Handled = true; } }
}
