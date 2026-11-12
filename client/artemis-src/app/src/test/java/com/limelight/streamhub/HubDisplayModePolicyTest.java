package com.limelight.streamhub;

import org.junit.Test;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

public class HubDisplayModePolicyTest {
    @Test public void appliesToManagedSessionOnBuiltInDisplay() {
        assertTrue(HubDisplayModePolicy.applies(true, false));
        assertFalse(HubDisplayModePolicy.applies(false, false));
        assertFalse(HubDisplayModePolicy.applies(true, true));
    }

    @Test public void managedNinetyFpsMatchesNinetyHertzAtSameResolution() {
        assertTrue(matches(90, 2064, 3096, 90, Float.NaN));
        assertFalse(matches(90, 2064, 3096, 60, Float.NaN));
        assertFalse(matches(90, 2064, 3096, 120, Float.NaN));
        assertFalse(matches(90, 1920, 1080, 90, Float.NaN));
    }

    @Test public void lowerRequestedFpsDoesNotSelectNinetyHertz() {
        assertTrue(matches(60, 2064, 3096, 59.94f, Float.NaN));
        assertFalse(matches(60, 2064, 3096, 90, Float.NaN));
        assertTrue(matches(30, 2064, 3096, 30, Float.NaN));
        assertFalse(matches(30, 2064, 3096, 90, Float.NaN));
    }

    @Test public void missingExactModeLeavesTheExistingPolicyInControl() {
        assertFalse(matches(75, 2064, 3096, 60, Float.NaN));
        assertFalse(matches(75, 2064, 3096, 90, Float.NaN));
        assertFalse(matches(90, 1920, 1080, 90, Float.NaN));
    }

    @Test public void acceptsTheSamePanelResolutionAfterOrientationChanges() {
        assertTrue(matches(90, 3096, 2064, 90, Float.NaN));
    }

    @Test public void choosesClosestMatchingRateAndKeepsFirstOnTie() {
        assertTrue(matches(90, 2064, 3096, 89.9f, Float.NaN));
        assertTrue(matches(90, 2064, 3096, 90, 89.9f));
        assertFalse(matches(90, 2064, 3096, 89.9f, 90));
        assertFalse(matches(90, 2064, 3096, 90, 90));
        assertFalse(matches(90, 2064, 3096, 89.49f, Float.NaN));
    }

    @Test public void invalidRatesNeverProduceARequest() {
        assertFalse(matches(Float.NaN, 2064, 3096, 90, Float.NaN));
        assertFalse(matches(Float.POSITIVE_INFINITY, 2064, 3096, 90, Float.NaN));
        assertFalse(matches(0, 2064, 3096, 90, Float.NaN));
        assertFalse(matches(90, 2064, 3096, Float.NaN, Float.NaN));
        assertFalse(matches(90, 2064, 3096, Float.POSITIVE_INFINITY, Float.NaN));
    }

    @Test public void reselectsExactModeFromCurrentSupportedModeList() {
        assertEquals(1, HubDisplayModePolicy.bestMatchingModeIndex(2064, 3096, 90,
                new int[]{2064, 3096, 2064}, new int[]{3096, 2064, 3096},
                new float[]{60, 90, 89.9f}));
        assertEquals(-1, HubDisplayModePolicy.bestMatchingModeIndex(2064, 3096, 90,
                new int[]{2064}, new int[]{3096}, new float[]{60}));
        assertEquals(-1, HubDisplayModePolicy.bestMatchingModeIndex(2064, 3096, 90,
                new int[]{2064, 2064}, new int[]{3096}, new float[]{60, 90}));
    }

    @Test public void reportsWhetherRequestedRefreshModeActuallyApplied() {
        assertTrue(HubDisplayModePolicy.isApplied(90, 90, 3, 3));
        assertTrue(HubDisplayModePolicy.isApplied(89.5f, 90, 3, 3));
        assertFalse(HubDisplayModePolicy.isApplied(60, 90, 4, 3));
        assertFalse(HubDisplayModePolicy.isApplied(90, 90, 4, 3));
        assertFalse(HubDisplayModePolicy.isApplied(90, 0, 3, 3));
    }

    @Test public void atLeastSurfaceRateIsLimitedToManagedHighRateBuiltInSessions() {
        assertTrue(HubDisplayModePolicy.shouldUseAtLeastSurfaceRate(true, false, 90, 36));
        assertFalse(HubDisplayModePolicy.shouldUseAtLeastSurfaceRate(true, false, 90, 35));
        assertFalse(HubDisplayModePolicy.shouldUseAtLeastSurfaceRate(true, false, 60, 36));
        assertFalse(HubDisplayModePolicy.shouldUseAtLeastSurfaceRate(false, false, 90, 36));
        assertFalse(HubDisplayModePolicy.shouldUseAtLeastSurfaceRate(true, true, 90, 36));
    }

    private static boolean matches(float targetFps, int width, int height, float rate, float bestRate) {
        return HubDisplayModePolicy.isBetterMatch(2064, 3096, targetFps, width, height, rate, bestRate);
    }
}
