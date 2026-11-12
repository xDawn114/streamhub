namespace StreamHub.Host.Runtime;

/// <summary>
/// Keeps the privacy overlay behind two independent gates: an owner's saved
/// preference and authenticated proof that the remote stream has started.
/// </summary>
public static class PrivacyScreenPolicy
{
    public const int MaxConsecutiveFailures = 3;

    public static bool ShouldShow(bool enabled, bool authenticatedStream, bool remoteEngineRunning) =>
        enabled && authenticatedStream && remoteEngineRunning;

    public static bool ShouldRetry(int consecutiveFailures) =>
        consecutiveFailures < MaxConsecutiveFailures;

    public static bool RequiresSessionRestart(bool interactive, uint helperSession, uint activeSession) =>
        !interactive && helperSession != activeSession;

    public static string Message(bool enabled, bool active, bool authenticatedStream, bool remoteEngineRunning) =>
        !enabled ? "远控隐私保护已关闭" : active ? "远控隐私保护已开启，本机显示黑屏" :
        authenticatedStream && remoteEngineRunning ? "正在开启本机隐私黑屏" : "已开启，等待远控串流出画";

    public static string RetryLimitMessage() => "隐私黑屏助手连续失败，已停止自动重试；请关闭后重新开启隐私保护。";
}
