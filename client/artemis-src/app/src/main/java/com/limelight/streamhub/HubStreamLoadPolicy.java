package com.limelight.streamhub;

/** Separates sustained device-side stream pressure from the network warning path. */
public final class HubStreamLoadPolicy {
    private static final int REQUIRED_SAMPLES = 2;
    private int highLoadSamples;
    private int normalSamples;
    private boolean highLoad;

    public synchronized boolean isHighLoad() {
        return highLoad;
    }

    /**
     * Adds one decoder/rendering sample. Network evidence suppresses device-load classification;
     * HubNetworkLadder remains responsible for deciding when that evidence is sustained enough
     * to show a network warning.
     */
    public synchronized boolean sample(boolean networkDegraded, float requestedFps, float panelHz,
            float receivedFps, float renderedFps, float decodeMs, int rttMs, int lostFrames, int totalFrames) {
        if (lostFrames < 0 || totalFrames < lostFrames || rttMs < 0 || rttMs > 10000) {
            throw new IllegalArgumentException("invalid stream load sample");
        }

        boolean networkEvidence = networkDegraded || rttMs >= 150 ||
                (totalFrames > 0 && (double) lostFrames / totalFrames >= 0.02);
        if (networkEvidence || !isFinitePositive(requestedFps) || !Float.isFinite(receivedFps)
                || !Float.isFinite(renderedFps) || !Float.isFinite(decodeMs)) {
            highLoadSamples = 0;
            normalSamples = 0;
            highLoad = false;
            return false;
        }

        float effectiveFps = isFinitePositive(panelHz) ? Math.min(requestedFps, panelHz) : requestedFps;
        if (isDeviceLoad(effectiveFps, receivedFps, renderedFps, decodeMs)) {
            highLoadSamples++;
            normalSamples = 0;
            if (highLoadSamples >= REQUIRED_SAMPLES) highLoad = true;
        }
        else {
            highLoadSamples = 0;
            normalSamples++;
            if (normalSamples >= REQUIRED_SAMPLES) highLoad = false;
        }
        return highLoad;
    }

    private static boolean isDeviceLoad(float requestedFps, float receivedFps, float renderedFps,
            float decodeMs) {
        float frameBudgetMs = 1000f / requestedFps;
        float decodePressureThreshold = Math.max(18f, frameBudgetMs * 1.6f);
        boolean decodeOverBudget = decodeMs >= decodePressureThreshold;
        boolean fpsLimited = receivedFps < requestedFps * 0.75f || renderedFps < requestedFps * 0.75f;
        boolean decodeElevated = decodeMs >= Math.max(12f, frameBudgetMs * 1.2f);
        return decodeOverBudget || (fpsLimited && decodeElevated);
    }

    private static boolean isFinitePositive(float value) {
        return Float.isFinite(value) && value > 0;
    }
}
