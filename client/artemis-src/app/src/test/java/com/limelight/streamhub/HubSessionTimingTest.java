package com.limelight.streamhub;

import org.junit.Test;
import static org.junit.Assert.*;

public class HubSessionTimingTest {
    @Test public void rotationStartsASeparateVideoGenerationOnTheSameLease() {
        HubSessionTiming timing = new HubSessionTiming(100);
        assertTrue(timing.markMediaConnected());
        assertTrue(timing.markFirstFrame());
        timing.markConnectionReported(timing.pendingReportGeneration());
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

    @Test public void lateAcknowledgementCannotMarkAReplacementVideoAsReported() {
        HubSessionTiming timing = new HubSessionTiming(100);
        assertEquals(-1, timing.pendingReportGeneration());
        timing.markMediaConnected();
        long oldReport = timing.pendingReportGeneration();
        timing.begin(500);
        timing.markMediaConnected();
        timing.markConnectionReported(oldReport);
        assertFalse(timing.connectionReported());
        timing.markConnectionReported(timing.pendingReportGeneration());
        assertTrue(timing.connectionReported());
        assertEquals(-1, timing.pendingReportGeneration());
    }
}
