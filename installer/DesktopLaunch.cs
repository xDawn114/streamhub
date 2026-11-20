using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StreamHub.Installer;

internal static class DesktopLaunch
{
    internal static void OpenAssistance(string installRoot)
    {
        string executable = Path.Combine(Path.GetFullPath(installRoot), "我控制.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到已安装的程序。", executable);
        uint thread = GetWindowThreadProcessId(GetShellWindow(), out uint processId);
        if (thread == 0) throw new InvalidOperationException("请回到 Windows 桌面后重试。");
        using var shell = Process.GetProcessById((int)processId);
        using var current = Process.GetCurrentProcess();
        if (shell.SessionId != current.SessionId)
            throw new InvalidOperationException("请在安装时的桌面会话中打开。");
        using var process = OpenProcess(0x1000, false, processId);
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!OpenProcessToken(process, 0x000A, out var shellToken))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (shellToken)
        {
            // The installer is elevated; the application belongs to the desktop user.
            using var identity = new WindowsIdentity(shellToken.DangerousGetHandle());
            if (identity.User?.Value != InstallationSecurity.InteractiveOwnerSid() ||
                new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("请从桌面快捷方式打开我控制。");
            if (!DuplicateTokenEx(shellToken, 0x000B, IntPtr.Zero, 2, 1, out var primaryToken))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using (primaryToken)
            {
                if (!CreateEnvironmentBlock(out var environment, primaryToken, false))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    var startup = new StartupInfo {
                        Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default",
                        Flags = 1, ShowWindow = 1
                    };
                    if (!CreateProcessWithTokenW(primaryToken, 1, executable,
                        new StringBuilder('"' + executable + "\" --assistance"), 0x400,
                        environment, Path.GetDirectoryName(executable)!, ref startup, out var child))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    CloseHandle(child.Thread);
                    CloseHandle(child.Process);
                }
                finally { DestroyEnvironmentBlock(environment); }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, Width, Height, XChars, YChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes, int impersonation, int type, out SafeAccessTokenHandle duplicate);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, uint logonFlags,
        string application, StringBuilder command, uint flags, IntPtr environment, string directory,
        ref StartupInfo startup, out ProcessInformation process);
}
