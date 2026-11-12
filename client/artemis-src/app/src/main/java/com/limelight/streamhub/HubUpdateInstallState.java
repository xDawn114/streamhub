package com.limelight.streamhub;

/** A launch attempt is not proof of installation. Confirm against this process's installed build. */
public final class HubUpdateInstallState {
    private HubUpdateInstallState() { }
    public static String describe(int previous, int target, String channel, long started, int current, String currentChannel, long now) {
        if (previous < 1 || target <= previous || !currentChannel.equals(channel) || started > now + 300000 ||
                started < now - 30L * 86400000) return "";
        if (current == target) return "上次更新已完成，电脑绑定和设置保留。";
        if (current == previous) return "上次系统安装尚未确认完成，当前版本仍可使用；可重新检查并重试。";
        return "";
    }
}
