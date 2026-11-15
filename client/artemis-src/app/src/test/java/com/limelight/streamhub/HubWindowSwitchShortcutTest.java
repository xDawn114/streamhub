package com.limelight.streamhub;

import android.view.KeyEvent;
import android.content.Intent;
import com.limelight.Game;
import com.limelight.binding.input.ControllerHandler;
import com.limelight.binding.input.KeyboardTranslator;
import com.limelight.nvstream.NvConnection;
import com.limelight.nvstream.input.KeyboardPacket;
import com.limelight.preferences.PreferenceConfiguration;
import com.limelight.ui.StreamView;
import java.util.ArrayList;
import java.util.List;
import java.util.HashSet;
import java.util.Set;
import com.limelight.nvstream.jni.MoonBridge;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.Robolectric;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.util.ReflectionHelpers;
import org.robolectric.annotation.Config;
import static org.junit.Assert.*;
import static org.mockito.Mockito.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28, manifest=Config.NONE, application=android.app.Application.class)
public class HubWindowSwitchShortcutTest {
    private static final int CHORD = KeyEvent.META_CTRL_ON | KeyEvent.META_ALT_ON;
    private KeyEvent event(int action, int key, int meta, int repeat, int device) {
        return new KeyEvent(1, 2, action, key, repeat, meta, device, 0);
    }
    @Test public void streamViewPreImeRoutesShortcutBeforeRawKeyboardForwarding() {
        Game game = spy(Robolectric.buildActivity(Game.class,
                new Intent().putExtra("HubManaged", true)).get());
        NvConnection connection = mock(NvConnection.class);
        KeyboardTranslator translator = mock(KeyboardTranslator.class);
        when(translator.translate(anyInt(), anyInt(), anyInt())).thenAnswer(call -> {
            int key = call.getArgument(0);
            return (short) (0x8000 | (key == KeyEvent.KEYCODE_CTRL_LEFT ? 0xA2
                    : key == KeyEvent.KEYCODE_ALT_LEFT ? 0xA4 : 9));
        });
        ReflectionHelpers.setField(game, "prefConfig", new PreferenceConfiguration());
        ReflectionHelpers.setField(game, "connected", true);
        ReflectionHelpers.setField(game, "conn", connection);
        ReflectionHelpers.setField(game, "keyboardTranslator", translator);
        ReflectionHelpers.setField(game, "controllerHandler", mock(ControllerHandler.class));
        doNothing().when(game).sendKeys(any(short[].class));
        StreamView view = new StreamView(RuntimeEnvironment.getApplication());
        view.setInputCallbacks(game);
        for (int key : new int[] {KeyEvent.KEYCODE_CTRL_LEFT, KeyEvent.KEYCODE_ALT_LEFT,
                KeyEvent.KEYCODE_TAB})
            assertTrue(view.onKeyPreIme(key, event(KeyEvent.ACTION_DOWN, key, 0, 0, -1)));
        assertTrue(view.onKeyPreIme(KeyEvent.KEYCODE_TAB,
                event(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_TAB, 0, 0, -1)));
        verify(game).sendKeys(argThat(keys -> java.util.Arrays.equals(keys,
                new short[] {KeyboardTranslator.VK_LMENU, KeyboardTranslator.VK_TAB})));
        verify(connection, never()).sendKeyboardInput(eq((short) 0x8009),
                eq(KeyboardPacket.KEY_DOWN), anyByte(), anyByte());
    }
    @Test public void separateModifierEventsWorkWhenTabOmitsMetaState() {
        HubWindowSwitchShortcut keys = new HubWindowSwitchShortcut();
        List<Boolean> sent = new ArrayList<>();
        for (int key : new int[] {KeyEvent.KEYCODE_CTRL_LEFT, KeyEvent.KEYCODE_ALT_LEFT})
            assertFalse(keys.handle(event(KeyEvent.ACTION_DOWN, key, 0, 0, -1), sent::add));
        assertTrue(keys.handle(event(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_TAB, 0, 0, -1), sent::add));
        assertTrue(keys.handle(event(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_TAB, 0, 0, -1), sent::add));
        for (int key : new int[] {KeyEvent.KEYCODE_ALT_LEFT, KeyEvent.KEYCODE_CTRL_LEFT})
            assertFalse(keys.handle(event(KeyEvent.ACTION_UP, key, 0, 0, -1), sent::add));
        assertFalse(keys.handle(event(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_TAB, 0, 0, -1), sent::add));
        assertEquals(java.util.Collections.singletonList(false), sent);
        keys.handle(event(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_CTRL_LEFT, 0, 0, -1), sent::add);
        keys.reset();
        keys.handle(event(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_ALT_LEFT, 0, 0, -1), sent::add);
        assertFalse(keys.handle(event(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_TAB, 0, 0, -1), sent::add));
    }
    @Test public void switchingReleasesKeysInBothHostTrackingModes() {
        Set<String> held = new HashSet<>();
        held.add("162:0"); // A normalized Ctrl event from a mapped keyboard.
        held.add("164:" + MoonBridge.SS_KBE_FLAG_NON_NORMALIZED); // Raw Alt.
        held.add("161:" + MoonBridge.SS_KBE_FLAG_NON_NORMALIZED); // Reverse chord.
        held.add("65:0"); // An unrelated key must remain untouched.
        HubWindowSwitchShortcut.releaseHostModifiers((key, flags) -> held.remove(key + ":" + flags));
        assertEquals(java.util.Collections.singleton("65:0"), held);
    }
    @Test public void normalTabAndSystemAltTabRemainUntouched() {
        HubWindowSwitchShortcut keys = new HubWindowSwitchShortcut();
        List<Boolean> sent = new ArrayList<>();
        for (int meta : new int[] {0, KeyEvent.META_ALT_ON, KeyEvent.META_CTRL_ON, CHORD | KeyEvent.META_META_ON})
            assertFalse(keys.handle(event(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_TAB, meta, 0, 3), sent::add));
        assertTrue(sent.isEmpty());
    }
    @Test public void switchFiresOnceAndReleaseIsConsumedAfterModifiersComeUp() {
        HubWindowSwitchShortcut keys = new HubWindowSwitchShortcut();
        List<Boolean> sent = new ArrayList<>();
        assertTrue(keys.handle(event(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_TAB, CHORD, 0, 3), sent::add));
        assertTrue(keys.handle(event(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_TAB, CHORD, 1, 3), sent::add));
        assertTrue(keys.handle(event(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_TAB, 0, 0, 3), sent::add));
        assertEquals(java.util.Arrays.asList(false), sent);
        assertFalse(keys.handle(event(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_TAB, 0, 0, 3), sent::add));
    }
    @Test public void shiftReversesAndTheNextPressCanSwitchAgain() {
        HubWindowSwitchShortcut keys = new HubWindowSwitchShortcut();
        List<Boolean> sent = new ArrayList<>();
        keys.handle(event(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_TAB, CHORD | KeyEvent.META_SHIFT_ON, 0, 4), sent::add);
        keys.handle(event(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_TAB, 0, 0, 4), sent::add);
        keys.handle(event(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_TAB, CHORD, 0, 4), sent::add);
        assertEquals(java.util.Arrays.asList(true, false), sent);
    }
    @Test public void focusLossAndAnotherKeyboardCannotLeaveTabLatched() {
        HubWindowSwitchShortcut keys = new HubWindowSwitchShortcut();
        List<Boolean> sent = new ArrayList<>();
        keys.handle(event(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_TAB, CHORD, 0, 3), sent::add);
        assertFalse(keys.handle(event(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_TAB, 0, 0, 4), sent::add));
        keys.reset();
        assertFalse(keys.handle(event(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_TAB, 0, 0, 3), sent::add));
        keys.handle(event(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_TAB, CHORD, 0, 3), sent::add);
        assertEquals(2, sent.size());
    }
}
