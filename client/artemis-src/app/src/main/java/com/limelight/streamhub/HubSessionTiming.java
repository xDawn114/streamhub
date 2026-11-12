package com.limelight.streamhub;

/** Tracks one video connection generation inside a reusable host lease. */
public final class HubSessionTiming {
    private long connectionStarted;
    private boolean mediaConnected;
    private boolean firstFrame;
    private boolean connectionReported;

    public HubSessionTiming(long started) { connectionStarted = started; }

    /** Returns true when the lease has moved to a new video connection. */
    public synchronized boolean begin(long started) {
        if (started == connectionStarted) return false;
        connectionStarted = started;
        mediaConnected = false;
        firstFrame = false;
        connectionReported = false;
        return true;
    }

    public synchronized long elapsed(long now) { return Math.max(0, now - connectionStarted); }
    public synchronized boolean markMediaConnected() {
        if (mediaConnected) return false;
        mediaConnected = true;
        return true;
    }
    public synchronized boolean markFirstFrame() {
        if (firstFrame) return false;
        firstFrame = true;
        return true;
    }
    public synchronized boolean mediaConnected() { return mediaConnected; }
    public synchronized boolean connectionReported() { return connectionReported; }
    public synchronized void markConnectionReported() { connectionReported = true; }
}
