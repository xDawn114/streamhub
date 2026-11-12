package com.limelight.streamhub;

/** Chooses an existing built-in panel mode without changing its resolution. */
public final class HubDisplayModePolicy {
    private static final float MAX_REFRESH_RATE_ERROR = 0.5f;

    private HubDisplayModePolicy() { }

    public static boolean applies(boolean managedSession, boolean externalDisplay) {
        return managedSession && !externalDisplay;
    }

    public static boolean isBetterMatch(int currentWidth, int currentHeight, float targetFps,
                                        int width, int height, float refreshRate, float bestRefreshRate) {
        if (!samePhysicalResolution(currentWidth, currentHeight, width, height) ||
                !Float.isFinite(targetFps) || targetFps <= 0 ||
                !Float.isFinite(refreshRate) || refreshRate <= 0) {
            return false;
        }

        float error = Math.abs(refreshRate - targetFps);
        return error <= MAX_REFRESH_RATE_ERROR &&
                (!Float.isFinite(bestRefreshRate) || error < Math.abs(bestRefreshRate - targetFps));
    }

    public static int bestMatchingModeIndex(int currentWidth, int currentHeight, float targetFps,
                                            int[] widths, int[] heights, float[] refreshRates) {
        if (widths == null || heights == null || refreshRates == null ||
                widths.length != heights.length || widths.length != refreshRates.length) {
            return -1;
        }

        int bestIndex = -1;
        float bestRate = Float.NaN;
        for (int i = 0; i < widths.length; i++) {
            if (isBetterMatch(currentWidth, currentHeight, targetFps, widths[i], heights[i],
                    refreshRates[i], bestRate)) {
                bestIndex = i;
                bestRate = refreshRates[i];
            }
        }
        return bestIndex;
    }

    public static boolean isApplied(float actualRefreshRate, float requestedRefreshRate,
                                    int actualModeId, int requestedModeId) {
        return Float.isFinite(actualRefreshRate) && Float.isFinite(requestedRefreshRate) &&
                requestedRefreshRate > 0 && Math.abs(actualRefreshRate - requestedRefreshRate) <= 1f &&
                (requestedModeId <= 0 || actualModeId == requestedModeId);
    }

    public static boolean shouldUseAtLeastSurfaceRate(boolean managedSession, boolean externalDisplay,
                                                       float requestedFps, int sdkVersion) {
        return applies(managedSession, externalDisplay) && sdkVersion >= 36 &&
                Float.isFinite(requestedFps) && requestedFps > 60f;
    }

    private static boolean samePhysicalResolution(int firstWidth, int firstHeight,
                                                   int secondWidth, int secondHeight) {
        return (firstWidth == secondWidth && firstHeight == secondHeight) ||
                (firstWidth == secondHeight && firstHeight == secondWidth);
    }
}
