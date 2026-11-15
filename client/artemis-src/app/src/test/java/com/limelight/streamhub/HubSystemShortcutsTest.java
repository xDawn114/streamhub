package com.limelight.streamhub;

import android.content.Context;
import android.view.KeyEvent;
import java.util.ArrayList;
import java.util.List;
import org.junit.After;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.annotation.Config;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28, manifest=Config.NONE, application=android.app.Application.class)
public class HubSystemShortcutsTest {
    private HubSystemShortcutKeys.Sender sender(List<Boolean> sent) { return new HubSystemShortcutKeys.Sender() {
        public void step(boolean reverse){sent.add(reverse);}public void finish(){}
    }; }
    private final Context context = RuntimeEnvironment.getApplication();
    private KeyEvent tab(int action, int meta, int repeat, int device) {
        return new KeyEvent(1, 2, action, KeyEvent.KEYCODE_TAB, repeat, meta, device, 0);
    }
    @After public void cleanup() { HubSystemShortcuts.request(context, false); }
    @Test public void defaultsOffAndDeactivationImmediatelyReturnsKeysToSystem() {
        HubSystemShortcuts.request(context, false);
        assertFalse(HubSystemShortcuts.requested(context));
        List<Boolean> sent = new ArrayList<>();
        HubSystemShortcuts.Target target = new HubSystemShortcuts.Target() {
            public boolean isActive() { return true; }
            public void switchWindow(boolean reverse) { sent.add(reverse); }
        };
        HubSystemShortcuts.activate(target);
        assertFalse(HubSystemShortcuts.dispatch(context, tab(0, KeyEvent.META_ALT_ON, 0, 3)));
        HubSystemShortcuts.request(context, true);
        assertTrue(HubSystemShortcuts.dispatch(context, tab(0, KeyEvent.META_ALT_ON, 0, 3)));
        HubSystemShortcuts.deactivate(target);
        assertFalse(HubSystemShortcuts.dispatch(context, tab(1, 0, 0, 3)));
        assertFalse(HubSystemShortcuts.dispatch(context, tab(0, KeyEvent.META_ALT_ON, 0, 3)));
        assertEquals(java.util.Collections.singletonList(false), sent);
    }
    @Test public void connectedButInactiveTargetNeverCaptures() {
        HubSystemShortcuts.request(context, true);
        HubSystemShortcuts.Target target = new HubSystemShortcuts.Target() {
            public boolean isActive() { return false; }
            public void switchWindow(boolean reverse) { fail("background target was invoked"); }
        };
        HubSystemShortcuts.activate(target);
        assertFalse(HubSystemShortcuts.dispatch(context, tab(0, KeyEvent.META_ALT_ON, 0, 3)));
        HubSystemShortcuts.deactivate(target);
    }
    @Test public void onlyPhysicalAltTabIsCapturedAndRepeatContinuesSelection() {
        HubSystemShortcutKeys keys = new HubSystemShortcutKeys(); List<Boolean> sent = new ArrayList<>();
        for (int meta : new int[] {0, KeyEvent.META_CTRL_ON, KeyEvent.META_ALT_ON|KeyEvent.META_CTRL_ON, KeyEvent.META_ALT_ON|KeyEvent.META_META_ON})
            assertFalse(keys.handle(tab(0, meta, 0, 3), true, sender(sent)));
        assertFalse(keys.handle(tab(0, KeyEvent.META_ALT_ON, 0, -1), true, sender(sent)));
        assertFalse(keys.handle(new KeyEvent(1,2,0,KeyEvent.KEYCODE_A,0,KeyEvent.META_ALT_ON,3,0),true,sender(sent)));
        assertTrue(keys.handle(tab(0, KeyEvent.META_ALT_ON|KeyEvent.META_SHIFT_ON, 0, 3), true, sender(sent)));
        assertTrue(keys.handle(tab(0, KeyEvent.META_ALT_ON, 1, 3), true, sender(sent)));
        assertFalse(keys.handle(tab(1, 0, 0, 4), true, sender(sent)));
        assertTrue(keys.handle(tab(1, 0, 0, 3), true, sender(sent)));
        assertEquals(java.util.Arrays.asList(true,false), sent);
        assertFalse(keys.handle(tab(1, 0, 0, 3), true, sender(sent)));
    }
    @Test public void altReleaseFinishesSelectionAndFocusLossCancelsIt() {
        HubSystemShortcutKeys keys=new HubSystemShortcutKeys();List<String> events=new ArrayList<>();
        HubSystemShortcutKeys.Sender sender=new HubSystemShortcutKeys.Sender(){
            public void step(boolean reverse){events.add(reverse?"reverse":"step");}public void finish(){events.add("finish");}
        };
        assertFalse(keys.handle(new KeyEvent(1,2,0,KeyEvent.KEYCODE_ALT_LEFT,0,0,3,0),true,sender));
        assertTrue(keys.handle(tab(0,KeyEvent.META_ALT_ON,0,3),true,sender));
        assertTrue(keys.handle(tab(1,KeyEvent.META_ALT_ON,0,3),true,sender));
        assertEquals(java.util.Collections.singletonList("step"),events);
        assertFalse(keys.handle(new KeyEvent(1,2,1,KeyEvent.KEYCODE_ALT_LEFT,0,0,4,0),true,sender));
        assertTrue(keys.handle(new KeyEvent(1,2,1,KeyEvent.KEYCODE_ALT_LEFT,0,0,3,0),true,sender));
        assertEquals(java.util.Arrays.asList("step","finish"),events);
        keys.handle(tab(0,KeyEvent.META_ALT_ON,0,3),true,sender);
        assertFalse(keys.handle(tab(1,0,0,3),false,sender));
        assertEquals(java.util.Arrays.asList("step","finish","step","finish"),events);
    }
    @Test public void disabledMidChordClearsTracking() {
        HubSystemShortcutKeys keys = new HubSystemShortcutKeys(); List<Boolean> sent = new ArrayList<>();
        assertTrue(keys.handle(tab(0, KeyEvent.META_ALT_ON, 0, 3),true,sender(sent)));
        assertFalse(keys.handle(tab(1, 0, 0, 3),false,sender(sent)));
        assertFalse(keys.handle(tab(1, 0, 0, 3),true,sender(sent)));
        assertTrue(keys.handle(tab(0, KeyEvent.META_ALT_ON, 0, 3),true,sender(sent)));
        assertEquals(2,sent.size());
    }
    @Test public void winTabUsesPhysicalMetaStateAndDoesNotToggleOnKeyRepeat() {
        HubSystemShortcutKeys keys=new HubSystemShortcutKeys();List<String> sent=new ArrayList<>();
        HubSystemShortcutKeys.Sender target=new HubSystemShortcutKeys.Sender(){
            public void step(boolean reverse){fail("Alt path used for Win+Tab");}
            public void finish(){} public void taskView(){sent.add("task-view");}
        };
        assertTrue(keys.handle(new KeyEvent(1,2,0,KeyEvent.KEYCODE_META_RIGHT,0,0,3,0),true,target));
        assertFalse(keys.handle(tab(0,0,0,4),true,target));
        assertTrue(keys.handle(tab(0,0,0,3),true,target));
        assertTrue(keys.handle(tab(0,0,1,3),true,target));
        assertTrue(keys.handle(tab(1,0,0,3),true,target));
        assertTrue(keys.handle(new KeyEvent(1,2,1,KeyEvent.KEYCODE_META_RIGHT,0,0,3,0),true,target));
        assertEquals(java.util.Collections.singletonList("task-view"),sent);
        assertFalse(keys.handle(tab(0,0,0,3),true,target));
        assertTrue(keys.handle(tab(0,KeyEvent.META_META_ON,0,3),true,target));
        assertEquals(2,sent.size());
    }
    @Test public void winTabUsesTheSameOptInAndForegroundGate() {
        int[] sent={0};HubSystemShortcuts.Target target=new HubSystemShortcuts.Target(){
            public boolean isActive(){return true;}public void switchWindow(boolean reverse){fail("wrong target");}
            public void showTaskView(){sent[0]++;}
        };
        HubSystemShortcuts.request(context,false);HubSystemShortcuts.activate(target);
        assertFalse(HubSystemShortcuts.dispatch(context,tab(0,KeyEvent.META_META_ON,0,3)));
        HubSystemShortcuts.request(context,true);
        for(int modifier:new int[]{KeyEvent.META_CTRL_ON,KeyEvent.META_ALT_ON,KeyEvent.META_SHIFT_ON})
            assertFalse(HubSystemShortcuts.dispatch(context,tab(0,KeyEvent.META_META_ON|modifier,0,3)));
        assertFalse(HubSystemShortcuts.dispatch(context,tab(0,KeyEvent.META_META_ON,0,-1)));
        assertTrue(HubSystemShortcuts.dispatch(context,tab(0,KeyEvent.META_META_ON,0,3)));
        HubSystemShortcuts.deactivate(target);
        assertFalse(HubSystemShortcuts.dispatch(context,tab(0,KeyEvent.META_META_ON,0,3)));
        assertEquals(1,sent[0]);
    }
    @Test public void windowsLongPressIsConsumedFromDownAndReleaseIsBalancedAcrossKeyboards() {
        HubSystemShortcutKeys keys=new HubSystemShortcutKeys();List<String> sent=new ArrayList<>();
        HubSystemShortcutKeys.Sender target=new HubSystemShortcutKeys.Sender(){
            public void step(boolean reverse){}public void finish(){sent.add("finish");}
            public void windowsKey(boolean right,boolean down){sent.add(right+":"+down);}
        };
        assertTrue(keys.handle(new KeyEvent(1,2,0,KeyEvent.KEYCODE_META_LEFT,0,0,3,0),true,target));
        assertTrue(keys.handle(new KeyEvent(1,2,0,KeyEvent.KEYCODE_META_LEFT,2,0,3,0),true,target));
        assertTrue(keys.handle(new KeyEvent(1,2,0,KeyEvent.KEYCODE_META_LEFT,0,0,4,0),true,target));
        assertTrue(keys.handle(new KeyEvent(1,2,1,KeyEvent.KEYCODE_META_LEFT,0,0,3,0),true,target));
        assertEquals(java.util.Collections.singletonList("false:true"),sent);
        assertFalse(keys.handle(new KeyEvent(1,2,0,KeyEvent.KEYCODE_D,0,KeyEvent.META_META_ON,4,0),true,target));
        assertTrue(keys.handle(new KeyEvent(1,2,1,KeyEvent.KEYCODE_META_LEFT,0,0,4,0),true,target));
        assertEquals(java.util.Arrays.asList("false:true","false:false"),sent);
        assertTrue(keys.handle(new KeyEvent(1,2,0,KeyEvent.KEYCODE_META_RIGHT,0,0,3,0),true,target));
        assertFalse(keys.handle(tab(0,0,0,3),false,target));
        assertEquals("finish",sent.get(sent.size()-1));
        assertFalse(keys.handle(new KeyEvent(1,2,0,KeyEvent.KEYCODE_META_LEFT,0,0,3,0),false,target));
    }
}
