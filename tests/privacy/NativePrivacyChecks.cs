using System.Diagnostics;
using System.Runtime.InteropServices;
using StreamHub.Service;

internal static class NativePrivacyChecks
{
    public static void CheckHitTestAndRecovery(string helperExecutable)
    {
        var x = GetSystemMetrics(76) + 40;
        var y = GetSystemMetrics(77) + 40;
        var target = CreateWindowEx(0x00000008 | 0x00000080 | 0x08000000, "BUTTON", "", unchecked((int)0x80000000),
            x, y, 48, 48, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (target == IntPtr.Zero) throw new InvalidOperationException("无法创建隔离命中测试窗口。");
        try
        {
            if (!SetWindowPos(target, new IntPtr(-1), x, y, 48, 48, 0x0010 | 0x0040))
                throw new InvalidOperationException("无法显示隔离命中测试窗口。");
            var point = new Point { X = x + 24, Y = y + 24 };
            if (WindowFromPoint(point) != target)
                throw new InvalidOperationException("测试目标被其他窗口遮住，不能开始隐私遮罩检查。");
            using var lifetime = PrivacyScreenLifetime.Create();
            using var watchdog = new Timer(_ => { try { lifetime.RequestStop(); } catch (ObjectDisposedException) { } },
                null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
            var start = new ProcessStartInfo(helperExecutable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--privacy-screen");
            start.ArgumentList.Add(lifetime.Name);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动隔离的隐私助手。");
            try
            {
                var started = Stopwatch.StartNew();
                while (!lifetime.IsReady && !process.HasExited && started.Elapsed < TimeSpan.FromSeconds(2)) Thread.Sleep(10);
                if (!lifetime.IsReady || process.HasExited)
                    throw new InvalidOperationException("隔离隐私助手未就绪或提前退出。");
                var overlay = FindWindow("StreamHubPrivacyScreen_" + process.Id, null);
                if (overlay == IntPtr.Zero) throw new InvalidOperationException("无法找到已就绪的隐私遮罩。");
                if (WindowFromPoint(point) != target)
                    throw new InvalidOperationException("隐私遮罩阻挡了不同进程窗口的命中测试。");
                if (!PostMessage(overlay, 0x0312, new IntPtr(1), IntPtr.Zero))
                    throw new InvalidOperationException("无法向隔离助手投递恢复消息。");
                if (!process.WaitForExit(3000) || process.ExitCode != 9)
                    throw new InvalidOperationException("恢复消息未使隔离助手以主动恢复状态退出。");
                if (WindowFromPoint(point) != target)
                    throw new InvalidOperationException("隐私助手退出后测试窗口未恢复命中。");
                Console.WriteLine("Native cross-process hit test and simulated hotkey-message recovery passed.");
            }
            finally
            {
                if (!process.HasExited) { lifetime.RequestStop(); process.Kill(); process.WaitForExit(); }
            }
        }
        finally { DestroyWindow(target); }
    }

    public static void CheckHotkeyConflict(string helperExecutable)
    {
        const int hotkeyId = 14917;
        const uint controlAltNoRepeat = 0x0002 | 0x0001 | 0x4000;
        const uint f12 = 0x7B;
        if (!RegisterHotKey(IntPtr.Zero, hotkeyId, controlAltNoRepeat, f12))
            throw new InvalidOperationException("无法预占恢复热键，不能执行冲突测试：" + Marshal.GetLastWin32Error());
        try
        {
            using var lifetime = PrivacyScreenLifetime.Create();
            var start = new ProcessStartInfo(helperExecutable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--privacy-screen");
            start.ArgumentList.Add(lifetime.Name);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动隔离的隐私助手。");
            try
            {
                if (!process.WaitForExit(5000)) throw new InvalidOperationException("恢复热键冲突时助手没有及时退出。");
                if (process.ExitCode != 8 || lifetime.IsReady)
                    throw new InvalidOperationException($"热键冲突必须在显示黑屏前退出：code={process.ExitCode}, ready={lifetime.IsReady}。");
                Console.WriteLine("Native hotkey conflict rejected before the privacy window was shown.");
            }
            finally
            {
                if (!process.HasExited) { lifetime.RequestStop(); process.Kill(); process.WaitForExit(); }
            }
        }
        finally { UnregisterHotKey(IntPtr.Zero, hotkeyId); }
    }

    public static void CheckActualHotkey(string helperExecutable)
    {
        using var lifetime = PrivacyScreenLifetime.Create();
        using var watchdog = new Timer(_ => { try { lifetime.RequestStop(); } catch (ObjectDisposedException) { } },
            null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
        var start = new ProcessStartInfo(helperExecutable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add("--privacy-screen");
        start.ArgumentList.Add(lifetime.Name);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动隔离的隐私助手。");
        try
        {
            var started = Stopwatch.StartNew();
            while (!lifetime.IsReady && !process.HasExited && started.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(10);
            if (!lifetime.IsReady || process.HasExited)
                throw new InvalidOperationException(process.HasExited
                    ? $"隔离隐私助手未就绪：{process.ExitCode}，{process.StandardError.ReadToEnd()}"
                    : "隔离隐私助手启动超时。");

            var keys = new[] { Key(0x11, false), Key(0x12, false), Key(0x7B, false), Key(0x7B, true), Key(0x12, true), Key(0x11, true) };
            try
            {
                if (SendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>()) != keys.Length)
                    throw new InvalidOperationException("无法完整发送恢复热键：" + Marshal.GetLastWin32Error());
            }
            finally
            {
                var release = new[] { Key(0x7B, true), Key(0x12, true), Key(0x11, true) };
                SendInput((uint)release.Length, release, Marshal.SizeOf<Input>());
            }
            if (!process.WaitForExit(3000) || process.ExitCode != 9)
                throw new InvalidOperationException("真实热键未使隔离助手以主动恢复状态退出。");
            Console.WriteLine("Native Ctrl+Alt+F12 input and helper recovery passed.");
        }
        finally
        {
            if (!process.HasExited) { lifetime.RequestStop(); process.Kill(); process.WaitForExit(); }
        }
    }

    private static Input Key(ushort key, bool up) => new()
    {
        Type = 1,
        Data = new InputUnion { Keyboard = new KeyboardInput { Key = key, Flags = up ? 2u : 0u } }
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int style, string className, string title, int windowStyle, int x, int y,
        int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
