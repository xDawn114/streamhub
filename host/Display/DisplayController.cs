using System.Runtime.InteropServices;
using StreamHub.Host.Runtime;

namespace StreamHub.Host.Display;

/// <summary>Operates only on a dedicated extended MTT1337 display in this interactive session.</summary>
public static class DisplayController
{
    private const uint Attached = 1, Primary = 4, Mirroring = 8;
    private const uint SdcUseSuppliedDisplayConfig = 0x20, SdcValidate = 0x40, SdcApply = 0x80;
    private const uint SdcSaveToDatabase = 0x200;
    private const uint ModeFields = 0x20 | 0x80 | 0x40000 | 0x80000 | 0x100000 | 0x200000 | 0x400000;
    private static readonly object Gate = new();
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Device
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Mode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public ushort Spec, Version, Size, Extra;
        public uint Fields;
        public int X, Y;
        public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint Bits, Width, Height, Flags, Frequency;
        public uint IcmMethod, IcmIntent, Media, Dither, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterId { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rational { public uint Numerator, Denominator; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ConfigSource { public AdapterId Adapter; public uint Id, ModeIndex, Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ConfigTarget
    {
        public AdapterId Adapter; public uint Id, ModeIndex; public int Technology; public uint Rotation, Scaling;
        public Rational Refresh; public uint ScanLine; public int Available; public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ConfigPath { public ConfigSource Source; public ConfigTarget Target; public uint Flags; }
    // No virtual-mode-aware query flag is used: ModeIndex is a full index, never
    // the clone-group/16-bit-source-index union used by virtual-aware topology.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct ConfigMode
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public AdapterId Adapter;
        [FieldOffset(16)] public ulong Raw0;
        [FieldOffset(24)] public ulong Raw1;
        [FieldOffset(32)] public ulong Raw2;
        [FieldOffset(40)] public ulong Raw3;
        [FieldOffset(48)] public ulong Raw4;
        [FieldOffset(56)] public ulong Raw5;
        [FieldOffset(16)] public uint Width;
        [FieldOffset(20)] public uint Height;
        [FieldOffset(24)] public uint PixelFormat;
        [FieldOffset(28)] public int X;
        [FieldOffset(32)] public int Y;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ConfigHeader { public uint Type, Size; public AdapterId Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ConfigTargetName
    {
        public ConfigHeader Header; public uint Flags; public int Technology; public ushort Manufacturer, Product; public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string MonitorPath;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ConfigSourceName
    {
        public ConfigHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    }
    private sealed record ConfigSnapshot(ConfigPath[] Paths, ConfigMode[] Modes);
    private sealed record ActivationPlan(ConfigSnapshot Original, ConfigPath[] Paths, ConfigMode[] Modes);
    public sealed record Snapshot(string Source, uint Width, uint Height, uint Frequency, uint Orientation, int X, int Y, uint BitsPerPixel, uint DisplayFlags, uint FixedOutput);
    public sealed record DisplayState(string Source, string MonitorId, bool Active, Snapshot Primary, Snapshot? Tablet);
    private sealed record DisplaySource(Device Device, Device[] Monitors);
    private sealed record DisplayTarget(DisplaySource Tablet, DisplaySource Primary, DisplaySource[] Sources);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? name, uint index, ref Device device, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsEx(string name, int index, ref Mode mode, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string name, ref Mode mode, IntPtr hwnd, uint flags, IntPtr param);
    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] ConfigPath[] paths, ref uint modeCount, [Out] ConfigMode[] modes, IntPtr topologyId);
    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint pathCount, [In] ConfigPath[] paths, uint modeCount, [In] ConfigMode[] modes, uint flags);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetConfigName(ref ConfigTargetName name);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetConfigName(ref ConfigSourceName name);
    private static Device NewDevice() => new() { Size = Marshal.SizeOf<Device>() };
    private static Mode NewMode() => new() { Size = (ushort)Marshal.SizeOf<Mode>() };

    private static DisplayTarget FindTarget(bool requireActive, bool allowTabletOverlap = false)
    {
        var sources = new List<DisplaySource>();
        for (uint i = 0; ; i++)
        {
            var source = NewDevice();
            if (!EnumDisplayDevices(null, i, ref source, 0)) break;
            var monitors = new List<Device>();
            for (uint j = 0; ; j++)
            {
                var monitor = NewDevice();
                if (!EnumDisplayDevices(source.Name, j, ref monitor, 1)) break;
                monitors.Add(monitor);
            }
            sources.Add(new(source, monitors.ToArray()));
        }
        // Count inactive monitors too, so enabling a screen never selects an arbitrary
        // source when multiple MTT monitors happen to be installed.
        var candidates = sources.SelectMany(source => source.Monitors.Where(IsTablet).Select(monitor => source)).ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("未检测到 MTT 虚拟副屏，请先安装或修复显示驱动。");
        if (candidates.Length != 1) throw new InvalidOperationException("发现多块虚拟屏，请先只保留一块副屏。");
        var tablet = candidates[0];
        if ((tablet.Device.Flags & Primary) != 0) throw new InvalidOperationException("虚拟屏是主显示器，请先将电脑屏幕设为主屏。");
        if ((tablet.Device.Flags & Mirroring) != 0 || tablet.Monitors.Any(monitor => !IsTablet(monitor)))
            throw new InvalidOperationException("副屏与其他屏幕共享显示源或处于复制模式，请先检查系统显示布局。");
        var primaries = sources.Where(source => IsAttached(source) && (source.Device.Flags & Primary) != 0).ToArray();
        if (primaries.Length != 1) throw new InvalidOperationException("尚未找到唯一的电脑主屏，请检查显示设置。");
        var primary = primaries[0];
        if ((primary.Device.Flags & Mirroring) != 0 || primary.Monitors.Any(IsTablet) ||
            !(primary.Device.Id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) || primary.Device.Id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("当前主屏不是可确认的实体电脑显示器，请先将电脑屏幕设为主屏。");
        if (IsAttached(tablet))
        {
            var mode = Read(tablet.Device.Name);
            if (!allowTabletOverlap && sources.Where(source => IsAttached(source) && source.Device.Name != tablet.Device.Name).Any(source => Overlaps(mode, Read(source.Device.Name))))
                throw new InvalidOperationException("副屏处于复制或重叠布局，请先切换到扩展显示。");
        }
        else if (requireActive) throw new InvalidOperationException("虚拟副屏尚未激活，请点击“启用副屏”连接扩展显示。");
        return new(tablet, primary, sources.ToArray());
    }
    private static bool IsTablet(Device monitor) => monitor.Id.Contains("MTT1337", StringComparison.OrdinalIgnoreCase);
    private static bool IsAttached(DisplaySource source) => (source.Device.Flags & Attached) != 0;
    private static bool Overlaps(Snapshot a, Snapshot b) => a.X < (long)b.X + b.Width && b.X < (long)a.X + a.Width && a.Y < (long)b.Y + b.Height && b.Y < (long)a.Y + a.Height;
    private static (string Vdd, string Primary) Find()
    {
        var target = FindTarget(true);
        return (target.Tablet.Device.Name, target.Primary.Device.Name);
    }
    private static Mode Current(string name)
    {
        var mode = NewMode();
        if (!EnumDisplaySettingsEx(name, -1, ref mode, 0)) throw new InvalidOperationException("无法读取显示器的当前显示模式。");
        return mode;
    }
    private static Snapshot Read(string name)
    {
        var mode = Current(name);
        return ToSnapshot(name, mode);
    }
    private static Snapshot ToSnapshot(string name, Mode mode) => new(name, mode.Width, mode.Height, mode.Frequency, mode.Orientation, mode.X, mode.Y, mode.Bits, mode.Flags, mode.FixedOutput);
    private static ConfigSnapshot ReadConfig()
    {
        // ALL_PATHS is needed to name one currently detached MTT target. Keep the
        // complete original modes, including target timings, byte-for-byte.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var result = GetDisplayConfigBufferSizes(1, out var pathCount, out var modeCount);
            if (result != 0) throw new InvalidOperationException($"无法读取显示拓扑大小（{result}）。");
            var paths = new ConfigPath[pathCount]; var modes = new ConfigMode[modeCount];
            result = QueryDisplayConfig(1, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result == 122) continue;
            if (result != 0) throw new InvalidOperationException($"无法读取显示拓扑（{result}）。");
            return new(paths.Take((int)pathCount).ToArray(), modes.Take((int)modeCount).ToArray());
        }
        throw new InvalidOperationException("显示设备持续发生变化，请稳定连接后重试。");
    }
    private static string TargetName(ConfigPath path)
    {
        var name = new ConfigTargetName { Header = new() { Type = 2, Size = (uint)Marshal.SizeOf<ConfigTargetName>(), Adapter = path.Target.Adapter, Id = path.Target.Id } };
        var result = GetConfigName(ref name);
        if (result != 0) throw new InvalidOperationException($"无法核对显示目标身份（{result}），操作已停止。");
        return name.MonitorPath;
    }
    private static string SourceName(ConfigPath path)
    {
        var name = new ConfigSourceName { Header = new() { Type = 1, Size = (uint)Marshal.SizeOf<ConfigSourceName>(), Adapter = path.Source.Adapter, Id = path.Source.Id } };
        var result = GetConfigName(ref name);
        if (result != 0) throw new InvalidOperationException($"无法核对显示源身份（{result}），操作已停止。");
        return name.Name;
    }
    private static ActivationPlan PlanActivation(DisplayTarget target, Snapshot planned)
    {
        var original = ReadConfig();
        var mtt = original.Paths.Where(path => path.Target.Available != 0 &&
            string.Equals(TargetName(path), target.Tablet.Monitors.Single().Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(SourceName(path), target.Tablet.Device.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (mtt.Length != 1) throw new InvalidOperationException("无法确认唯一的虚拟副屏拓扑路径，操作已停止。");
        var candidate = mtt[0];
        var active = original.Paths.Where(path => (path.Flags & 1) != 0).ToArray();
        if ((candidate.Flags & 1) != 0 || active.Any(path => path.Source.Adapter.Equals(candidate.Source.Adapter) && path.Source.Id == candidate.Source.Id))
            throw new InvalidOperationException("虚拟副屏路径已启用或与其他屏幕共用显示源，请刷新后检查扩展布局。");
        var modes = original.Modes.ToList();
        candidate.Source.ModeIndex = (uint)modes.Count;
        modes.Add(new ConfigMode { Type = 1, Id = candidate.Source.Id, Adapter = candidate.Source.Adapter, Width = planned.Width, Height = planned.Height, PixelFormat = 4, X = planned.X, Y = planned.Y });
        candidate.Flags = 1; candidate.Target.ModeIndex = uint.MaxValue;
        candidate.Target.Rotation = planned.Orientation + 1; candidate.Target.Scaling = 1; candidate.Target.ScanLine = 1;
        candidate.Target.Refresh = new() { Numerator = planned.Frequency, Denominator = 1 };
        // Existing paths and all their mode indices are unchanged. No ALLOW_CHANGES,
        // topology preset, database reset, or global DisplaySwitch fallback is allowed.
        var plan = new ActivationPlan(original, active.Append(candidate).ToArray(), modes.ToArray());
        ValidateConfig(plan.Paths, plan.Modes);
        return plan;
    }
    private static void ValidateConfig(ConfigPath[] paths, ConfigMode[] modes)
    {
        var result = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, SdcUseSuppliedDisplayConfig | SdcValidate);
        if (result != 0) throw new InvalidOperationException($"Windows 未接受单目标副屏拓扑（{result}），显示配置未更改。");
    }
    private static void ApplyConfig(ConfigPath[] paths, ConfigMode[] modes, bool persist = false)
    {
        var result = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes,
            SdcUseSuppliedDisplayConfig | SdcApply | (persist ? SdcSaveToDatabase : 0));
        if (result != 0) throw new InvalidOperationException($"Windows 未能应用单目标副屏拓扑（{result}），请检查显示设置。");
    }
    private static void VerifyPreservedConfig(ConfigSnapshot original, int expectedActiveCount)
    {
        var after = ReadConfig();
        var current = after.Paths.Where(path => (path.Flags & 1) != 0).ToArray();
        if (current.Length != expectedActiveCount) throw new InvalidOperationException("操作后活动显示器数量与请求不一致，请检查显示设置。");
        foreach (var before in original.Paths.Where(path => (path.Flags & 1) != 0))
        {
            var matches = current.Where(path => path.Source.Adapter.Equals(before.Source.Adapter) && path.Source.Id == before.Source.Id &&
                path.Target.Adapter.Equals(before.Target.Adapter) && path.Target.Id == before.Target.Id).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("操作后原活动显示路径发生变化，请检查显示设置。");
            var now = matches[0];
            if (now.Target.Rotation != before.Target.Rotation || now.Target.Scaling != before.Target.Scaling ||
                !now.Target.Refresh.Equals(before.Target.Refresh) || now.Target.ScanLine != before.Target.ScanLine ||
                !SameConfigMode(original.Modes, before.Source.ModeIndex, after.Modes, now.Source.ModeIndex) ||
                !SameConfigMode(original.Modes, before.Target.ModeIndex, after.Modes, now.Target.ModeIndex))
                throw new InvalidOperationException("操作后实体主屏或原活动显示器的完整模式发生变化，请检查系统显示设置。");
        }
    }
    private static bool SameConfigMode(ConfigMode[] before, uint a, ConfigMode[] after, uint b) =>
        a == uint.MaxValue ? b == uint.MaxValue : a < before.Length && b < after.Length && before[a].Equals(after[b]);
    private static ConfigSnapshot PlanDetach(DisplayTarget target)
    {
        var original = ReadConfig();
        var active = original.Paths.Where(path => (path.Flags & 1) != 0).ToArray();
        var mtt = active.Where(path => string.Equals(TargetName(path), target.Tablet.Monitors.Single().Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(SourceName(path), target.Tablet.Device.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (mtt.Length != 1) throw new InvalidOperationException("无法确认唯一的已激活副屏路径，不能自动恢复。");
        var path = mtt[0];
        var keep = active.Where(other => !(other.Source.Adapter.Equals(path.Source.Adapter) && other.Source.Id == path.Source.Id &&
            other.Target.Adapter.Equals(path.Target.Adapter) && other.Target.Id == path.Target.Id)).ToArray();
        ValidateConfig(keep, original.Modes);
        return new(keep, original.Modes);
    }
    private static Dictionary<string, Snapshot> OtherModes(DisplayTarget target) => target.Sources
        .Where(source => IsAttached(source) && source.Device.Name != target.Tablet.Device.Name)
        .ToDictionary(source => source.Device.Name, source => Read(source.Device.Name), StringComparer.OrdinalIgnoreCase);
    private static DisplayTarget VerifyUnchanged(DisplayTarget before, Dictionary<string, Snapshot> others, bool requireActive, bool allowTabletOverlap = false)
    {
        var after = FindTarget(requireActive, allowTabletOverlap);
        if (!string.Equals(after.Tablet.Monitors.Single().Id, before.Tablet.Monitors.Single().Id, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(after.Tablet.Device.Name, before.Tablet.Device.Name, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(after.Primary.Device.Name, before.Primary.Device.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("显示设备在操作期间发生变化，已停止后续配置，请刷新后重试。");
        var current = OtherModes(after);
        if (current.Count != others.Count || others.Any(item => !current.TryGetValue(item.Key, out var mode) || mode != item.Value))
            throw new InvalidOperationException("操作期间主屏或其他显示器布局发生变化，已停止后续配置，请检查系统显示设置。");
        return after;
    }
    private static void TestMode(string name, ref Mode requested, string action)
    {
        requested.Extra = 0;
        var result = ChangeDisplaySettingsEx(name, ref requested, IntPtr.Zero, 2, IntPtr.Zero);
        if (result != 0) throw new InvalidOperationException($"显示驱动未接受{action}（{result}），当前显示模式未更改。");
    }
    private static void ApplyMode(string name, ref Mode requested, string action)
    {
        // Change only this named MTT source; never perform a global topology reset.
        var result = ChangeDisplaySettingsEx(name, ref requested, IntPtr.Zero, 0, IntPtr.Zero);
        if (result != 0) throw new InvalidOperationException($"{action}未完成（{result}），请检查系统显示设置。");
    }
    /// <summary>Read-only checkpoint. A detached tablet has no current desktop mode.</summary>
    public static DisplayState CaptureState()
    {
        lock (Gate)
        {
            var target = FindTarget(false, allowTabletOverlap: true);
            // CCD is authoritative: EnumDisplayDevices may retain ATTACHED_TO_DESKTOP
            // briefly after a driver transition. Never describe a stale flag as a live screen.
            var active = HasActiveTabletPath(target);
            return new(target.Tablet.Device.Name, target.Tablet.Monitors.Single().Id, active,
                Read(target.Primary.Device.Name), active ? Read(target.Tablet.Device.Name) : null);
        }
    }
    public static string Describe()
    {
        try
        {
            var state = CaptureState();
            if (state.Tablet is not { } s) return "虚拟副屏已检测到，尚未激活；连接副屏后自动启用，断开后自动停用。";
            return $"虚拟副屏已激活 · {s.Width} × {s.Height} · {s.Frequency} Hz · {s.Orientation * 90}°";
        }
        catch (InvalidOperationException ex) { return ex.Message; }
    }
    public static void ValidateExtended() { lock (Gate) { Find(); } }
    public static string TestExtended() => EnsureExtended(false);
    /// <summary>Detaches the owned MTT display while preserving every other active path.</summary>
    public static string DisableExtended(bool apply = true)
    {
        lock (Gate)
        {
            var target = FindTarget(false, allowTabletOverlap: true);
            var others = OtherModes(target);
            if (!HasActiveTabletPath(target))
            {
                VerifyUnchanged(target, others, false, allowTabletOverlap: true);
                return "虚拟副屏已处于未激活状态，主屏保持不变。";
            }
            var plan = PlanDetach(target);
            VerifyUnchanged(target, others, false, allowTabletOverlap: true);
            if (!apply) return "副屏停用检查通过，显示配置未更改。";
            // A temporary detach can be undone when Windows restores its saved
            // topology on wake or device arrival. Persist only the verified
            // topology that removes our MTT target and preserves all other paths.
            ApplyConfig(plan.Paths, plan.Modes, persist: true);
            // IddCx may publish legacy device flags after the CCD operation.
            // CCD is authoritative; repeat only the same verified target if an exiting
            // capture backend briefly reactivates it during disconnect.
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var detached = VerifyUnchanged(target, others, false, allowTabletOverlap: true);
                if (!HasActiveTabletPath(detached))
                {
                    VerifyPreservedConfig(plan, plan.Paths.Length);
                    return "副屏已停用，鼠标不会再进入右侧虚拟区域。";
                }
                if (attempt is 4 or 8 && HasActiveTabletPath(detached))
                {
                    var retry = PlanDetach(detached);
                    VerifyUnchanged(target, others, false, allowTabletOverlap: true);
                    ApplyConfig(retry.Paths, retry.Modes, persist: true);
                }
                Thread.Sleep(100);
            }
            throw new InvalidOperationException("副屏仍被其他程序占用，未确认停用成功；请结束副屏连接后重试。");
        }
    }
    private static bool HasActiveTabletPath(DisplayTarget target) => ReadConfig().Paths.Any(path => (path.Flags & 1) != 0 &&
        string.Equals(TargetName(path), target.Tablet.Monitors.Single().Id, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(SourceName(path), target.Tablet.Device.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Selects a supported mode and operates on this product's MTT source only.</summary>
    public static TabletDisplayMode ConfigureDynamic(TabletDisplayRequest request)
    {
        TabletModeSelection.Validate(request);
        lock (Gate)
        {
            var target = FindTarget(false);
            var others = OtherModes(target);
            var before = IsAttached(target.Tablet) ? Read(target.Tablet.Device.Name) : null;
            var selected = TabletModeSelection.Select(request, EnumerateTabletModes(target.Tablet.Device.Name, request.Orientation));
            var primary = others[target.Primary.Device.Name];
            var requested = before is null ? NewMode() : Current(target.Tablet.Device.Name);
            requested.Width = checked((uint)selected.Width); requested.Height = checked((uint)selected.Height);
            requested.Frequency = checked((uint)selected.RefreshRate); requested.Orientation = checked((uint)(selected.Orientation / 90));
            requested.Bits = 32; requested.Fields = ModeFields;
            requested.X = before?.X ?? checked(primary.X + (int)primary.Width); requested.Y = before?.Y ?? primary.Y;
            var planned = ToSnapshot(target.Tablet.Device.Name, requested);
            if (others.Values.Any(mode => Overlaps(planned, mode)))
                throw new InvalidOperationException("副屏目标位置与其他显示器重叠，请先在系统显示设置中调整扩展布局。");

            if (before is null)
            {
                // An inactive IddCx path has no target timing from which CCD
                // can infer a rotated source. Activate its enumerated native
                // mode first, then rotate this one active source via DEVMODE.
                // Supplying the portrait source directly can fail validation
                // with ERROR_GEN_FAILURE (31) on the MTT driver.
                var native = planned.Orientation % 2 == 0 ? planned with { Orientation = 0 }
                    : planned with { Width = planned.Height, Height = planned.Width, Orientation = 0 };
                if (others.Values.Any(mode => Overlaps(native, mode)))
                    throw new InvalidOperationException("副屏原生模式的启用位置与其他显示器重叠，请先调整扩展布局。");
                var plan = PlanActivation(target, native);
                if (IsAttached(VerifyUnchanged(target, others, false).Tablet))
                    throw new InvalidOperationException("副屏在配置期间被其他程序启用，请重试。");
                try
                {
                    ApplyConfig(plan.Paths, plan.Modes);
                    var active = VerifyUnchanged(target, others, true);
                    VerifyPreservedConfig(plan.Original, plan.Paths.Length);
                    if (Read(active.Tablet.Device.Name) != native)
                        throw new InvalidOperationException("副屏原生模式与请求不一致，连接已停止。");
                    if (native != planned)
                    {
                        TestMode(target.Tablet.Device.Name, ref requested, "副屏分辨率和方向");
                        VerifyUnchanged(target, others, true);
                        ApplyMode(target.Tablet.Device.Name, ref requested, "副屏方向切换");
                    }
                }
                catch (Exception error)
                {
                    // If rotation fails after attaching a temporary native
                    // desktop, reclaim that path immediately. The runtime's
                    // independent cleanup remains a second line of recovery.
                    try { DisableExtended(); }
                    catch (Exception cleanupError)
                    { throw new InvalidOperationException(error.Message + " 副屏回收未完成：" + cleanupError.Message, error); }
                    throw;
                }
            }
            else if (before != planned)
            {
                TestMode(target.Tablet.Device.Name, ref requested, "副屏分辨率和方向");
                var checkedTarget = VerifyUnchanged(target, others, true);
                if (Read(checkedTarget.Tablet.Device.Name) != before)
                    throw new InvalidOperationException("副屏模式在检查期间发生变化，请重试。");
                ApplyMode(target.Tablet.Device.Name, ref requested, "副屏显示模式切换");
            }
            var after = Read(VerifyUnchanged(target, others, true).Tablet.Device.Name);
            if (after != planned)
                throw new InvalidOperationException("Windows 返回的副屏模式与协商结果不一致，连接已停止，请重试。");
            return selected;
        }
    }
    public static TabletDisplayMode[] SupportedTabletModes(int orientation)
    {
        if (orientation is not (0 or 90 or 180 or 270)) throw new ArgumentException("副屏方向无效。");
        lock (Gate)
        {
            return EnumerateTabletModes(FindTarget(false).Tablet.Device.Name, orientation)
                .Where(mode => mode.Width is >= 320 and <= 7680 && mode.Height is >= 320 and <= 7680 &&
                    mode.Width % 2 == 0 && mode.Height % 2 == 0 && (long)mode.Width * mode.Height <= 33_177_600)
                .OrderBy(mode => (long)mode.Width * mode.Height).ThenBy(mode => mode.RefreshRate).Take(128).ToArray();
        }
    }
    private static IEnumerable<TabletDisplayMode> EnumerateTabletModes(string source, int orientation)
    {
        var modes = new HashSet<TabletDisplayMode>();
        foreach (uint flags in new uint[] { 0, 2 })
            for (int index = 0; ; index++)
            {
                var mode = NewMode();
                if (!EnumDisplaySettingsEx(source, index, ref mode, flags)) break;
                if (mode.Bits != 32 || mode.Frequency is < 24 or > 240 || mode.Width is < 200 or > 16384 || mode.Height is < 200 or > 16384) continue;
                var width = mode.Width; var height = mode.Height;
                if ((mode.Orientation & 1) != 0) (width, height) = (height, width);
                if (orientation is 90 or 270) (width, height) = (height, width);
                modes.Add(new(checked((int)width), checked((int)height), checked((int)mode.Frequency), orientation));
            }
        return modes;
    }
    /// <summary>Enables only the unique inactive MTT display immediately right of the physical primary.</summary>
    public static string EnsureExtended(bool apply = true)
    {
        lock (Gate)
        {
            var target = FindTarget(false);
            if (IsAttached(target.Tablet)) return "虚拟副屏已处于扩展模式，现有分辨率和方向保持不变。";
            var others = OtherModes(target);
            var primary = others[target.Primary.Device.Name];
            var requested = FindPreferred(target.Tablet.Device.Name);
            requested.Orientation = 0;
            requested.X = checked(primary.X + (int)primary.Width); requested.Y = primary.Y;
            // Keep the driver's preferred native timing. The receiver's
            // negotiated mode is applied later; this activation fallback must
            // not force the original tablet's 90 Hz onto other devices.
            requested.Bits = 32; requested.Fields = ModeFields;
            var planned = ToSnapshot(target.Tablet.Device.Name, requested);
            if (others.Values.Any(mode => Overlaps(planned, mode)))
                throw new InvalidOperationException("实体主屏右侧已有显示器占用，请先在系统设置中腾出副屏位置。");
            // Legacy CDS_TEST rejects an installed-but-detached IddCx source on
            // this system. CCD validates that exact source while preserving every
            // already active path, source mode and target timing unchanged.
            var plan = PlanActivation(target, planned);
            var checkedTarget = VerifyUnchanged(target, others, false);
            if (IsAttached(checkedTarget.Tablet)) throw new InvalidOperationException("副屏状态在检查期间发生变化，请刷新后重试。");
            if (!apply) return $"副屏启用检查通过 · {requested.Width} × {requested.Height} · {requested.Frequency} Hz · 主屏右侧（{requested.X}, {requested.Y}）；显示配置未更改。";
            ApplyConfig(plan.Paths, plan.Modes);
            var after = VerifyUnchanged(target, others, true);
            VerifyPreservedConfig(plan.Original, plan.Paths.Length);
            if (Read(after.Tablet.Device.Name) != planned)
                throw new InvalidOperationException("副屏已响应，但当前显示模式与请求不一致，请检查系统显示设置。");
            return $"副屏已启用 · {requested.Width} × {requested.Height} · {requested.Frequency} Hz · 位于电脑主屏右侧。";
        }
    }
    /// <summary>Restore the same MTT display only, refusing to change the physical primary.</summary>
    public static string RestoreState(DisplayState saved, bool apply = true)
    {
        ArgumentNullException.ThrowIfNull(saved);
        lock (Gate)
        {
            var target = FindTarget(false); var others = OtherModes(target);
            if (!string.Equals(saved.Source, target.Tablet.Device.Name, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(saved.MonitorId, target.Tablet.Monitors.Single().Id, StringComparison.OrdinalIgnoreCase) ||
                saved.Primary != Read(target.Primary.Device.Name))
                throw new InvalidOperationException("原显示快照与当前设备或主屏不一致，不能自动恢复，请检查系统显示设置。");
            if (saved.Active != (saved.Tablet is not null) || saved.Tablet is { } invalid && invalid.Source != saved.Source)
                throw new ArgumentException("副屏快照不完整。", nameof(saved));
            if (!saved.Active && !IsAttached(target.Tablet) && !HasActiveTabletPath(target))
                return "副屏已恢复为未激活状态，主屏保持不变。";
            if (!saved.Active)
            {
                var plan = PlanDetach(target);
                VerifyUnchanged(target, others, true);
                if (!apply) return "副屏停用恢复检查通过，显示配置未更改。";
                ApplyConfig(plan.Paths, plan.Modes, persist: true);
                var detached = VerifyUnchanged(target, others, false);
                VerifyPreservedConfig(plan, plan.Paths.Length);
                if (IsAttached(detached.Tablet)) throw new InvalidOperationException("副屏未恢复为原先的未激活状态，请检查系统显示设置。");
                return "副屏已恢复为未激活状态，主屏保持不变。";
            }
            var requested = NewMode();
            if (saved.Tablet is { } mode)
            {
                requested.Width = mode.Width; requested.Height = mode.Height; requested.Frequency = mode.Frequency;
                requested.Orientation = mode.Orientation; requested.X = mode.X; requested.Y = mode.Y;
                requested.Bits = mode.BitsPerPixel; requested.Flags = mode.DisplayFlags; requested.FixedOutput = mode.FixedOutput; requested.Fields = ModeFields;
                if (others.Values.Any(other => Overlaps(mode, other))) throw new InvalidOperationException("原副屏位置已被其他显示器占用，不能自动恢复。");
            }
            else { requested.Width = requested.Height = 0; requested.Fields = 0x20 | 0x80000 | 0x100000; }
            TestMode(target.Tablet.Device.Name, ref requested, "恢复原副屏状态");
            VerifyUnchanged(target, others, false);
            if (!apply) return "副屏恢复检查通过，显示配置未更改。";
            ApplyMode(target.Tablet.Device.Name, ref requested, "恢复原副屏状态");
            var after = VerifyUnchanged(target, others, false);
            if (IsAttached(after.Tablet) != saved.Active || saved.Tablet is { } expected && Read(after.Tablet.Device.Name) != expected)
                throw new InvalidOperationException("副屏未完整恢复到原快照，请检查系统显示设置。");
            return saved.Active ? "副屏已恢复原分辨率、方向和位置，主屏保持不变。" : "副屏已恢复为未激活状态，主屏保持不变。";
        }
    }
    public static string Configure(int degrees, bool apply)
    {
        lock (Gate) { return ConfigureCore(degrees, apply); }
    }
    private static string ConfigureCore(int degrees, bool apply)
    {
        if (degrees is not (0 or 90 or 180 or 270)) throw new ArgumentOutOfRangeException(nameof(degrees), "方向必须为 0、90、180 或 270 度。");
        var target = FindTarget(true); var before = Read(target.Tablet.Device.Name); var others = OtherModes(target);
        var requested = Current(target.Tablet.Device.Name);
        bool portrait = degrees is 90 or 270;
        var nativeWidth = (before.Orientation & 1) == 0 ? before.Width : before.Height;
        var nativeHeight = (before.Orientation & 1) == 0 ? before.Height : before.Width;
        requested.Width = portrait ? nativeHeight : nativeWidth;
        requested.Height = portrait ? nativeWidth : nativeHeight;
        requested.Orientation = (uint)(degrees / 90);
        requested.X = before.X; requested.Y = before.Y;
        requested.Bits = 32; requested.Frequency = before.Frequency;
        requested.Fields = ModeFields;
        var planned = ToSnapshot(target.Tablet.Device.Name, requested);
        if (others.Values.Any(mode => Overlaps(planned, mode))) throw new InvalidOperationException("这个方向会与其他显示器重叠，请先调整扩展屏布局。");
        TestMode(target.Tablet.Device.Name, ref requested, "这个副屏方向");
        var checkedTarget = VerifyUnchanged(target, others, true);
        if (Read(checkedTarget.Tablet.Device.Name) != before) throw new InvalidOperationException("副屏模式在检查期间发生变化，请刷新后重试。");
        bool matches = before == planned;
        if (apply && !matches)
        {
            ApplyMode(target.Tablet.Device.Name, ref requested, "副屏方向切换");
        }
        var refreshed = VerifyUnchanged(target, others, true);
        var after = Read(refreshed.Tablet.Device.Name);
        if (after != (apply ? planned : before))
            throw new InvalidOperationException("副屏当前模式与请求不一致，请重新检查显示设置。");
        return apply ? $"副屏已设置为 {degrees}° · {after.Width} × {after.Height} · {after.Frequency} Hz。重新连接接收端以匹配画面方向。" : "副屏方向检查通过，显示配置未更改。";
    }
    private static Mode FindPreferred(string source)
    {
        Mode? preferred = null;
        foreach (uint flags in new uint[] { 0, 2 })
            for (int i = 0; ; i++)
            {
                var mode = NewMode();
                if (!EnumDisplaySettingsEx(source, i, ref mode, flags)) break;
                uint width = mode.Width, height = mode.Height;
                if ((mode.Orientation & 1) != 0)
                {
                    (width, height) = (height, width);
                    // EnsureExtended activates a neutral landscape mode first;
                    // normalize an enumerated rotated mode before returning it.
                    mode.Width = width; mode.Height = height; mode.Orientation = 0;
                }
                if (mode.Bits != 32 || mode.Frequency is < 24 or > 240 || width is < 200 or > 16384 || height is < 200 or > 16384) continue;
                if (preferred is null || (ulong)width * height > (ulong)preferred.Value.Width * preferred.Value.Height ||
                    ((ulong)width * height == (ulong)preferred.Value.Width * preferred.Value.Height && mode.Frequency > preferred.Value.Frequency))
                    preferred = mode;
            }
        return preferred ?? throw new InvalidOperationException("虚拟显示驱动未提供可用显示模式，请修复安装。");
    }
}

/// <summary>Pure negotiation logic, shared by display configuration and regression checks.</summary>
internal static class TabletModeSelection
{
    public static void Validate(TabletDisplayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Width is < 200 or > 16384 || request.Height is < 200 or > 16384 ||
            request.RefreshRate is < 24 or > 240 || request.Orientation is not (0 or 90 or 180 or 270))
            throw new ArgumentException("副屏尺寸须为 200–16384 像素，刷新率须为 24–240 Hz，方向须为 0、90、180 或 270 度。");
    }

    public static TabletDisplayMode Select(TabletDisplayRequest request, IEnumerable<TabletDisplayMode> available)
    {
        Validate(request);
        // Resolution first: preserve desktop text density whenever an exact
        // resolution exists. Otherwise protect aspect ratio, then pick the
        // nearest pixel area. At the same size prefer a supported rate at or
        // below the receiver's cap before considering a higher rate.
        var selected = available.Where(mode => mode.Width is >= 200 and <= 16384 && mode.Height is >= 200 and <= 16384 &&
                mode.RefreshRate is >= 24 and <= 240 && mode.Orientation == request.Orientation)
            .Distinct()
            .OrderBy(mode => Math.Abs(Math.Log(((double)mode.Width * request.Height) / ((double)mode.Height * request.Width))))
            .ThenBy(mode => Math.Abs(Math.Log(((double)mode.Width * mode.Height) / ((double)request.Width * request.Height))))
            .ThenBy(mode => mode.RefreshRate > request.RefreshRate ? 1 : 0)
            .ThenBy(mode => Math.Abs(mode.RefreshRate - request.RefreshRate))
            .ThenBy(mode => mode.Width).ThenBy(mode => mode.Height).ThenBy(mode => mode.RefreshRate)
            .FirstOrDefault() ?? throw new InvalidOperationException("虚拟显示驱动未提供可用显示模式，请修复显示驱动后重试。");
        return selected with { Degraded = selected.Width != request.Width || selected.Height != request.Height || selected.RefreshRate != request.RefreshRate };
    }
}
