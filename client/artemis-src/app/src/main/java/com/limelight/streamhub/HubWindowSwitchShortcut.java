package com.limelight.streamhub;

import android.view.KeyEvent;
import com.limelight.nvstream.jni.MoonBridge;
import java.util.HashSet;
import java.util.Set;
import java.util.Map;
import java.util.HashMap;

/** App-local alternative to Android's reserved Alt+Tab. No global keyboard hook. */
public final class HubWindowSwitchShortcut {
    public interface Sender { void switchWindow(boolean reverse); }
    public interface ReleaseSender { void keyUp(short key, byte flags); }
    private final Set<Integer> heldTabs = new HashSet<>();
    private final Map<Integer, Integer> heldModifiers = new HashMap<>();

    public boolean handle(KeyEvent event, Sender sender) {
        int device = event.getDeviceId();
        int modifier = modifierFor(event.getKeyCode());
        if (modifier != 0) {
            int held = heldModifiers.getOrDefault(device, 0);
            if (event.getAction() == KeyEvent.ACTION_DOWN) held |= modifier;
            else if (event.getAction() == KeyEvent.ACTION_UP) held &= ~modifier;
            if (held == 0) heldModifiers.remove(device); else heldModifiers.put(device, held);
            return false; // Preserve ordinary modifier forwarding to the host.
        }
        if (event.getKeyCode() != KeyEvent.KEYCODE_TAB) return false;
        if (event.getAction() == KeyEvent.ACTION_UP) return heldTabs.remove(device);
        if (event.getAction() != KeyEvent.ACTION_DOWN) return false;
        if (heldTabs.contains(device)) return true;
        // Some IMEs/OEM injected keyboards send modifier key events but omit
        // their metaState from Tab. Keep per-device state as a fallback.
        int meta = event.getMetaState() | heldModifiers.getOrDefault(device, 0);
        if ((meta & KeyEvent.META_CTRL_MASK) == 0 || (meta & KeyEvent.META_ALT_MASK) == 0 ||
                (meta & KeyEvent.META_META_MASK) != 0) return false;
        heldTabs.add(device);
        if (event.getRepeatCount() == 0) sender.switchWindow((meta & KeyEvent.META_SHIFT_MASK) != 0);
        return true;
    }
    public void reset() { heldTabs.clear(); heldModifiers.clear(); }

    private static int modifierFor(int key) {
        switch (key) {
            case KeyEvent.KEYCODE_CTRL_LEFT: return KeyEvent.META_CTRL_LEFT_ON;
            case KeyEvent.KEYCODE_CTRL_RIGHT: return KeyEvent.META_CTRL_RIGHT_ON;
            case KeyEvent.KEYCODE_ALT_LEFT: return KeyEvent.META_ALT_LEFT_ON;
            case KeyEvent.KEYCODE_ALT_RIGHT: return KeyEvent.META_ALT_RIGHT_ON;
            case KeyEvent.KEYCODE_SHIFT_LEFT: return KeyEvent.META_SHIFT_LEFT_ON;
            case KeyEvent.KEYCODE_SHIFT_RIGHT: return KeyEvent.META_SHIFT_RIGHT_ON;
            case KeyEvent.KEYCODE_META_LEFT: return KeyEvent.META_META_LEFT_ON;
            case KeyEvent.KEYCODE_META_RIGHT: return KeyEvent.META_META_RIGHT_ON;
            default: return 0;
        }
    }

    public static void releaseHostModifiers(ReleaseSender sender) {
        // Sunshine tracks a held key by both its virtual key and packet flags.
        // Physical/ADB keyboard events can use NON_NORMALIZED while menu chords
        // use normalized events. Raising only flags=0 leaves the original Ctrl
        // held and turns the intended Alt+Tab into Windows Ctrl+Alt+Tab.
        for (short key : new short[] {0xA2, 0xA3, 0xA4, 0xA5, 0xA0, 0xA1}) {
            sender.keyUp(key, (byte) 0);
            sender.keyUp(key, MoonBridge.SS_KBE_FLAG_NON_NORMALIZED);
        }
    }
}
