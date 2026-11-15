package com.limelight.streamhub;

import com.limelight.utils.HelpUrlPolicy;
import org.junit.Test;
import static org.junit.Assert.*;

public class HubHelpPolicyTest {
    @Test public void httpsLinksAndAnchorsRemainUsable() {
        assertTrue(HelpUrlPolicy.allowed("https://github.com/moonlight-stream/moonlight-docs/wiki/Setup-Guide#network"));
        assertTrue(HelpUrlPolicy.allowed("https://example.org:443/help?topic=connect"));
    }
    @Test public void helpCannotNavigateToLocalDataOrExternalIntentHandlers() {
        for (String uri : new String[]{null,"", "http://example.org/help", "javascript:alert(1)",
                "content://com.example.private/secret", "file:///data/data/app/secret", "intent://launch",
                "data:text/html,test", "https://user:password@example.org/help", "https://example.org:8080/",
                "https:/invalid", "https://example.org/\nheader"})
            assertFalse(String.valueOf(uri), HelpUrlPolicy.allowed(uri));
    }
}
