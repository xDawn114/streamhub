using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using StreamHub.Host.Runtime;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace StreamHub.Host;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _tray;
    private MainWindow? _window;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationWait;
    private bool _ownsInstanceMutex;

    internal bool IsExiting { get; private set; }
    public ControlApiClient? ControlApi { get; private set; }
    internal void ShowSessionNotification(string title, string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => ShowSessionNotification(title, message))); return; }
        if (_tray is not null && !IsExiting) _tray.ShowBalloonTip(5000, title, message, Forms.ToolTipIcon.Info);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // This mode must precede the single-instance activation path. A quiet
        // install or an exited tray UI must not disable clipboard synchronization.
        if (e.Args.Length > 0 && e.Args[0] == "--clipboard-job")
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(7));
                var result = e.Args.Length == 2
                    ? await new ClipboardAgent(new RuntimeClient()).ExecuteAsync(e.Args[1], DesktopClipboard.Read, DesktopClipboard.Write, deadline.Token)
                    : 1;
                Shutdown(result);
            }
            catch { Shutdown(1); } // No modal window, clipboard payload, or error dump from a helper.
            return;
        }
        try
        {
            // Create the event first so an early second launch cannot lose its activation request.
            var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            var instanceKey = $@"Local\StreamHub.Host.{user}.{Process.GetCurrentProcess().SessionId}";
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, instanceKey + ".Activate");
            _instanceMutex = new Mutex(false, instanceKey + ".Instance");
            try { _ownsInstanceMutex = _instanceMutex.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsInstanceMutex = true; }

            if (!_ownsInstanceMutex)
            {
                AllowExistingInstanceToActivate();
                _activationEvent.Set();
                Shutdown();
                return;
            }

            ControlApi = new ControlApiClient(new RuntimeClient());
            try { await ControlApi.StartAsync(); }
            catch (InvalidOperationException) { /* The desktop remains usable; the binding panel displays LastError. */ }
            _window = new MainWindow();
            _tray = new Forms.NotifyIcon
            {
                Text = "我控制",
                Icon = LoadProductIcon(),
                Visible = true
            };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开我控制", null, (_, _) => ShowWindow());
            menu.Items.Add("刷新状态", null, (_, _) => _window.RefreshStatusFromTray());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("立即恢复本机屏幕", null, (_, _) => _window.RecoverPrivacyScreenFromTray());
            menu.Items.Add("停止所有远程服务…", null, (_, _) => _window.StopServicesFromTray());
            menu.Items.Add("退出界面（服务继续运行）", null, (_, _) => ShutdownFromTray());
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => ShowWindow();
            _activationWait = ThreadPool.RegisterWaitForSingleObject(_activationEvent, (_, _) =>
            {
                if (!Dispatcher.HasShutdownStarted)
                    Dispatcher.BeginInvoke(new Action(ShowWindow));
            }, null, Timeout.Infinite, executeOnlyOnce: false);

            // Only an explicit background launch should start without a visible window.
            if (!e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase)) ShowWindow();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"我控制启动失败：{ex.Message}", "我控制",
                MessageBoxButton.OK, MessageBoxImage.Error);
            IsExiting = true;
            Shutdown(1);
        }
    }

    private void ShowWindow()
    {
        if (_window is null || IsExiting) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.RefreshStatusFromTray();
    }

    private static void AllowExistingInstanceToActivate()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                try
                {
                    if (process.Id != current.Id && process.SessionId == current.SessionId)
                        AllowSetForegroundWindow((uint)process.Id);
                }
                catch (InvalidOperationException) { /* A concurrent launch may already have exited. */ }
            }
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    private void ShutdownFromTray()
    {
        IsExiting = true;
        Shutdown();
    }

    private static Drawing.Icon LoadProductIcon()
    {
        try { return Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? Drawing.SystemIcons.Application; }
        catch { return Drawing.SystemIcons.Application; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        _window?.DisposeWindow();
        _activationWait?.Unregister(null);
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _activationEvent?.Dispose();
        if (_ownsInstanceMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
