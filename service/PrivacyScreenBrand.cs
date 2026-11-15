using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace StreamHub.Service;

// Paints inside the existing capture-excluded, input-transparent window.
// No additional window, focus, input handler or lifetime is introduced.
internal static class PrivacyScreenBrand
{
    internal const string Title = "我控制 | iControl";
    internal const string Message = "本机已开启黑屏防护，当前正在被远程控制。";
    internal const string Recovery = "按 Ctrl+Alt+F12 立即恢复本机屏幕";
    private static readonly Lazy<IntPtr> Icon = new(() =>
    {
        try { return LoadIcon(); }
        catch (Exception ex) when (ex is IOException or ArgumentException or ExternalException) { return IntPtr.Zero; }
    });
    private const uint Center = 1, VerticalCenter = 4, SingleLine = 32, NoPrefix = 2048;

    internal static void Paint(IntPtr dc)
    {
        var originX = GetSystemMetrics(76);
        var originY = GetSystemMetrics(77);
        var areas = new List<NativeRect>();
        MonitorCallback callback = (IntPtr monitor, IntPtr monitorDc, ref NativeRect bounds, IntPtr data) =>
        {
            areas.Add(new() { Left = bounds.Left - originX, Top = bounds.Top - originY,
                Right = bounds.Right - originX, Bottom = bounds.Bottom - originY });
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        if (areas.Count == 0) areas.Add(new() { Right = GetSystemMetrics(78), Bottom = GetSystemMetrics(79) });
        foreach (var area in areas) Draw(dc, area);
        GC.KeepAlive(callback);
    }

    private static void Draw(IntPtr dc, NativeRect area)
    {
        int width = area.Right - area.Left, height = area.Bottom - area.Top;
        if (width < 240 || height < 180) return;
        int saved = SaveDC(dc);
        IntPtr titleFont = IntPtr.Zero, bodyFont = IntPtr.Zero, hintFont = IntPtr.Zero;
        try
        {
            IntersectClipRect(dc, area.Left, area.Top, area.Right, area.Bottom);
            // Bound the content by the physical monitor, including portrait and small outputs.
            double scale = Math.Clamp(Math.Min(width / 1280d, height / 720d), .75, 2);
            int size = (int)(64 * scale), gap = (int)(16 * scale);
            int center = area.Left + width / 2, top = area.Top + height / 2 - (int)(76 * scale);
            titleFont = Font((int)(32 * scale), 600);
            bodyFont = Font((int)(18 * scale), 400);
            hintFont = Font((int)(13 * scale), 400);
            SetBkMode(dc, 1);
            SelectObject(dc, titleFont);
            GetTextExtentPoint32(dc, Title, Title.Length, out var titleSize);
            int titleWidth = Math.Min(titleSize.X, width - size - gap - 32);
            bool hasIcon = Icon.Value != IntPtr.Zero;
            int total = titleWidth + (hasIcon ? size + gap : 0), left = center - total / 2;
            if (hasIcon) DrawIconEx(dc, left, top, Icon.Value, size, size, 0, IntPtr.Zero, 3);
            var title = new NativeRect { Left = left + (hasIcon ? size + gap : 0), Top = top,
                Right = left + total, Bottom = top + size };
            SetTextColor(dc, 0x00FFFFFF);
            DrawText(dc, Title, -1, ref title, Center | VerticalCenter | SingleLine | NoPrefix);
            SelectObject(dc, bodyFont);
            var body = new NativeRect { Left = area.Left + 16, Right = area.Right - 16,
                Top = top + size + (int)(26 * scale), Bottom = top + size + (int)(66 * scale) };
            DrawText(dc, Message, -1, ref body, Center | 16 /* WORDBREAK */ | NoPrefix);
            SelectObject(dc, hintFont);
            SetTextColor(dc, 0x00B0B0B0);
            var hint = new NativeRect { Left = area.Left + 16, Right = area.Right - 16,
                Top = body.Bottom + (int)(10 * scale), Bottom = body.Bottom + (int)(42 * scale) };
            DrawText(dc, Recovery, -1, ref hint, Center | VerticalCenter | SingleLine | NoPrefix);
        }
        finally
        {
            RestoreDC(dc, saved);
            if (titleFont != IntPtr.Zero) DeleteObject(titleFont);
            if (bodyFont != IntPtr.Zero) DeleteObject(bodyFont);
            if (hintFont != IntPtr.Zero) DeleteObject(hintFont);
        }
    }

    private static IntPtr Font(int pixels, int weight) => CreateFont(-pixels, 0, 0, 0, weight,
        0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");

    private static IntPtr LoadIcon()
    {
        using var stream = typeof(PrivacyScreenBrand).Assembly.GetManifestResourceStream("StreamHub.BrandIcon.ico");
        if (stream is null || stream.Length > 1_048_576) return IntPtr.Zero;
        using var memory = new MemoryStream(); stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        if (bytes.Length < 22 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x00010000) return IntPtr.Zero;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        if (count is < 1 or > 64 || bytes.Length < 6 + count * 16) return IntPtr.Zero;
        int best = -1, bestWidth = 0;
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + i * 16, width = bytes[entry] == 0 ? 256 : bytes[entry];
            if (width > bestWidth) { best = entry; bestWidth = width; }
        }
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(best + 8));
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(best + 12));
        if (length == 0 || length > bytes.Length || offset > bytes.Length - length) return IntPtr.Zero;
        var data = Marshal.AllocHGlobal((int)length);
        try
        {
            Marshal.Copy(bytes, (int)offset, data, (int)length);
            return CreateIconFromResourceEx(data, length, true, 0x00030000, 128, 128, 0);
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int X, Y; }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref NativeRect bounds, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawText(IntPtr dc, string text, int length, ref NativeRect area, uint flags);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconFromResourceEx(IntPtr bytes, uint length, bool icon, uint version, int width, int height, uint flags);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32(IntPtr dc, string text, int length, out NativeSize size);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll")] private static extern int SaveDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool RestoreDC(IntPtr dc, int saved);
    [DllImport("gdi32.dll")] private static extern int IntersectClipRect(IntPtr dc, int left, int top, int right, int bottom);
}
