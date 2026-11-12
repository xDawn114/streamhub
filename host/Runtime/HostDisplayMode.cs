using System.Runtime.InteropServices;

namespace StreamHub.Host.Runtime;

/// <summary>
/// Reads the physical mode selected by Windows instead of the DPI-scaled WPF bounds.
/// This overlay is used by the prepare response so the receiver does not request a
/// larger stream merely because the tablet has a higher pixel count.
/// </summary>
public readonly record struct HostDisplayMode(int Width, int Height, int RefreshRate)
{
    private const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DevMode devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields;
        public int PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public int ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2,
            PanningWidth, PanningHeight;
    }

    public static HostDisplayMode ReadPrimary()
    {
        var mode = new DevMode { DeviceName = string.Empty, FormName = string.Empty, Size = (short)Marshal.SizeOf<DevMode>() };
        if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref mode) || mode.PelsWidth <= 0 || mode.PelsHeight <= 0)
            throw new InvalidOperationException("无法读取 Windows 主屏的物理显示模式。");
        return new HostDisplayMode(mode.PelsWidth, mode.PelsHeight, Math.Max(1, mode.DisplayFrequency));
    }
}
