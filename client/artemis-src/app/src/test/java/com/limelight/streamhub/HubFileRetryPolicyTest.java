package com.limelight.streamhub;

import org.junit.Test;
import java.io.EOFException;
import java.io.IOException;
import java.net.SocketException;
import java.net.SocketTimeoutException;
import javax.net.ssl.SSLHandshakeException;
import static org.junit.Assert.*;

public class HubFileRetryPolicyTest {
    @Test public void timeoutCanResumeKnownTask() { assertTrue(HubFileRetryPolicy.shouldRetry(new SocketTimeoutException(),0,true)); }
    @Test public void socketFailureCanResumeKnownTask() { assertTrue(HubFileRetryPolicy.shouldRetry(new SocketException(),1,true)); }
    @Test public void unexpectedEofCanResumeKnownTask() { assertTrue(HubFileRetryPolicy.shouldRetry(new EOFException(),0,true)); }
    @Test public void retryBudgetAllowsThreeRetries() {
        assertTrue(HubFileRetryPolicy.shouldRetry(new SocketTimeoutException(),2,true));
        assertFalse(HubFileRetryPolicy.shouldRetry(new SocketTimeoutException(),3,true));
    }
    @Test public void unknownUploadIsNotCreatedTwice() { assertFalse(HubFileRetryPolicy.shouldRetry(new SocketTimeoutException(),0,false)); }
    @Test public void discoveryBeforeTaskCanRetry() { assertTrue(HubFileRetryPolicy.shouldRetry(new HubFileRetryPolicy.DiscoveryFailure(new IOException("offline")),0,false)); }
    @Test public void tlsErrorNeverRetries() { assertFalse(HubFileRetryPolicy.shouldRetry(new SSLHandshakeException("pin"),0,true)); }
    @Test public void tlsErrorInsideDiscoveryNeverRetries() { assertFalse(HubFileRetryPolicy.shouldRetry(new HubFileRetryPolicy.DiscoveryFailure(new SSLHandshakeException("pin")),0,false)); }
    @Test public void authorizationFailureInsideDiscoveryNeverRetries() { assertFalse(HubFileRetryPolicy.shouldRetry(new HubFileRetryPolicy.DiscoveryFailure(new HubFileRetryPolicy.HttpFailure(403,"denied")),0,false)); }
    @Test public void conflictInsideDiscoveryNeverRetries() { assertFalse(HubFileRetryPolicy.shouldRetry(new HubFileRetryPolicy.DiscoveryFailure(new HubFileRetryPolicy.HttpFailure(409,"conflict")),0,false)); }
    @Test public void authorizationNeverRetries() { assertFalse(HubFileRetryPolicy.shouldRetry(new HubFileRetryPolicy.HttpFailure(403,"denied"),0,true)); }
    @Test public void conflictingOffsetNeverBlindlyRetries() { assertFalse(HubFileRetryPolicy.shouldRetry(new HubFileRetryPolicy.HttpFailure(409,"conflict"),0,true)); }
    @Test public void checksumErrorNeverRetries() { assertFalse(HubFileRetryPolicy.shouldRetry(new HubFileRetryPolicy.HttpFailure(422,"bad hash"),0,true)); }
    @Test public void serverTimeoutCanResume() { assertTrue(HubFileRetryPolicy.shouldRetry(new HubFileRetryPolicy.HttpFailure(504,"timeout"),0,true)); }
    @Test public void busyServerCanResume() { assertTrue(HubFileRetryPolicy.shouldRetry(new HubFileRetryPolicy.HttpFailure(429,"busy"),0,true)); }
    @Test public void diskErrorIsNotRetried() { assertFalse(HubFileRetryPolicy.shouldRetry(new IOException("disk"),0,true)); }
    @Test public void delaysUseBoundedExponentialBackoff() {
        assertEquals(1000,HubFileRetryPolicy.delayMs(0));
        assertEquals(2000,HubFileRetryPolicy.delayMs(1));
        assertEquals(4000,HubFileRetryPolicy.delayMs(2));
        assertEquals(8000,HubFileRetryPolicy.delayMs(3));
    }
}
