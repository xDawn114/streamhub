using System.Text.Json;
using System.Windows;
using StreamHub.Host.Runtime;
using Wpf = System.Windows.Controls;

namespace StreamHub.Host;

public partial class MainWindow
{
    private Wpf.StackPanel? _hostPolicyPanel;
    private Wpf.CheckBox? _hostPolicyLock, _hostPolicyLogin, _hostPolicySubmit;
    private Wpf.PasswordBox? _hostPolicyPassword;
    private HostPolicySnapshot? _hostPolicySnapshot;
    private bool _renderingHostPolicy, _hostPolicyDirty, _savingHostPolicy;

    private async void HostPolicySettings_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is Wpf.ContentControl target && target.Content is null)
            target.Content = BuildHostPolicySettingsPanel();
        await ReadHostPolicySettingsAsync();
    }

    internal FrameworkElement BuildHostPolicySettingsPanel()
    {
        if (_hostPolicyPanel is not null) return _hostPolicyPanel;
        var panel = _hostPolicyPanel = new Wpf.StackPanel();
        Wpf.CheckBox Choice(string text, string description, string icon)
        {
            var choice = new Wpf.CheckBox { MinHeight = 40 };
            System.Windows.Automation.AutomationProperties.SetName(choice, text);
            choice.Click += (_, _) => { if (!_renderingHostPolicy) _hostPolicyDirty = true; };
            panel.Children.Add(new SettingRow { Header = text, ToolTip = description, Tag = icon, Content = choice }); return choice;
        }

        _hostPolicyLock = Choice("退出远控后锁屏", "主动退出时锁定；旋转和内部重连不触发", "\uE72E");

        _hostPolicyLogin = Choice("连接后自动填写密码", "仅使用本机加密保存的 Windows 密码", "\uE8D7");
        _hostPolicySubmit = Choice("填写后自动确认登录", "自动按 Enter 提交；仅限受信任设备", "\uE73E");
        panel.Children.Add(new Wpf.TextBlock { Text = "Windows 密码", Margin = new Thickness(0, 12, 0, 4) });
        _hostPolicyPassword = new Wpf.PasswordBox { MaxLength = 256, MinHeight = 40, Width = 320, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 12), ToolTip = "仅支持当前授权 Windows 用户的密码，不支持 PIN、Hello、切换用户或 UAC。密码在本机加密保存，不发送到手机或平板；不会回显已保存的密码。" };
        System.Windows.Automation.AutomationProperties.SetName(_hostPolicyPassword, "在电脑本机设置 Windows 密码");
        panel.Children.Add(_hostPolicyPassword);
        var buttons = new Wpf.WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        void Action(string text, Func<Task> action)
        {
            var button = new Wpf.Button { Content = text, MinHeight = 40, Margin = new Thickness(0, 0, 8, 8) };
            button.Click += async (_, _) => await action(); buttons.Children.Add(button);
        }
        Action("保存密码", SaveHostPasswordAsync); Action("删除保存的密码", ClearHostPasswordAsync);
        Action("保存策略", SaveHostPolicySettingsAsync);
        Action("重新读取", async () => { _hostPolicyDirty = false; await ReadHostPolicySettingsAsync(); });
        panel.Children.Add(buttons);
        if (_status?.HostPolicy is {} policy) RefreshHostPolicySettings(policy);
        return panel;
    }

    private void RefreshHostPolicySettings(HostPolicySnapshot? policy)
    {
        if (_hostPolicyPanel is null || policy is null || _hostPolicyDirty || _savingHostPolicy) return;
        _hostPolicySnapshot = policy; _renderingHostPolicy = true;
        try
        {
            _hostPolicyLock!.IsChecked = policy.LockOnExit;
            _hostPolicyLogin!.IsChecked = policy.AutomaticLoginEnabled;
            _hostPolicySubmit!.IsChecked = policy.AutomaticLoginSubmit;
            _hostPolicyLogin.IsEnabled = _hostPolicySubmit.IsEnabled = _hostPolicyPassword!.IsEnabled = policy.AutomaticLoginAvailable;
        }
        finally { _renderingHostPolicy = false; }
    }

    private async Task ReadHostPolicySettingsAsync()
    {
        try
        {
            var result = await _runtime.SendAsync(new("host-policy-status"), _closing.Token);
            if (result.Success && result.PayloadJson is not null)
                RefreshHostPolicySettings(JsonSerializer.Deserialize<HostPolicySnapshot>(result.PayloadJson, RuntimeProtocol.JsonOptions));
            else ShowFeedback(result.Message, error: true);
        }
        catch (OperationCanceledException) { }
        catch (JsonException) { ShowFeedback("电脑设置状态读取失败", error: true); }
    }

    private async Task SendHostPolicySettingsAsync(RuntimeRequest request)
    {
        if (_savingHostPolicy || _hostPolicySnapshot is null) return;
        bool preserveEdits = request.Command == "host-policy-password-save" && _hostPolicyDirty;
        _savingHostPolicy = true;
        if (_hostPolicyPanel is not null) _hostPolicyPanel.IsEnabled = false;
        try
        {
            var result = await _runtime.SendAsync(request with { PolicyRevision = _hostPolicySnapshot.Revision }, _closing.Token);
            if (result.Success && !preserveEdits) _hostPolicyDirty = false;
            _savingHostPolicy = false;
            if (result.PayloadJson is not null)
            {
                var policy = JsonSerializer.Deserialize<HostPolicySnapshot>(result.PayloadJson, RuntimeProtocol.JsonOptions);
                if (preserveEdits && result.Success) _hostPolicySnapshot = policy;
                else RefreshHostPolicySettings(policy);
            }
            ShowFeedback(result.Message, error: !result.Success);
        }
        catch (OperationCanceledException) { }
        catch (JsonException) { ShowFeedback("未能确认保存结果，请重新读取", error: true); }
        finally { _savingHostPolicy = false; if (_hostPolicyPanel is not null) _hostPolicyPanel.IsEnabled = true; }
    }

    private async Task SaveHostPolicySettingsAsync()
    {
        if (_hostPolicyLogin?.IsChecked == true && _hostPolicySnapshot?.AutomaticLoginEnabled != true &&
            System.Windows.MessageBox.Show(this, "允许已绑定的控制端在连接锁屏时，使用这台电脑保存的密码自动填写 Windows 密码框？请仅授权你信任的设备。",
                "开启自动填写？", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await SendHostPolicySettingsAsync(new("host-policy-save", PrivacyEnabled: null,
            LockOnExit: _hostPolicyLock?.IsChecked, AutomaticLoginEnabled: _hostPolicyLogin?.IsChecked,
            LoginSubmit: _hostPolicySubmit?.IsChecked));
    }

    private async Task SaveHostPasswordAsync()
    {
        if (_hostPolicyPassword is null) return;
        string password = _hostPolicyPassword.Password; _hostPolicyPassword.Clear();
        try { DesktopInputPolicy.ValidatePassword(password); }
        catch (ArgumentException error) { ShowFeedback(error.Message, error: true); return; }
        await SendHostPolicySettingsAsync(new("host-policy-password-save", LoginSecret: password));
    }

    private async Task ClearHostPasswordAsync()
    {
        _hostPolicyPassword?.Clear();
        if (System.Windows.MessageBox.Show(this, "删除这台电脑保存的 Windows 密码，并关闭自动填写？",
            "删除保存的密码", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await SendHostPolicySettingsAsync(new("host-policy-password-clear"));
    }
}
