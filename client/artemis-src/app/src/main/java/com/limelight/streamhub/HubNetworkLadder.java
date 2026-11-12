package com.limelight.streamhub;

/** Five-second frame-loss/RTT windows; results are recommendations, not live encoder changes. */
public final class HubNetworkLadder {
    private static final int[] LEVELS = {40000, 25000, 15000, 8000};
    private final int configuredKbps, ceiling;
    private int level, badWindows, goodWindows;
    private boolean networkDegraded;
    private long start = -1, lost, frames, rttTotal;
    private int rttSamples;

    public HubNetworkLadder(int configuredKbps) {
        if (configuredKbps <= 0) throw new IllegalArgumentException("bitrate must be positive");
        this.configuredKbps = configuredKbps;
        int index = 0;
        while (index < LEVELS.length - 1 && LEVELS[index + 1] >= configuredKbps) index++;
        this.ceiling = this.level = index;
    }

    public synchronized int recommendationKbps() { return Math.min(configuredKbps, LEVELS[level]); }

    /** True only after a completed window has supplied sustained loss or RTT evidence. */
    public synchronized boolean isNetworkDegraded() { return networkDegraded; }

    public synchronized int sample(long nowMs, int lostFrames, int totalFrames, int rttMs) {
        if (nowMs < 0 || lostFrames < 0 || totalFrames < lostFrames || rttMs < 0 || rttMs > 10000)
            throw new IllegalArgumentException("invalid network sample");
        if (start < 0 || nowMs < start || nowMs - start > 10000) {
            clearWindow(nowMs);
            badWindows = goodWindows = 0;
            networkDegraded = false;
        }
        lost += lostFrames;
        frames += totalFrames;
        if (rttMs > 0) { rttTotal += rttMs; rttSamples++; }
        if (nowMs - start < 5000) return recommendationKbps();

        if (frames == 0 && rttSamples == 0) {
            badWindows = goodWindows = 0;
        } else {
            double loss = frames == 0 ? 0 : (double)lost / frames;
            double rtt = rttSamples == 0 ? 0 : (double)rttTotal / rttSamples;
            boolean severe = loss >= 0.08 || rtt >= 300;
            boolean bad = loss >= 0.02 || rtt >= 150;
            boolean good = frames > 0 && rttSamples > 0 && loss < 0.005 && rtt < 80;
            if (severe || bad) {
                goodWindows = 0;
                badWindows++;
                boolean sustained = severe || badWindows >= 2;
                if (sustained) {
                    networkDegraded = true;
                    if (level < LEVELS.length - 1) level++;
                    badWindows = 0;
                }
            } else {
                badWindows = 0;
                goodWindows = good ? goodWindows + 1 : 0;
                if (goodWindows >= 2) networkDegraded = false;
                if (goodWindows >= 6 && level > ceiling) { level--; goodWindows = 0; }
            }
        }
        clearWindow(nowMs);
        return recommendationKbps();
    }

    private void clearWindow(long nowMs) { start = nowMs; lost = frames = rttTotal = 0; rttSamples = 0; }
}
