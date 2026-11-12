using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace StreamHub.Installer;

internal static class DriverInstaller
{
    // Register one root-enumerated device, then ask Windows to install the unchanged
    // vendor-signed package. This avoids redistributing a separate devcon tool.
    internal static bool Install(string inf)
    {
        var displayClass = new Guid("4D36E968-E325-11CE-BFC1-08002BE10318");
        var set = SetupDiCreateDeviceInfoList(ref displayClass, IntPtr.Zero);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建副屏设备列表。");
        var device = new DeviceInfoData { Size = (uint)Marshal.SizeOf<DeviceInfoData>() };
        var registered = false;
        try
        {
            if (!SetupDiCreateDeviceInfo(set, "MTTVDD", ref displayClass, "Virtual Display Driver", IntPtr.Zero, 1, ref device))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建副屏设备。");
            var hardwareId = Encoding.Unicode.GetBytes("Root\\MttVDD\0\0");
            if (!SetupDiSetDeviceRegistryProperty(set, ref device, 1, hardwareId, (uint)hardwareId.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法登记副屏硬件标识。");
            if (!SetupDiCallClassInstaller(0x19, set, ref device))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法登记副屏设备。");
            registered = true;
            if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, "Root\\MttVDD", Path.GetFullPath(inf), 1, out var reboot))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 未完成副屏驱动安装。");
            return reboot;
        }
        catch
        {
            if (registered) SetupDiCallClassInstaller(0x05, set, ref device);
            throw;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DeviceInstance;
        public UIntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr window);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiCreateDeviceInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiCreateDeviceInfo(IntPtr set, string name, ref Guid classGuid, string description, IntPtr window, uint flags, ref DeviceInfoData device);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiSetDeviceRegistryPropertyW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref DeviceInfoData device, uint property, byte[] buffer, uint size);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref DeviceInfoData device);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "UpdateDriverForPlugAndPlayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr window, string hardwareId, string inf, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool reboot);
}
