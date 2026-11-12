package com.limelight.streamhub;

import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28, manifest=Config.NONE, application=android.app.Application.class)
public class HubHttpFailureTest {
    @Test public void statusSurvivesEmptyOrNonJsonErrorBodies() {
        for (String body : new String[]{"", "<html>unavailable</html>"}) {
            HubFileRetryPolicy.HttpFailure busy = HubControlClient.httpFailure(503, body, "请求失败");
            assertEquals(503, busy.status);
            assertTrue(HubFileRetryPolicy.shouldRetry(busy, 0, true));
            HubFileRetryPolicy.HttpFailure denied = HubControlClient.httpFailure(403, body, "请求失败");
            assertEquals(403, denied.status);
            assertFalse(HubFileRetryPolicy.shouldRetry(denied, 0, true));
        }
    }

    @Test public void structuredServerMessageRemainsAvailable() {
        assertEquals("permission denied", HubControlClient.httpFailure(403,
                "{\"message\":\"permission denied\"}", "请求失败").getMessage());
    }
}
