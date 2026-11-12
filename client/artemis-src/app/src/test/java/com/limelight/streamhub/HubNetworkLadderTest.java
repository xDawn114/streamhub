package com.limelight.streamhub;

import org.junit.Test;
import static org.junit.Assert.*;

public class HubNetworkLadderTest {
    private static int window(HubNetworkLadder p, long start, int lost, int total, int rtt) {
        p.sample(start, lost, total, rtt);
        return p.sample(start + 5000, 0, 0, 0);
    }
    @Test public void startsAtConfiguredRate() { assertEquals(40000, new HubNetworkLadder(40000).recommendationKbps()); }
    @Test public void customRateIsNotRoundedUp() { assertEquals(24000, new HubNetworkLadder(24000).recommendationKbps()); }
    @Test public void lowRateIsNotRaisedToFloor() { assertEquals(6000, new HubNetworkLadder(6000).recommendationKbps()); }
    @Test(expected = IllegalArgumentException.class) public void rejectsZeroRate() { new HubNetworkLadder(0); }
    @Test(expected = IllegalArgumentException.class) public void rejectsNegativeSamples() { new HubNetworkLadder(40000).sample(1, -1, 10, 20); }
    @Test(expected = IllegalArgumentException.class) public void rejectsImpossibleLoss() { new HubNetworkLadder(40000).sample(1, 11, 10, 20); }
    @Test public void holdsUntilFiveSeconds() { HubNetworkLadder p=new HubNetworkLadder(40000); p.sample(0, 10, 100, 20); assertEquals(40000,p.sample(4999, 0, 0, 0)); }
    @Test public void severeLossLowersOneStep() { assertEquals(25000,window(new HubNetworkLadder(40000),0,8,100,30)); }
    @Test public void severeRttLowersOneStep() { assertEquals(25000,window(new HubNetworkLadder(40000),0,0,100,300)); }
    @Test public void dynamicStage53SampleDoesNotMarkNetworkDegraded() {
        HubNetworkLadder p = new HubNetworkLadder(24000);
        p.sample(0, 3, 174, 16);
        p.sample(5000, 0, 0, 0);
        assertFalse(p.isNetworkDegraded());
    }
    @Test public void oneModerateWindowDoesNotMarkNetworkDegraded() {
        HubNetworkLadder p = new HubNetworkLadder(40000);
        window(p, 0, 2, 100, 30);
        assertFalse(p.isNetworkDegraded());
    }
    @Test public void repeatedModerateWindowsMarkNetworkDegraded() {
        HubNetworkLadder p = new HubNetworkLadder(40000);
        window(p, 0, 2, 100, 30);
        window(p, 5000, 2, 100, 30);
        assertTrue(p.isNetworkDegraded());
    }
    @Test public void severeWindowMarksNetworkDegradedImmediately() {
        HubNetworkLadder p = new HubNetworkLadder(40000);
        window(p, 0, 8, 100, 30);
        assertTrue(p.isNetworkDegraded());
    }
    @Test public void goodWindowsClearNetworkDegradedState() {
        HubNetworkLadder p = new HubNetworkLadder(40000);
        window(p, 0, 8, 100, 30);
        window(p, 5000, 0, 100, 30);
        assertTrue(p.isNetworkDegraded());
        window(p, 10000, 0, 100, 30);
        assertFalse(p.isNetworkDegraded());
    }
    @Test public void moderateWindowDoesNotImmediatelyLower() { assertEquals(40000,window(new HubNetworkLadder(40000),0,2,100,30)); }
    @Test public void twoModerateWindowsLower() { HubNetworkLadder p=new HubNetworkLadder(40000);window(p,0,2,100,30);assertEquals(25000,window(p,5000,2,100,30)); }
    @Test public void rttAlsoCountsAsModerate() { HubNetworkLadder p=new HubNetworkLadder(40000);window(p,0,0,100,150);assertEquals(25000,window(p,5000,0,100,150)); }
    @Test public void goodWindowBreaksBadSequence() { HubNetworkLadder p=new HubNetworkLadder(40000);window(p,0,2,100,30);window(p,5000,0,100,30);assertEquals(40000,window(p,10000,2,100,30)); }
    @Test public void repeatedSevereWindowsReachFloor() { HubNetworkLadder p=new HubNetworkLadder(40000);for(int i=0;i<5;i++)window(p,i*5000L,10,100,30);assertEquals(8000,p.recommendationKbps()); }
    @Test public void customRateUsesNextLowerRung() { assertEquals(15000,window(new HubNetworkLadder(24000),0,10,100,30)); }
    @Test public void fiveGoodWindowsDoNotRaise() { HubNetworkLadder p=new HubNetworkLadder(40000);window(p,0,10,100,30);for(int i=1;i<=5;i++)window(p,i*5000L,0,100,30);assertEquals(25000,p.recommendationKbps()); }
    @Test public void sixGoodWindowsRaiseOneStep() { HubNetworkLadder p=new HubNetworkLadder(40000);window(p,0,10,100,30);for(int i=1;i<=6;i++)window(p,i*5000L,0,100,30);assertEquals(40000,p.recommendationKbps()); }
    @Test public void recoveryNeverExceedsCustomRate() { HubNetworkLadder p=new HubNetworkLadder(24000);window(p,0,10,100,30);for(int i=1;i<=12;i++)window(p,i*5000L,0,100,30);assertEquals(24000,p.recommendationKbps()); }
    @Test public void missingMetricsDoNotCauseRecovery() { HubNetworkLadder p=new HubNetworkLadder(40000);window(p,0,10,100,30);for(int i=1;i<=8;i++)window(p,i*5000L,0,0,0);assertEquals(25000,p.recommendationKbps()); }
    @Test public void clockRollbackClearsPartialWindow() { HubNetworkLadder p=new HubNetworkLadder(40000);p.sample(10000,10,100,30);p.sample(9000,0,100,30);assertEquals(40000,p.sample(14000,0,0,0)); }
    @Test public void longGapClearsPartialWindow() { HubNetworkLadder p=new HubNetworkLadder(40000);p.sample(0,10,100,30);p.sample(11000,0,100,30);assertEquals(40000,p.sample(16000,0,0,0)); }
    @Test public void mixedFrameSamplesUseWeightedLoss() { HubNetworkLadder p=new HubNetworkLadder(40000);p.sample(0,8,10,30);p.sample(1000,0,990,30);assertEquals(40000,p.sample(5000,0,0,0)); }
    @Test public void separateSessionsDoNotShareState() { HubNetworkLadder a=new HubNetworkLadder(40000),b=new HubNetworkLadder(40000);window(a,0,10,100,30);assertEquals(40000,b.recommendationKbps()); }
}
