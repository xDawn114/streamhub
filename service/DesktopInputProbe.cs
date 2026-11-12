using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using StreamHub.Host.Runtime;

namespace StreamHub.Service;

/// <summary>Bounded, process-authenticated helper; no credentials in arguments, files or logs.</summary>
internal static class DesktopInputProbe
{
    private const string Prefix = "StreamHub.DesktopInput.";
    private sealed record Operation(string Action, string? Password = null, bool Submit = false);
    private sealed record Answer(DesktopInputState Desktop, LoginInputResult Login);
    public static bool ValidPipe(string name) => name.StartsWith(Prefix, StringComparison.Ordinal) &&
        name.Length == Prefix.Length + 32 && Guid.TryParseExact(name[Prefix.Length..], "N", out _);

    internal static async Task<(DesktopInputState Desktop, LoginInputResult Login)> RequestAsync(
        string root, bool interactive, string ownerSid, string? password, bool submit, CancellationToken token)
    {
        // Do not enable secure-desktop injection from an unelevated interactive/debug host.
        if (interactive) return (new(), new("unavailable"));
        if (password is not null) DesktopInputPolicy.ValidatePassword(password);
        string pipeName = Prefix + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var helper = SessionLauncher.Start(Path.Combine(root, "service", "串流中心后台.exe"),
            ["--desktop-input", pipeName, Environment.ProcessId.ToString(), ownerSid], root, false);
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint client) || client != helper.Process.Id)
                throw new InvalidOperationException("桌面输入助手身份校验失败。");
            await WriteAsync(pipe, new Operation(password is null ? "status" : "fill", password, submit), timeout.Token);
            var answer = await ReadAsync<Answer>(pipe, timeout.Token);
            await helper.Process.WaitForExitAsync(timeout.Token);
            if (helper.ExitCode != 0 || helper.SessionId != SessionLauncher.ActiveSession)
                throw new InvalidOperationException("电脑登录会话已切换，请重新连接。");
            return (answer.Desktop, answer.Login);
        }
        finally { try { if (helper.Running) helper.Process.Kill(true); } catch (InvalidOperationException) { } }
    }

    internal static async Task RunHelperAsync(string pipeName, int serverId, string ownerSid)
    {
        if (!ValidPipe(pipeName) || serverId <= 0 || !WindowsIdentity.GetCurrent().IsSystem)
            throw new InvalidOperationException("桌面输入助手参数无效。");
        _ = new SecurityIdentifier(ownerSid);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint actual) || actual != serverId)
            throw new InvalidOperationException("桌面输入服务身份校验失败。");
        var operation = await ReadAsync<Operation>(pipe, timeout.Token);
        if (operation.Action is not ("status" or "fill")) throw new ArgumentException("桌面操作无效。");
        if (operation.Action == "fill") DesktopInputPolicy.ValidatePassword(operation.Password);
        Answer? answer = null;
        // A fresh STA may attach to the current input desktop before UI Automation creates windows.
        // Never call SwitchDesktop or change the user's foreground window.
        var thread = new Thread(() =>
        {
            try { answer = Inspect(ownerSid, operation); }
            catch { answer = new(new(), new("unavailable")); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(2))) throw new TimeoutException("读取登录界面超时。");
        await WriteAsync(pipe, answer ?? new(new(), new("unavailable")), timeout.Token);
    }

    private static Answer Inspect(string ownerSid, Operation operation)
    {
        SetThreadDpiAwarenessContext(new IntPtr(-4)); // Physical pixels, matching the captured primary output.
        var session = SessionLauncher.ActiveSession;
        if (!TryLocked(session, ownerSid, out bool locked)) return new(new(), new("unavailable"));
        IntPtr desktop = OpenInputDesktop(0, false, 0x83);
        if (desktop == IntPtr.Zero) return new(new(), new("unavailable"));
        try
        {
            if (!SetThreadDesktop(desktop)) return new(new(), new("unavailable"));
            var name = new StringBuilder(128);
            GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _);
            bool secure = string.Equals(name.ToString(), "winlogon", StringComparison.OrdinalIgnoreCase);
            if (!locked)
                return new(new(true, false, false, CaptionRegions()), new("unlocked"));
            bool focused = secure && PasswordFocused(ownerSid);
            var state = new DesktopInputState(true, true, focused, []);
            if (operation.Action != "fill") return new(state, new("status"));
            if (!focused) return new(state, new("not-ready"));
            // Confirm focus and the current owner again immediately before every input batch.
            // No clipboard, WM_SETTEXT, credential-provider bypass or account authentication API.
            if (!SafeKeys(ownerSid, [Key(0x11), Key(0x41), Key(0x41, 2), Key(0x11, 2), Key(0x08), Key(0x08, 2)]))
                return new(state, new("stopped"));
            foreach (char c in operation.Password!)
                if (!SafeKeys(ownerSid, [Key(c, 4, true), Key(c, 6, true)])) return new(state, new("stopped"));
            bool submitted = operation.Submit && SafeKeys(ownerSid, [Key(0x0D), Key(0x0D, 2)]);
            return new(state, new("filled", true, submitted));
        }
        finally { CloseDesktop(desktop); }
    }

    private static bool SafeKeys(string ownerSid, Input[] inputs) => PasswordFocused(ownerSid) &&
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;

    private static bool PasswordFocused(string ownerSid)
    {
        if (!TryLocked(SessionLauncher.ActiveSession, ownerSid, out bool locked) || !locked) return false;
        // Also verify the foreground belongs to Windows LogonUI, rather than a UAC prompt or app.
        IntPtr window = GetForegroundWindow(); GetWindowThreadProcessId(window, out uint foregroundId);
        if (!TrustedLogon(foregroundId)) return false;
        var automation = (IUIAutomation)new CUIAutomation();
        IUIAutomationElement? element = null;
        try
        {
            automation.GetFocusedElement(out element);
            if (element is null) return false;
            element.GetCurrentPropertyValue(30002, out object process);
            element.GetCurrentPropertyValue(30019, out object password);
            element.GetCurrentPropertyValue(30008, out object focus);
            element.GetCurrentPropertyValue(30005, out object name);
            return DesktopInputPolicy.CanFill(true, true, IsSecureDesktop(),
                Convert.ToUInt32(process) == foregroundId && TrustedLogon(foregroundId),
                password is true, focus is true, name as string);
        }
        finally
        {
            if (element is not null) Marshal.FinalReleaseComObject(element);
            Marshal.FinalReleaseComObject(automation);
        }
    }

    private static bool IsSecureDesktop()
    {
        IntPtr desktop=OpenInputDesktop(0,false,1);
        if(desktop==IntPtr.Zero)return false;
        try {
            var name = new StringBuilder(128);
            return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) &&
                string.Equals(name.ToString(), "winlogon", StringComparison.OrdinalIgnoreCase);
        } finally {CloseDesktop(desktop);}
    }
    private static bool TrustedLogon(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.SessionId == SessionLauncher.ActiveSession &&
                string.Equals(process.MainModule?.FileName, Path.Combine(Environment.SystemDirectory, "LogonUI.exe"), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException) { return false; }
    }

    private static bool TryLocked(uint session, string ownerSid, out bool locked)
    {
        locked = false;
        if (session == uint.MaxValue || !WTSQueryUserToken(session, out IntPtr token)) return false;
        try { using var identity = new WindowsIdentity(token); if (identity.User?.Value != ownerSid) return false; }
        finally { CloseHandle(token); }
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, 25, out IntPtr data, out uint bytes)) return false;
        try
        {
            // WTSINFOEX level 1: DWORD level, 8-byte-aligned union, session/state/flags.
            if (bytes < 20 || Marshal.ReadInt32(data) != 1 || (uint)Marshal.ReadInt32(data, 8) != session ||
                Marshal.ReadInt32(data, 12) != 0) return false;
            int flags = Marshal.ReadInt32(data, 16);
            if (flags is not (0 or 1)) return false;
            locked = flags == 0; return true; // Windows 10/11; the Windows 7 inverted flags are unsupported.
        }
        finally { WTSFreeMemory(data); }
    }

    private static DesktopHitRegion[] CaptionRegions()
    {
        IntPtr window = GetForegroundWindow();
        int width = GetSystemMetrics(0), height = GetSystemMetrics(1);
        if(width<=0 || height<=0)return [];
        // Ask the actual window first; never infer caption buttons from fixed offsets.
        var bar=new TitleBarInfo {Size=(uint)Marshal.SizeOf<TitleBarInfo>(),States=new uint[6],Rects=new Rect[6]};
        if(SendMessageTimeout(window,0x33F,IntPtr.Zero,ref bar,0x0002,40,out _)!=IntPtr.Zero) {
            var regions=new List<DesktopHitRegion>();
            foreach(int index in new[]{2,3,5}) {
                var b=bar.Rects[index];
                if((bar.States[index]&0x8001)==0 && b.Left>=0 && b.Top>=0 && b.Right<=width && b.Bottom<=height && b.Right>b.Left && b.Bottom>b.Top)
                    regions.Add(new((double)b.Left/width,(double)b.Top/height,(double)b.Right/width,(double)b.Bottom/height));
            }
            if(regions.Count>0)return regions.ToArray();
        }
        if (!GetWindowRect(window, out var rect) ||
            DwmGetWindowAttribute(window, 5, out var buttons, Marshal.SizeOf<Rect>()) != 0 ||
            buttons.Right <= buttons.Left || buttons.Bottom <= buttons.Top) return [];
        int left = rect.Left + buttons.Left, top = rect.Top + buttons.Top;
        int right = rect.Left + buttons.Right, bottom = rect.Top + buttons.Bottom;
        if (left < 0 || top < 0 || right > width || bottom > height || right <= left || bottom <= top) return [];
        return [new((double)left / width, (double)top / height, (double)right / width, (double)bottom / height)];
    }

    private static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, RuntimeProtocol.JsonOptions);
        try
        {
            await stream.WriteAsync(BitConverter.GetBytes(body.Length), token);
            await stream.WriteAsync(body, token); await stream.FlushAsync(token);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(body); }
    }
    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] size = new byte[4]; await stream.ReadExactlyAsync(size, token);
        int length = BitConverter.ToInt32(size); if (length is <= 0 or > 4096) throw new IOException("桌面助手数据无效。");
        byte[] body = new byte[length];
        try { await stream.ReadExactlyAsync(body, token); return JsonSerializer.Deserialize<T>(body, RuntimeProtocol.JsonOptions) ?? throw new IOException("桌面助手返回为空。"); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(body); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct TitleBarInfo {
        public uint Size;public Rect Title;
        [MarshalAs(UnmanagedType.ByValArray,SizeConst=6)] public uint[] States;
        [MarshalAs(UnmanagedType.ByValArray,SizeConst=6)] public Rect[] Rects;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    private static Input Key(int key, uint flags = 0, bool unicode = false) => new() { Type = 1, Data = new() {
        Keyboard = new() { Vk = unicode ? (ushort)0 : (ushort)key, Scan = unicode ? (ushort)key : (ushort)0, Flags = flags } } };
    [ComImport, Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E")] private class CUIAutomation { }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
    private interface IUIAutomation
    {
        void CompareElements(IntPtr a, IntPtr b, out int same);
        void CompareRuntimeIds(IntPtr a, IntPtr b, out int same);
        void GetRootElement(out IUIAutomationElement element);
        void ElementFromHandle(IntPtr window, out IUIAutomationElement element);
        void ElementFromPoint(long point, out IUIAutomationElement element);
        void GetFocusedElement(out IUIAutomationElement element);
    }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
    private interface IUIAutomationElement
    {
        void SetFocus(); void GetRuntimeId(out IntPtr id);
        void FindFirst(int scope, IntPtr condition, out IntPtr element);
        void FindAll(int scope, IntPtr condition, out IntPtr elements);
        void FindFirstBuildCache(int scope, IntPtr condition, IntPtr cache, out IntPtr element);
        void FindAllBuildCache(int scope, IntPtr condition, IntPtr cache, out IntPtr elements);
        void BuildUpdatedCache(IntPtr cache, out IntPtr element);
        void GetCurrentPropertyValue(int property, [MarshalAs(UnmanagedType.Struct)] out object value);
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint id);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint session, out IntPtr token);
    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)] private static extern bool WTSQuerySessionInformation(IntPtr server, uint session, int info, out IntPtr data, out uint bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr data);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int bytes, out int needed);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll",EntryPoint="SendMessageTimeoutW")] private static extern IntPtr SendMessageTimeout(IntPtr window,uint message,IntPtr wParam,ref TitleBarInfo bar,uint flags,uint timeout,out UIntPtr result);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect rect, int bytes);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
