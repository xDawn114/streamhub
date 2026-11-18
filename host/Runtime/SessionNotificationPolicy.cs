namespace StreamHub.Host.Runtime;

public sealed record SessionNotification(string Title, string Message);

/// <summary>Small, non-sensitive tray notices for transitions in the authenticated session state.</summary>
public static class SessionNotificationPolicy
{
    public static string State(RuntimeStatus? status)
    {
        if (status is null) return "unavailable";
        if (status.Remote.Streaming && status.Tablet.Streaming) return "both-streaming";
        if (status.Remote.Streaming) return status.Remote.SessionVerified ? "remote-streaming" : "remote-verifying";
        if (status.Tablet.Streaming) return status.Tablet.SessionVerified ? "tablet-streaming" : "tablet-verifying";
        return "idle";
    }

    public static SessionNotification? Transition(string previous, string current)
    {
        if (previous == current || previous == "initial" || current == "unavailable") return null;
        // Informational toasts are captured with the main desktop. The host's
        // live status/tray remains visible; permission and security alerts use other paths.
        if (current is "both-streaming" or "remote-streaming" or "remote-verifying") return null;
        if (current is "tablet-streaming" or "tablet-verifying") return new("我控制 · 平板副屏已连接", "电脑正在使用平板作为副屏。");
        if (previous is "remote-streaming" or "remote-verifying" or "tablet-streaming" or "tablet-verifying" or "both-streaming")
            return new("我控制 · 串流已结束", "当前远程画面已断开，电脑显示状态正在收尾。");
        return null;
    }
}
