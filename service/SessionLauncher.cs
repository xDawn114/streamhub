using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StreamHub.Service;

internal sealed class OwnedProcess : IDisposable
{
    public Process Process { get; }
    public uint SessionId { get; }
    private readonly IntPtr _job;
    private readonly SafeProcessHandle? _processHandle;

    public OwnedProcess(Process process, uint sessionId, IntPtr job) => (Process, SessionId, _job) = (process, sessionId, job);
    public OwnedProcess(Process process, uint sessionId, IntPtr job, IntPtr processHandle) : this(process, sessionId, job) =>
        _processHandle = new SafeProcessHandle(processHandle, ownsHandle: true);
    public bool Running { get { try { return !Process.HasExited; } catch (InvalidOperationException) { return false; } } }
    public bool TryGetExitCode(out int exitCode)
    {
        exitCode = 0;
        try
        {
            var handle = _processHandle ?? Process.SafeHandle;
            if (handle.IsInvalid || !GetExitCodeProcess(handle, out var rawCode)) return false;
            exitCode = unchecked((int)rawCode);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ObjectDisposedException)
        { return false; }
    }
    public int ExitCode => TryGetExitCode(out var exitCode)
        ? exitCode
        : throw new InvalidOperationException("无法读取子进程退出码。");
    public void Dispose()
    {
        if (_job != IntPtr.Zero) SessionLauncher.CloseHandle(_job);
        Process.Dispose();
        _processHandle?.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}

internal static class SessionLauncher
{
    private const uint CREATE_SUSPENDED = 0x4, CREATE_NEW_CONSOLE = 0x10, CREATE_UNICODE_ENVIRONMENT = 0x400;
    private const uint STARTF_USESHOWWINDOW = 0x1;
    private const uint TOKEN_ALL_ACCESS = 0xf01ff;
    public static uint ActiveSession => WTSGetActiveConsoleSessionId();

    public static OwnedProcess Start(string executable, IEnumerable<string> arguments, string workingDirectory, bool interactive,
        string? ownerSid = null)
    {
        if (interactive)
        {
            if (ownerSid is not null && System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value != ownerSid)
                throw new InvalidOperationException("当前用户与授权的 Windows 用户不同。");
            var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            var process = Process.Start(start) ?? throw new InvalidOperationException("未能创建串流进程。");
            return new(process, (uint)process.SessionId, IntPtr.Zero);
        }

        var session = ActiveSession;
        if (session == uint.MaxValue) throw new InvalidOperationException("等待电脑进入登录会话。");
        // Request only the right needed to duplicate the service token. Sunshine uses the
        // same pattern; asking for TOKEN_ALL_ACCESS here fails under hardened service ACLs.
        IntPtr ownToken;
        if (ownerSid is null) {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0002, out ownToken)) ThrowWin32();
        } else {
            if (!WTSQueryUserToken(session, out ownToken)) ThrowWin32();
        }
        IntPtr primaryToken = IntPtr.Zero, environment = IntPtr.Zero, job = IntPtr.Zero;
        ProcessInformation info = default;
        try
        {
            // Engine helpers retain the service token. File/lock helpers explicitly
            // use the matching logged-in user's token and never inherit SYSTEM rights.
            if (ownerSid is not null) {
                using var identity = new System.Security.Principal.WindowsIdentity(ownToken);
                if (identity.User?.Value != ownerSid) throw new InvalidOperationException("当前登录用户与授权用户不同。");
            }
            if (!DuplicateTokenEx(ownToken, TOKEN_ALL_ACCESS, IntPtr.Zero, 2, 1, out primaryToken)) ThrowWin32();
            if (ownerSid is null && !SetTokenInformation(primaryToken, 12, ref session, sizeof(uint))) ThrowWin32();
            if (!CreateEnvironmentBlock(out environment, primaryToken, false)) ThrowWin32();
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) ThrowWin32();
            var limits = new JobObjectExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>())) ThrowWin32();
            // A private hidden console makes Ctrl+C graceful shutdown possible without
            // broadcasting to the service or another independently managed backend.
            var startup = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(), lpDesktop = @"winsta0\default",
                dwFlags = STARTF_USESHOWWINDOW, wShowWindow = 0 // SW_HIDE
            };
            var commandLine = new StringBuilder(Quote(executable) + " " + string.Join(" ", arguments.Select(Quote)));
            if (!CreateProcessAsUser(primaryToken, executable, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE, environment, workingDirectory, ref startup, out info)) ThrowWin32();
            if (!AssignProcessToJobObject(job, info.hProcess)) ThrowWin32();
            // Hold the original process handle until disposal so a fast exit
            // cannot lose its status before the supervisor observes it.
            var process = Process.GetProcessById((int)info.dwProcessId);
            try
            {
                if (ResumeThread(info.hThread) == uint.MaxValue) ThrowWin32();
                var result = new OwnedProcess(process, session, job, info.hProcess);
                info.hProcess = IntPtr.Zero;
                job = IntPtr.Zero;
                return result;
            }
            catch { process.Dispose(); throw; }
        }
        catch
        {
            if (info.hProcess != IntPtr.Zero) TerminateProcess(info.hProcess, 1);
            throw;
        }
        finally
        {
            if (info.hThread != IntPtr.Zero) CloseHandle(info.hThread);
            if (info.hProcess != IntPtr.Zero) CloseHandle(info.hProcess);
            if (job != IntPtr.Zero) CloseHandle(job);
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            CloseHandle(ownToken);
        }
    }

    public static OwnedProcess OwnLocalProcess(Process process)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) { process.Kill(entireProcessTree: true); ThrowWin32(); }
        try
        {
            var limits = new JobObjectExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = 0x2000;
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()) ||
                !AssignProcessToJobObject(job, process.Handle)) ThrowWin32();
            return new(process, (uint)process.SessionId, job);
        }
        catch { CloseHandle(job); try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
    }

    public static bool SignalCtrlC(int processId)
    {
        if (processId <= 0 || processId == Environment.ProcessId) return false;
        FreeConsole();
        if (!AttachConsole((uint)processId)) return false;
        try
        {
            if (!SetConsoleCtrlHandler(IntPtr.Zero, true)) return false;
            return GenerateConsoleCtrlEvent(0, 0);
        }
        finally { FreeConsole(); }
    }

    private static string Quote(string text)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in text)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') result.Append('\\', slashes * 2 + 1);
            else result.Append('\\', slashes);
            slashes = 0;
            result.Append(character);
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static void ThrowWin32() => throw new Win32Exception(Marshal.GetLastWin32Error());
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JobObjectExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation; public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr duplicate);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(IntPtr token, int kind, ref uint data, int length);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessAsUser(IntPtr token, string app, StringBuilder args, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int kind, ref JobObjectExtendedLimitInformation information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll")] private static extern bool GenerateConsoleCtrlEvent(uint signal, uint processGroupId);
}
