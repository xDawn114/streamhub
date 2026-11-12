package com.limelight.streamhub;

import org.junit.Test;
import static org.junit.Assert.*;

public class HubUpdateInstallStateTest {
    private final long now = 1794441600000L;
    private String state(int previous, int target, String channel, long started, int current, String installedChannel, long at) {
        return HubUpdateInstallState.describe(previous, target, channel, started, current, installedChannel, at);
    }
    @Test public void launchAndCancelCannotBeCountedAsInstalled() {
        assertTrue(state(140,141,"internal",now,140,"internal",now).contains("尚未确认"));
        assertTrue(state(140,141,"internal",now,141,"internal",now).contains("已完成"));
    }
    @Test public void changedChannelAndStaleAttemptsAreIgnored() {
        assertEquals("",state(140,141,"internal",now,141,"stable",now));
        assertEquals("",state(140,141,"internal",now,142,"internal",now));
        assertEquals("",state(140,141,"internal",now,141,"internal",now+31L*86400000));
        assertEquals("",state(140,141,"internal",now+600000,141,"internal",now));
        assertEquals("",state(141,140,"internal",now,140,"internal",now));
    }
}
