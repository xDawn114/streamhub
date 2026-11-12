package com.limelight.streamhub;

/** Shared by capability negotiation and the decoder request; never changes dimensions. */
public final class HubTransportPolicy {
    private HubTransportPolicy() { }
    public static float limitFps(float requested, String route) {
        return Math.min(requested, "usb".equals(route) ? 90 : 60);
    }
    public static int bitrateCap(String quality, String route, boolean metered) {
        boolean clear = "clear".equals(quality);
        int cap = "usb".equals(route) ? (clear ? 24000 : 12000) :
                "remote".equals(route) ? (clear ? 10000 : 6000) : (clear ? 16000 : 8000);
        return metered ? Math.min(cap, 6000) : cap;
    }
}
