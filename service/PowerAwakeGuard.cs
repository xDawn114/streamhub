using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StreamHub.Host.Runtime;

namespace StreamHub.Service;

// A process-owned request, rather than a change to the user's power plan.
// Closing the handle (including process termination) removes the request.
internal sealed class PowerAwakeGuard : IDisposable
{
    private readonly Func<IDisposable?> _acquire;
    private readonly Func<bool> _onAcPower;
    private IDisposable? _request;
    private readonly object _gate = new();
    private bool _disposed;
    public bool Active { get { lock (_gate) return _request is not null; } }
    public bool Failed { get; private set; }

    public PowerAwakeGuard() : this(SystemAwakeRequest.TryCreate, SystemAwakeRequest.OnAcPower) { }
    internal PowerAwakeGuard(Func<IDisposable?> acquire, Func<bool> onAcPower)
        => (_acquire, _onAcPower) = (acquire, onAcPower);

    public void Update(string mode, bool streaming)
    {
        lock (_gate)
        {
            if (_disposed) return;
            bool needed = PowerAwakePolicy.ShouldPreventSleep(mode, streaming,
                mode == PowerAwakePolicy.PluggedIn && _onAcPower());
            if (needed)
            {
                _request ??= _acquire();
                Failed = _request is null;
            }
            else
            {
                _request?.Dispose();
                _request = null;
                Failed = false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _request?.Dispose();
            _request = null;
        }
    }
}

internal sealed class SystemAwakeRequest(SafeFileHandle handle) : IDisposable
{
    private const uint SimpleString = 1;
    private const int SystemRequired = 1;
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct DetailedReason { public IntPtr Module; public uint Id, Count; public IntPtr Strings; }
    [StructLayout(LayoutKind.Explicit)]
    private struct ReasonUnion
    {
        [FieldOffset(0)] public IntPtr Simple;
        [FieldOffset(0)] public DetailedReason Detailed;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext { public uint Version, Flags; public ReasonUnion Reason; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(SafeFileHandle handle, int requestType);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(SafeFileHandle handle, int requestType);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);

    internal static bool OnAcPower() => GetSystemPowerStatus(out var status) && status.AcLineStatus == 1;
    internal static IDisposable? TryCreate()
    {
        var reason = Marshal.StringToHGlobalUni("iControl：保持远程连接可用");
        try
        {
            var context = new ReasonContext { Flags = SimpleString, Reason = new ReasonUnion { Simple = reason } };
            var request = PowerCreateRequest(ref context);
            if (!request.IsInvalid && PowerSetRequest(request, SystemRequired)) return new SystemAwakeRequest(request);
            request.Dispose();
            return null;
        }
        finally { Marshal.FreeHGlobal(reason); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PowerClearRequest(handle, SystemRequired);
        handle.Dispose();
    }
}
