package com.limelight.binding.video;

public interface PerfOverlayListener {
    void onPerfUpdate(final String text);
    /** Collect diagnostics while keeping the visual overlay hidden. */
    default boolean needsPerfUpdates() { return false; }
    default void onVideoSample(float receivedFps, float renderedFps, float decodeMs, int rttMs, int lostFrames, int totalFrames) { }
    default boolean needsNetworkSamples() { return false; }
    default void onNetworkSample(long nowMs, int lostFrames, int totalFrames, int rttMs) { }
}
