package com.limelight.streamhub;

/** Explicit checks bypass the interval; background retries remain bounded. */
public final class HubUpdateSchedule {
    public static final long INTERVAL = 12L * 60 * 60 * 1000;
    private HubUpdateSchedule() { }
    public static boolean due(boolean enabled, long next, long now) {
        return enabled && (next <= now || next > now + INTERVAL);
    }
    public static long retryDelay(int previousFailures) {
        return (previousFailures <= 0 ? 5 : previousFailures == 1 ? 15 : previousFailures == 2 ? 60 : 720) * 60000L;
    }
    public static boolean canInstall(boolean preparing, boolean streaming, boolean transferring) {
        return !preparing && !streaming && !transferring;
    }
}
