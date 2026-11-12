package com.limelight.streamhub;

import java.util.concurrent.TimeUnit;

public final class HubAddressFreshness {
    private HubAddressFreshness() { }
    public static boolean eligible(String route, long seenAt, String learnedNetwork, String network, long now) {
        if (seenAt == 0) return true; // Existing/manual seed: bounded discovery validates it.
        long age = Math.max(0, now - seenAt);
        if (age > TimeUnit.DAYS.toMillis("remote".equals(route) ? 7 : 1)) return false;
        return "remote".equals(route) || "usb".equals(route) || learnedNetwork.isEmpty() ||
                "unknown".equals(network) || learnedNetwork.equals(network);
    }
}
