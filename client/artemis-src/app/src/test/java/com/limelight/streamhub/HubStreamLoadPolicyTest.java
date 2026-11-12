package com.limelight.streamhub;

import org.junit.Test;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

public class HubStreamLoadPolicyTest {
    @Test public void oneHighDecodeSampleDoesNotShowWarning() {
        HubStreamLoadPolicy policy = new HubStreamLoadPolicy();
        assertFalse(policy.sample(false, 90, 90, 80, 78, 22, 12, 0, 100));
        assertFalse(policy.isHighLoad());
    }

    @Test public void stage53DynamicStreamDoesNotShowDeviceLoadWarning() {
        HubStreamLoadPolicy policy = new HubStreamLoadPolicy();
        assertFalse(policy.sample(false, 90, 90, 85.4f, 84.95f, 12.71f, 12, 0, 174));
        assertFalse(policy.sample(false, 90, 90, 85.4f, 84.95f, 12.67f, 16, 0, 174));
        assertFalse(policy.isHighLoad());
    }

    @Test public void sixtyHertzPanelCapDoesNotLookLikeDeviceDecodePressure() {
        HubStreamLoadPolicy policy = new HubStreamLoadPolicy();
        assertFalse(policy.sample(false, 90, 60, 64, 64, 14.7f, 14, 0, 127));
        assertFalse(policy.sample(false, 90, 60, 63, 63, 14.2f, 14, 0, 127));
        assertFalse(policy.isHighLoad());
    }

    @Test public void sustainedHighDecodeWithHealthyNetworkShowsLoadWarning() {
        HubStreamLoadPolicy policy = new HubStreamLoadPolicy();
        policy.sample(false, 90, 90, 70, 68, 22, 12, 0, 100);
        assertTrue(policy.sample(false, 90, 90, 70, 68, 22, 12, 0, 100));
        assertTrue(policy.isHighLoad());
    }

    @Test public void networkEvidenceNeverBecomesDeviceLoadWarning() {
        HubStreamLoadPolicy policy = new HubStreamLoadPolicy();
        assertFalse(policy.sample(false, 90, 90, 70, 68, 22, 220, 0, 100));
        assertFalse(policy.sample(false, 90, 90, 70, 68, 22, 220, 0, 100));
        assertFalse(policy.isHighLoad());
    }

    @Test public void normalSamplesClearSustainedLoadWarning() {
        HubStreamLoadPolicy policy = new HubStreamLoadPolicy();
        policy.sample(false, 90, 90, 70, 68, 22, 12, 0, 100);
        policy.sample(false, 90, 90, 70, 68, 22, 12, 0, 100);
        assertTrue(policy.isHighLoad());
        policy.sample(false, 90, 90, 88, 87, 12, 12, 0, 100);
        assertTrue(policy.isHighLoad());
        assertFalse(policy.sample(false, 90, 90, 88, 87, 12, 12, 0, 100));
        assertFalse(policy.isHighLoad());
    }
}
