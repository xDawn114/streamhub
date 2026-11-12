using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using StreamHub.Host.Display;
using StreamHub.Host.Runtime;

namespace StreamHub.Service;

internal sealed record TabletDisplayNegotiation(TabletDisplayMode Mode, TabletDisplayMode[] SupportedModes);

/// <summary>Reads the console desktop's primary display without trusting Session 0 display defaults.</summary>
internal static class DisplayModeProbe
{
    private const string Prefix = "StreamHub.DisplayMode.";
    private const string TabletPrefix = "StreamHub.TabletMode.";

    public static bool IsValidPipeName(string name) => name.StartsWith(Prefix, StringComparison.Ordinal) &&
        name.Length == Prefix.Length + 32 && Guid.TryParseExact(name[Prefix.Length..], "N", out _);
    public static bool IsValidTabletPipeName(string name) => name.StartsWith(TabletPrefix, StringComparison.Ordinal) &&
        name.Length == TabletPrefix.Length + 32 && Guid.TryParseExact(name[TabletPrefix.Length..], "N", out _);

    public static async Task<HostDisplayMode> ReadAsync(string root, bool interactive, CancellationToken cancellationToken)
    {
        if (interactive) return HostDisplayMode.ReadPrimary();
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("未能找到主屏显示助手。");
        var pipeName = Prefix + Guid.NewGuid().ToString("N");
        // SessionLauncher retains the service's SYSTEM token when moving the
        // helper to winsta0\default, so CurrentUserOnly allows that helper only.
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var helper = SessionLauncher.Start(executable,
            ["--display-mode", pipeName, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)], root, false);
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientId) || clientId != (uint)helper.Process.Id)
                throw new InvalidOperationException("主屏显示助手身份校验失败。");
            var bytes = new byte[12];
            await pipe.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
            await helper.Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (helper.ExitCode != 0 || helper.SessionId != SessionLauncher.ActiveSession)
                throw new InvalidOperationException("电脑登录会话已切换，请重新连接远程控制。");
            var mode = new HostDisplayMode(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4)), BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4)));
            if (mode.Width is < 320 or > 32768 || mode.Height is < 200 or > 32768 || mode.RefreshRate is < 1 or > 1000)
                throw new InvalidOperationException("电脑主屏返回的显示模式无效。");
            return mode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidOperationException("读取电脑主屏超时，请确认电脑已登录且实体屏已打开。"); }
        finally
        {
            StopHelper(helper);
        }
    }

    public static async Task WriteAsync(string pipeName, int serverId)
    {
        if (!IsValidPipeName(pipeName) || serverId <= 0) throw new InvalidOperationException("显示助手参数无效。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actualServerId) || actualServerId != (uint)serverId)
            throw new InvalidOperationException("主屏显示服务身份校验失败。");
        var mode = HostDisplayMode.ReadPrimary();
        var bytes = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), mode.Width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), mode.Height);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), mode.RefreshRate);
        await pipe.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Negotiates and applies the tablet mode in the active console session, never Session 0.</summary>
    public static async Task<TabletDisplayNegotiation> ConfigureTabletAsync(string root, bool interactive,
        TabletDisplayRequest request, CancellationToken cancellationToken)
    {
        TabletModeSelection.Validate(request);
        if (interactive) return new(DisplayController.ConfigureDynamic(request), DisplayController.SupportedTabletModes(request.Orientation));
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("未能找到副屏显示助手。");
        var pipeName = TabletPrefix + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var helper = SessionLauncher.Start(executable,
            ["--tablet-display", pipeName, Number(Environment.ProcessId), Number(request.Width), Number(request.Height),
                Number(request.RefreshRate), Number(request.Orientation)], root, false);
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientId) || clientId != (uint)helper.Process.Id)
                throw new InvalidOperationException("副屏显示助手身份校验失败。");
            var bytes = new byte[28];
            await pipe.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
            int Read(int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
            var errorLength = Read(24);
            if (errorLength is < 0 or > 2048 || Read(20) is not (0 or 1) || Read(0) is not (0 or 1))
                throw new InvalidOperationException("副屏显示助手返回的数据无效。");
            var errorBytes = new byte[errorLength];
            await pipe.ReadExactlyAsync(errorBytes, timeout.Token).ConfigureAwait(false);
            var countBytes = new byte[4];
            await pipe.ReadExactlyAsync(countBytes, timeout.Token).ConfigureAwait(false);
            var count = BinaryPrimitives.ReadInt32LittleEndian(countBytes);
            if (count is < 0 or > 128) throw new InvalidOperationException("副屏模式数量无效。");
            var modeBytes = new byte[count * 16];
            await pipe.ReadExactlyAsync(modeBytes, timeout.Token).ConfigureAwait(false);
            var modes = new TabletDisplayMode[count];
            for (var i = 0; i < count; i++)
            {
                int Value(int offset) => BinaryPrimitives.ReadInt32LittleEndian(modeBytes.AsSpan(i * 16 + offset, 4));
                var mode = new TabletDisplayMode(Value(0), Value(4), Value(8), Value(12));
                TabletModeSelection.Validate(new(mode.Width, mode.Height, mode.RefreshRate, mode.Orientation));
                if (mode.Orientation != request.Orientation) throw new InvalidOperationException("副屏支持模式方向无效。");
                modes[i] = mode;
            }
            await helper.Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (helper.SessionId != SessionLauncher.ActiveSession)
                throw new InvalidOperationException("电脑登录会话已切换，请重新连接副屏。");
            if (Read(0) != 0 || helper.ExitCode != 0)
                throw new InvalidOperationException(errorLength == 0 ? "副屏模式配置失败，请重新连接。" : Encoding.UTF8.GetString(errorBytes));
            var actual = new TabletDisplayMode(Read(4), Read(8), Read(12), Read(16), Read(20) != 0);
            TabletModeSelection.Validate(new(actual.Width, actual.Height, actual.RefreshRate, actual.Orientation));
            if (actual.Orientation != request.Orientation || actual.Degraded !=
                (actual.Width != request.Width || actual.Height != request.Height || actual.RefreshRate != request.RefreshRate))
                throw new InvalidOperationException("副屏显示助手返回的协商结果不一致。");
            return new(actual, modes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidOperationException("副屏模式配置超时，请确认电脑已解锁且实体屏已打开。"); }
        finally { StopHelper(helper); }
    }

    public static async Task WriteTabletAsync(string pipeName, int serverId, TabletDisplayRequest request)
    {
        if (!IsValidTabletPipeName(pipeName) || serverId <= 0) throw new InvalidOperationException("副屏显示助手参数无效。");
        TabletDisplayMode? actual = null;
        TabletModeSelection.Validate(request);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(13));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actualServerId) || actualServerId != (uint)serverId)
            throw new InvalidOperationException("副屏显示服务身份校验失败。");
        string? error = null;
        try { actual = DisplayController.ConfigureDynamic(request); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
        { error = ex.Message; }
        var errorBytes = Encoding.UTF8.GetBytes((error ?? "")[..Math.Min(error?.Length ?? 0, 512)]);
        var modes = actual is null ? [] : DisplayController.SupportedTabletModes(request.Orientation);
        var bytes = new byte[28];
        void Write(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        Write(0, error is null ? 0 : 1); Write(4, actual?.Width ?? 0); Write(8, actual?.Height ?? 0);
        Write(12, actual?.RefreshRate ?? 0); Write(16, actual?.Orientation ?? 0); Write(20, actual?.Degraded == true ? 1 : 0);
        Write(24, errorBytes.Length);
        await pipe.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
        await pipe.WriteAsync(errorBytes, timeout.Token).ConfigureAwait(false);
        var modeBytes = new byte[4 + modes.Length * 16];
        BinaryPrimitives.WriteInt32LittleEndian(modeBytes.AsSpan(0, 4), modes.Length);
        for (var i = 0; i < modes.Length; i++)
        {
            void ModeValue(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(modeBytes.AsSpan(4 + i * 16 + offset, 4), value);
            ModeValue(0, modes[i].Width); ModeValue(4, modes[i].Height);
            ModeValue(8, modes[i].RefreshRate); ModeValue(12, modes[i].Orientation);
        }
        await pipe.WriteAsync(modeBytes, timeout.Token).ConfigureAwait(false);
        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
        if (error is not null) Environment.ExitCode = 1;
    }

    private static string Number(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static void StopHelper(OwnedProcess helper)
    {
        try { if (helper.Running) helper.Process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
