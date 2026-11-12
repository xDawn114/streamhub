using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StreamHub.Service;

/// <summary>Independent of the media gate. Identity-checked handles prevent PID reuse.</summary>
internal sealed class PrivacyScreenGuard : IDisposable
{
    public const int Recovered = 12;
    public const int PollMs = 100;
    public const int EngineExitBudgetMs = 1000;
    private readonly SafeProcessHandle _engine, _owner;
    private readonly int _timeoutMs;
    private readonly uint _session;
    private long _deadline;
    private bool _recovering;

    private PrivacyScreenGuard(SafeProcessHandle engine, SafeProcessHandle owner, int timeoutMs, long deadline, uint session) =>
        (_engine, _owner, _timeoutMs, _deadline, _session) = (engine, owner, timeoutMs, deadline, session);

    public static PrivacyScreenGuard Open(int enginePid, long engineCreated, int ownerPid, long ownerCreated,
        int timeoutMs, long initialDeadline, uint session, string expectedEnginePath)
    {
        if (timeoutMs is not (6000 or 12000 or 25000) || initialDeadline > Environment.TickCount64 + timeoutMs + 1000)
            throw new ArgumentException("隐私黑屏心跳期限无效。");
        var engine = OpenIdentity(enginePid, engineCreated, terminate: true);
        try
        {
            var name = new StringBuilder(32768); var length = name.Capacity;
            if (!QueryFullProcessImageName(engine, 0, name, ref length) ||
                !string.Equals(Path.GetFullPath(name.ToString()), Path.GetFullPath(expectedEnginePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("隐私黑屏只能保护本产品的主屏引擎。");
            return new(engine, OpenIdentity(ownerPid, ownerCreated, terminate: false), timeoutMs, initialDeadline, session);
        }
        catch { engine.Dispose(); throw; }
    }

    private static SafeProcessHandle OpenIdentity(int pid, long created, bool terminate)
    {
        if (pid <= 0 || pid == Environment.ProcessId) throw new ArgumentException("隐私保护进程标识无效。");
        var handle = OpenProcess(0x00101000u | (terminate ? 1u : 0u), false, pid); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        if (!GetProcessTimes(handle, out var actual, out _, out _, out _) || actual != created)
        { handle.Dispose(); throw new InvalidOperationException("隐私保护进程已被替换。"); }
        return handle;
    }

    public bool NeedsRecovery(bool heartbeat, uint activeSession)
    {
        var now = Environment.TickCount64;
        if (!_recovering && heartbeat && now < _deadline) _deadline = now + _timeoutMs;
        _recovering |= now >= _deadline || activeSession != _session ||
            WaitForSingleObject(_owner, 0) == 0 || WaitForSingleObject(_engine, 0) == 0;
        return _recovering;
    }

    /// <summary>Never uncover the desktop while the lost receiver can control it.</summary>
    public bool EndRemoteBeforeReveal()
    {
        _recovering = true;
        if (WaitForSingleObject(_engine, 0) == 0) return true;
        if (!TerminateProcess(_engine, 1)) return WaitForSingleObject(_engine, 0) == 0;
        return WaitForSingleObject(_engine, EngineExitBudgetMs) == 0;
    }

    public void Dispose() { _engine.Dispose(); _owner.Dispose(); }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder name, ref int length);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle process, int milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
}
