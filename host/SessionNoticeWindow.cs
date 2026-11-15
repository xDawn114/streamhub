using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using StreamHub.Host.Runtime;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Control = System.Windows.Controls.Control;
using SizeToContent = System.Windows.SizeToContent;

namespace StreamHub.Host;

/// <summary>Service-owned helper, independent of the main window/tray lifetime.</summary>
internal sealed class SessionNoticeWindow : Window
{
    private readonly RuntimeClient _runtime;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly TextBlock _identity = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, FontSize = 13 };
    private readonly Button _disconnect = new() { Content = "断开连接", MinHeight = 32, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(12, 0, 0, 0) };
    private bool _refreshing;
    private bool _initialized;
    private ControllerSession? _controller;
    public SessionNoticeWindow(RuntimeClient? runtime = null)
    {
        _runtime = runtime ?? new();
        Title = "我控制 · 正在被远程控制";
        Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        var row = new Grid(); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = "我控制 | iControl · 正在被远程控制", FontSize = 13, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        text.Children.Add(_identity); row.Children.Add(text);
        _disconnect.FontSize = 13;
        _disconnect.Background = new SolidColorBrush(Color.FromRgb(43, 101, 220));
        _disconnect.Foreground = Brushes.White;
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.Name = "Frame"; frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        frame.SetValue(Border.PaddingProperty, new Thickness(12, 6, 12, 6));
        frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var label = new FrameworkElementFactory(typeof(ContentPresenter));
        label.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        label.SetValue(FrameworkElement.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        frame.AppendChild(label);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = frame };
        var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.White, "Frame"));
        focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "Frame"));
        template.Triggers.Add(focus); _disconnect.Template = template;
        Grid.SetColumn(_disconnect, 1); row.Children.Add(_disconnect);
        Content = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 12, 16, 12),
            Background = new SolidColorBrush(Color.FromRgb(26, 33, 46)), BorderBrush = new SolidColorBrush(Color.FromRgb(76, 137, 248)), BorderThickness = new Thickness(1), Child = row };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            long style = GetWindowLongPtr(handle, -20).ToInt64();
            SetWindowLongPtr(handle, -20, new IntPtr(style | 0x08000000L | 0x80L)); // NOACTIVATE | TOOLWINDOW
        };
        Loaded += async (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            Left = Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - Width - 20);
            Top = SystemParameters.WorkArea.Top + 16;
            _hideTimer.Start();
            _timer.Start(); await RefreshAsync();
        };
        // Keep the helper alive while hidden so the service does not recreate a
        // new banner on every reconciliation tick during the same connection.
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Hide(); };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _disconnect.Click += async (_, _) =>
        {
            _disconnect.IsEnabled = false;
            var result = await _runtime.SendAsync(new("session-disconnect"));
            if (result.Success) Close();
            else { _identity.Text = result.Message; _disconnect.IsEnabled = true; }
        };
        Closed += (_, _) => { _timer.Stop(); _hideTimer.Stop(); System.Windows.Application.Current.Shutdown(); };
    }
    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var response = await _runtime.GetStatusAsync();
            if (!response.Success) { _identity.Text = "连接状态暂不可读取；请从托盘检查并断开。"; return; }
            if (response.Status is not { } status || !status.Remote.Streaming && !status.Tablet.Streaming) { Close(); return; }
            _identity.Text = Describe(status.Controller);
            if (_controller != status.Controller)
            {
                _controller = status.Controller;
                if (!IsVisible) Show();
                _hideTimer.Stop(); _hideTimer.Start();
            }
        }
        catch { _identity.Text = "连接状态暂不可读取；请从托盘检查并断开。"; }
        finally { _refreshing = false; }
    }
    internal static string Describe(ControllerSession? session) => session is null
        ? "旧版媒体连接 · 设备身份待核对"
        : session.IndividuallyAuthenticated ? session.Name + " · " + session.Channel + " · 独立设备授权"
        : "旧版共享绑定 · " + session.Channel + " · 无法区分具体设备";
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
}
