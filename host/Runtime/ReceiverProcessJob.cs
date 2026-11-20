using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StreamHub.Host.Runtime;

/// <summary>Receiver dies with its owning app, including crashes.</summary>
internal sealed class ReceiverProcessJob : IDisposable
{
    private IntPtr _handle;
    public ReceiverProcessJob(Process process)
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        var info = new ExtendedLimits(); info.Basic.LimitFlags = 0x2000;
        if (_handle == IntPtr.Zero || !SetInformationJobObject(_handle, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimits>()) ||
            !AssignProcessToJobObject(_handle, process.Handle))
        {
            int error = Marshal.GetLastWin32Error();
            if (!process.HasExited) process.Kill(true);
            Dispose(); throw new Win32Exception(error, "无法绑定临时接收程序的生命周期。");
        }
    }
    public void Dispose() { if (_handle != IntPtr.Zero) CloseHandle(_handle); _handle = IntPtr.Zero; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits {
        public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcesses; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int type, ref ExtendedLimits info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
