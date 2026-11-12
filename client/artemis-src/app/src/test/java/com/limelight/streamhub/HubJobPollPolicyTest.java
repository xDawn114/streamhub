package com.limelight.streamhub;

import org.junit.Test;
import static org.junit.Assert.*;

public class HubJobPollPolicyTest {
    @Test public void fastJobsAreCheckedBeforeTheOldHalfSecondWait() {
        long elapsed = 0;
        for (int pending = 1; pending <= 3; pending++) {
            elapsed += HubJobPollPolicy.delayAfterPending(pending);
        }
        assertEquals(450, elapsed);
    }

    @Test public void longPreparationsUseBoundedControlTraffic() {
        long elapsed = 0;
        int requests = 1;
        while (elapsed + HubJobPollPolicy.delayAfterPending(requests) < 60000) {
            elapsed += HubJobPollPolicy.delayAfterPending(requests);
            requests++;
        }
        assertEquals(123, requests);
        assertTrue("reserve room for other authenticated control requests", requests < 360);
        assertEquals(500, HubJobPollPolicy.delayAfterPending(100));
    }
}
