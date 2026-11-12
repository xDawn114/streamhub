package com.limelight.streamhub;

import org.junit.Test;
import static org.junit.Assert.*;

public class HubSessionTimingTest {
    @Test public void rotationStartsASeparateVideoGenerationOnTheSameLease() {
        HubSessionTiming timing = new HubSessionTiming(100);
        assertTrue(timing.markMediaConnected());
        assertTrue(timing.markFirstFrame());
        timing.markConnectionReported();
        assertFalse(timing.begin(100));
        assertTrue(timing.mediaConnected());
        assertTrue(timing.connectionReported());

        assertTrue(timing.begin(500));
        assertEquals(25, timing.elapsed(525));
        assertFalse(timing.mediaConnected());
        assertFalse(timing.connectionReported());
        assertTrue(timing.markMediaConnected());
        assertTrue(timing.markFirstFrame());
        assertFalse(timing.markFirstFrame());
    }
}
