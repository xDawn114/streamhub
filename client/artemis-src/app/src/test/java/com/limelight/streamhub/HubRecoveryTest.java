package com.limelight.streamhub;

import java.io.IOException;
import java.net.SocketTimeoutException;
import java.util.Arrays;
import javax.net.ssl.SSLHandshakeException;
import org.junit.Test;
import static org.junit.Assert.*;

public class HubRecoveryTest {
    @Test public void progressResetsRetryBudgetAndNoProgressRemainsBounded() {
        HubFileRetryWindow window = new HubFileRetryWindow();
        Exception offline = new SocketTimeoutException();
        assertEquals(1000, window.afterFailure(offline, true, 0, 0, 100).delayMs);
        assertEquals(2000, window.afterFailure(offline, true, 0, 0, 1100).delayMs);
        assertEquals(1000, window.afterFailure(offline, true, 0, 256, 3100).delayMs);
        assertEquals(2000, window.afterFailure(offline, true, 256, 256, 4100).delayMs);
        assertEquals(4000, window.afterFailure(offline, true, 256, 256, 6100).delayMs);
        assertTrue(window.afterFailure(offline, true, 256, 256, 10100).waitingForNetwork());
        assertNotNull(window.afterFailure(offline, true, 256, 256, 35100));
        assertNull(window.afterFailure(offline, true, 256, 256, 40100));
    }

    @Test public void unknownUploadNeverEntersWaitingWindow() {
        HubFileRetryWindow window = new HubFileRetryWindow();
        Exception offline = new HubFileRetryPolicy.DiscoveryFailure(new IOException("offline"));
        for (int i = 0; i < 3; i++) assertNotNull(window.afterFailure(offline, false, 0, 0, i * 1000));
        assertNull(window.afterFailure(offline, false, 0, 0, 4000));
        assertNull(new HubFileRetryWindow().afterFailure(new SocketTimeoutException(), false, 0, 0, 0));
    }

    @Test public void mixedAddressFailuresDoNotLoseAuthorizationOrCertificateErrors() throws Exception {
        for (boolean reverse : new boolean[]{false, true}) {
            try {
                HubProbeRace.first(Arrays.asList("denied", "timeout"), host -> {
                    if (host.equals(reverse ? "denied" : "timeout")) Thread.sleep(25);
                    if (host.equals("denied")) throw new HubFileRetryPolicy.HttpFailure(403, "denied");
                    throw new SocketTimeoutException();
                }, () -> false, 1000);
                fail("both probes failed");
            } catch (HubProbeRace.ProbeFailure failure) {
                assertEquals(2, failure.getSuppressed().length);
                assertFalse(HubFileRetryPolicy.shouldRetry(new HubFileRetryPolicy.DiscoveryFailure(failure), 0, true));
            }
        }
        try {
            HubProbeRace.first(Arrays.asList("tls", "timeout"), host -> {
                if (host.equals("tls")) throw new SSLHandshakeException("pin");
                throw new SocketTimeoutException();
            }, () -> false, 1000);
            fail("both probes failed");
        } catch (HubProbeRace.ProbeFailure failure) {
            assertFalse(HubFileRetryPolicy.shouldRetry(new HubFileRetryPolicy.DiscoveryFailure(failure), 0, true));
        }
    }

    @Test public void aSuccessfulProbeStillWinsOverOtherAddressFailures() throws Exception {
        HubProbeRace.Match<String> match = HubProbeRace.first(Arrays.asList("denied", "live"), host -> {
            if (host.equals("denied")) throw new HubFileRetryPolicy.HttpFailure(403, "denied");
            return host;
        }, () -> false, 1000);
        assertEquals("live", match.host);
    }
}
