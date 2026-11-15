package com.limelight.streamhub;

import org.junit.Test;
import static org.junit.Assert.*;
import java.io.IOException;
import java.net.ConnectException;
import java.net.SocketTimeoutException;
import java.util.Arrays;
import javax.net.ssl.SSLHandshakeException;

public class HubTransportExplanationTest {
    @Test public void nestedTrustFailureTakesPriorityAndNeverEchoesSecret() {
        IOException error = new IOException("secret-token-and-private-address");
        error.addSuppressed(new SocketTimeoutException("unreachable-host"));
        error.addSuppressed(new SSLHandshakeException("private-certificate-details"));
        String message = HubTransportProbe.explanation(error);
        assertTrue(message.contains("身份校验"));
        assertFalse(message.contains("secret")); assertFalse(message.contains("private"));
    }
    @Test public void deniedReplyDoesNotMasqueradeAsNetworkFailure() {
        String message = HubTransportProbe.explanation(new HubFileRetryPolicy.HttpFailure(403,"do-not-echo"));
        assertTrue(message.contains("电脑已响应")); assertTrue(message.contains("授权"));
        assertFalse(message.contains("do-not-echo"));
    }
    @Test public void offlineReplyDoesNotClaimFirewallIsProvenCause() {
        assertTrue(HubTransportProbe.explanation(new ConnectException()).contains("暂未响应"));
        assertTrue(HubTransportProbe.explanation(new IOException()).contains("尚不能判断"));
    }
    @Test public void fallbackKeepsRouteEvidenceAndOriginalTrustErrors() throws Exception {
        try {
            HubTransportProbe.first(Arrays.asList("local-fixture","remote-fixture"), host -> {
                if(host.startsWith("local"))throw new ConnectException("192.0.2.10");
                throw new SSLHandshakeException("synthetic-secret");
            }, () -> false, 1000, host -> host.startsWith("local") ? "lan" : "remote");
            fail("must reject");
        } catch(IOException error) {
            assertTrue(error.getMessage().contains("局域网 / Wi-Fi"));
            assertTrue(error.getMessage().contains("异地网络"));
            assertEquals(2,error.getSuppressed().length);
            assertFalse(error.getMessage().contains("192.0.2.10"));
            assertFalse(HubFileRetryPolicy.shouldRetry(new HubFileRetryPolicy.DiscoveryFailure(error),0,true));
        }
    }
}
