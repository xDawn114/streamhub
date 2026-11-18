package com.limelight.streamhub;

import org.junit.Test;
import java.util.*;
import static org.junit.Assert.*;

public class HubInputGateTest {
    @Test public void viewOnlyReleasesHeldInputsAndDropsQueuedAndNewPackets(){
        HubInputGate gate=new HubInputGate();List<String> sent=new ArrayList<>();
        gate.press("key",()->sent.add("down"),()->sent.add("up"));
        gate.press("mouse",()->sent.add("mouse-down"),()->sent.add("mouse-up"));
        gate.contact("touch",false,()->{sent.add("touch-down");return 0;},()->sent.add("touch-cancel"));
        gate.setEnabled(false);
        gate.run(()->sent.add("late-text"));gate.press("new",()->sent.add("new-down"),()->sent.add("new-up"));
        gate.release("key",()->sent.add("late-up"));assertEquals(0,gate.call(()->{sent.add("late-contact");return -1;}));
        assertEquals(Arrays.asList("down","mouse-down","touch-down","up","mouse-up","touch-cancel"),sent);
        gate.setEnabled(true);gate.run(()->sent.add("resumed"));assertEquals("resumed",sent.get(sent.size()-1));
    }
    @Test public void finishedContactsAndReleasedKeysDoNotStickAcrossToggles(){
        HubInputGate gate=new HubInputGate();int[] releases={0};
        gate.press("key",()->{},()->releases[0]++);gate.release("key",()->releases[0]++);
        gate.contact("touch",false,()->0,()->releases[0]++);gate.contact("touch",true,()->0,()->releases[0]++);
        gate.setEnabled(false);gate.setEnabled(false);assertEquals(1,releases[0]);
    }
    @Test public void releaseFailureStillStopsInputAndTriesAllOtherReleases(){
        HubInputGate gate=new HubInputGate();int[] released={0};
        gate.press("bad",()->{},()->{throw new IllegalStateException("fixture");});
        gate.press("other",()->{},()->released[0]++);
        try{gate.setEnabled(false);fail();}catch(IllegalStateException expected){}
        assertFalse(gate.enabled());assertEquals(1,released[0]);gate.run(()->fail("input escaped view-only"));
    }
}
