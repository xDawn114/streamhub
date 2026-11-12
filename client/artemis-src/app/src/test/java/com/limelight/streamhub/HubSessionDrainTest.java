package com.limelight.streamhub;

import java.io.IOException;
import java.util.concurrent.CancellationException;
import java.util.concurrent.atomic.AtomicInteger;
import org.junit.Test;
import static org.junit.Assert.*;

public class HubSessionDrainTest {
    @Test public void confirmedIdleAddsNoArtificialDelay() throws Exception {
        AtomicInteger waits = new AtomicInteger();
        HubSessionDrain.awaitIdle(() -> true, ms -> waits.incrementAndGet());
        assertEquals(0, waits.get());
    }

    @Test public void transitionalBusyWaitsAndOnlyConfirmedIdleAllowsHandoff() throws Exception {
        AtomicInteger probes = new AtomicInteger(), waits = new AtomicInteger();
        HubSessionDrain.awaitIdle(() -> probes.incrementAndGet() == 3, ms -> waits.addAndGet((int) ms));
        assertEquals(3, probes.get());
        assertEquals(1000, waits.get());
    }

    @Test public void permanentlyBusyDoesNotStartAnotherSession() throws Exception {
        AtomicInteger probes = new AtomicInteger(), waits = new AtomicInteger();
        try {
            HubSessionDrain.awaitIdle(() -> { probes.incrementAndGet(); return false; }, ms -> waits.incrementAndGet());
            fail("busy host accepted");
        } catch (IOException expected) { }
        assertEquals(8, probes.get());
        assertEquals(7, waits.get());
    }

    @Test public void failedProbeAndUserCancellationCannotMasqueradeAsIdle() throws Exception {
        IOException error = new IOException("unavailable");
        try { HubSessionDrain.awaitIdle(() -> { throw error; }, ms -> fail("failure must not retry blindly")); fail(); }
        catch (IOException actual) { assertSame(error, actual); }
        try { HubSessionDrain.awaitIdle(() -> false, ms -> { throw new CancellationException(); }); fail(); }
        catch (CancellationException expected) { }
    }
}
