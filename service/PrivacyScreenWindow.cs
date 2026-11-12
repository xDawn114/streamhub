using System.Runtime.InteropServices;

namespace StreamHub.Service;

/// <summary>
/// Runs in the active user's desktop session. The overlay is excluded from
/// supported Windows capture APIs, so the local display is covered while the
/// remote receiver continues to see the underlying desktop.
/// </summary>
internal static class PrivacyScreenWindow
{
    public const int HotkeyUnavailable = 8;
    public const int EmergencyRecovery = 9;
    private const int WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint WM_DISPLAYCHANGE = 0x007E;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_HOTKEY = 0x0312;
    private const int RecoveryHotkeyId = 1;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_F12 = 0x7B;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint QS_ALLINPUT = 0x04FF;
    private const uint PM_REMOVE = 0x0001;
    private const int HTTRANSPARENT = -1;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int CS_HREDRAW = 0x0002;
    private const int CS_VREDRAW = 0x0001;
    // Stock-object indexes are not brush handles. Passing 4 directly to
    // FillRect selects a system color instead of the stock black brush.
    private static readonly IntPtr BLACK_BRUSH = GetStockObject(4);
    private static readonly WndProc WindowProcedure = WindowProc;

    public static int Run(string stopEventName, PrivacyScreenGuard? guard = null)
    {
        if (!IsSupportedWindowsBuild()) return 2;
        using var stop = PrivacyScreenLifetime.Open(stopEventName);
        using var ready = PrivacyScreenLifetime.OpenReady(stopEventName);
        using var heartbeat = PrivacyScreenLifetime.OpenHeartbeat(stopEventName);
        if (guard?.NeedsRecovery(false, SessionLauncher.ActiveSession) == true)
        {
            if (guard.EndRemoteBeforeReveal()) return PrivacyScreenGuard.Recovered;
            return 13; // No overlay has been shown; report startup failure.
        }

        var className = "StreamHubPrivacyScreen_" + Environment.ProcessId;
        var module = GetModuleHandle(null);
        var windowClass = new WndClass
        {
            Size = (uint)Marshal.SizeOf<WndClass>(),
            Style = CS_HREDRAW | CS_VREDRAW,
            Procedure = WindowProcedure,
            Instance = module,
            Cursor = LoadCursor(IntPtr.Zero,  IDC_ARROW),
            Background = BLACK_BRUSH,
            ClassName = className
        };
        if (RegisterClassEx(ref windowClass) == 0) return 3;

        var hwnd = IntPtr.Zero;
        var hotkeyRegistered = false;
        try
        {
            var bounds = VirtualScreenBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0) return 4;
            hwnd = CreateWindowEx(
                WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                className, "", WS_POPUP, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) return 5;
            // Transparent layered windows pass mouse input to windows in other
            // processes; HTTRANSPARENT alone only covers the same thread.
            if (!SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA)) return 10;
            if (!RegisterHotKey(hwnd, RecoveryHotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_F12))
                return HotkeyUnavailable;
            hotkeyRegistered = true;
            // Failing closed is deliberate. On older Windows builds the same
            // API can fall back to black capture, which would hide the desktop
            // from the remote user while claiming privacy mode is active.
            if (!SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)) return 6;
            if (stop.WaitOne(0)) return 0;
            if (!SetWindowPos(hwnd, HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW)) return 11;
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            UpdateWindow(hwnd);
            ready.Set();

            var stopHandles = new[] { stop.SafeWaitHandle.DangerousGetHandle(), heartbeat.SafeWaitHandle.DangerousGetHandle() };
            while (true)
            {
                // A service in Session 0 cannot close this tool window through
                // Process.MainWindowHandle. Wake the desktop message loop with
                // an event shared across sessions, then destroy it in finally.
                var result = MsgWaitForMultipleObjects(2, stopHandles, false, guard is null ? uint.MaxValue : PrivacyScreenGuard.PollMs, QS_ALLINPUT);
                if (result == 0) return 0;
                if (guard?.NeedsRecovery(result == 1, SessionLauncher.ActiveSession) == true && guard.EndRemoteBeforeReveal())
                    return PrivacyScreenGuard.Recovered;
                if (result is 1 or 258) continue;
                if (result != 2) return 7;
                while (PeekMessage(out var message, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    if (message.Value == WM_QUIT) return 0;
                    if (message.Value == WM_HOTKEY && message.WParam == new IntPtr(RecoveryHotkeyId))
                        return EmergencyRecovery;
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            }
        }
        finally
        {
            if (hotkeyRegistered) UnregisterHotKey(hwnd, RecoveryHotkeyId);
            if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
            UnregisterClass(className, module);
        }
    }

    private static bool IsSupportedWindowsBuild() => Environment.OSVersion.Platform == PlatformID.Win32NT &&
        Environment.OSVersion.Version.Build >= 19041;

    private static Rect VirtualScreenBounds() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WM_NCHITTEST: return new IntPtr(HTTRANSPARENT);
            case WM_ERASEBKGND: return new IntPtr(1);
            case WM_DISPLAYCHANGE:
                var bounds = VirtualScreenBounds();
                SetWindowPos(hwnd, HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                return IntPtr.Zero;
            case WM_PAINT:
                if (BeginPaint(hwnd, out var paint) != IntPtr.Zero)
                {
                    FillRect(paint.Dc, ref paint.Area, BLACK_BRUSH);
                    EndPaint(hwnd, ref paint);
                }
                return IntPtr.Zero;
            case WM_DESTROY: PostQuitMessage(0); return IntPtr.Zero;
            default: return DefWindowProc(hwnd, message, wParam, lParam);
        }
    }

    private readonly record struct Rect(int X, int Y, int Width, int Height);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Size;
        public uint Style;
        public WndProc? Procedure;
        public int ClsExtra;
        public int WndExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string? ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Message
    {
        public IntPtr Hwnd;
        public uint Value;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct PaintStruct
    {
        public IntPtr Dc;
        public int Erase;
        public RectNative Area;
        public int Restore;
        public int IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[]? Reserved;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RectNative { public int Left, Top, Right, Bottom; }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint IDC_ARROW = 32512;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WndClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(int extendedStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjects(uint count,
        IntPtr[] handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds, uint wakeMask);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr window, out PaintStruct paint);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr window, ref PaintStruct paint);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RectNative area, IntPtr brush);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(IntPtr window, int affinity);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr LoadCursor(IntPtr instance, uint cursor);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
}
