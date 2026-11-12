package com.limelight.streamhub;

import java.net.SocketException;
import org.junit.Test;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNotNull;
import static org.junit.Assert.assertNull;
import static org.junit.Assert.assertTrue;

public class HubFileRetryWindowTest {
    @Test public void confirmedProgressResetsFastRetryBudget() {
        HubFileRetryWindow window = new HubFileRetryWindow();
        assertEquals(1, window.afterFailure(new SocketException(), true, 0, 0, 0).fastRetry);
        assertEquals(2, window.afterFailure(new SocketException(), true, 0, 0, 1000).fastRetry);
        assertEquals(1, window.afterFailure(new SocketException(), true, 0, 1024, 2000).fastRetry);
    }

    @Test public void threeFailuresThenWaitForNetwork() {
        HubFileRetryWindow window = new HubFileRetryWindow();
        assertEquals(1, window.afterFailure(new SocketException(), true, 0, 0, 0).fastRetry);
        assertEquals(2, window.afterFailure(new SocketException(), true, 0, 0, 1000).fastRetry);
        assertEquals(3, window.afterFailure(new SocketException(), true, 0, 0, 3000).fastRetry);
        HubFileRetryWindow.Next waiting = window.afterFailure(new SocketException(), true, 0, 0, 7000);
        assertNotNull(waiting);
        assertTrue(waiting.waitingForNetwork());
        assertEquals(5000, waiting.delayMs);
    }

    @Test public void waitingWindowEndsAfterThirtySeconds() {
        HubFileRetryWindow window = new HubFileRetryWindow();
        for (int i = 0; i < 3; i++) window.afterFailure(new SocketException(), true, 0, 0, i * 1000L);
        window.afterFailure(new SocketException(), true, 0, 0, 3000);
        assertNull(window.afterFailure(new SocketException(), true, 0, 0, 33000));
    }
}
