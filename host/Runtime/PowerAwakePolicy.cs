namespace StreamHub.Host.Runtime;

public static class PowerAwakePolicy
{
    public const string System = "system";
    public const string Streaming = "streaming";
    public const string PluggedIn = "plugged-in";

    public static bool IsValid(string? mode) => mode is System or Streaming or PluggedIn;
    public static string Normalize(string? mode) => IsValid(mode) ? mode! : Streaming;
    public static bool ShouldPreventSleep(string mode, bool streaming, bool onAcPower) => mode switch
    {
        Streaming => streaming,
        PluggedIn => onAcPower || streaming,
        _ => false
    };

    public static string Describe(string mode, bool active, bool failed) => failed
        ? "系统防休眠请求未成功，后台将重试。"
        : active ? "正在防止电脑自动睡眠，屏幕仍可自动熄灭。"
        : mode switch
        {
            Streaming => "连接远控或扩展屏后保持唤醒，断开后跟随系统。",
            PluggedIn => "插电时保持远程在线；使用电池时仅在串流期间防休眠。",
            _ => "电脑按 Windows 电源设置自动睡眠。"
        };
}
