namespace StreamHub.Host.Runtime;

/// <summary>Validation for the future active-desktop to Sunshine output mapping.</summary>
public static class RemoteDisplaySelection
{
    public static void ValidateRequest(string mode, int? index)
    {
        if (index is null) return;
        if (mode != "remote") throw new ArgumentException("屏幕选择仅用于远程主屏，副屏连接不接受此参数。");
        if (index < 0 || index > 31) throw new ArgumentException("显示器序号无效。");
        throw new NotSupportedException("此版本尚不能可靠识别指定显示器，请使用默认主屏连接。");
    }

    public static string ValidateOutput(string output)
    {
        if (!Guid.TryParseExact(output, "B", out var id) || id == Guid.Empty)
            throw new ArgumentException("显示器标识无效，未更改串流配置。");
        return id.ToString("B");
    }
}
