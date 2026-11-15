package com.limelight.streamhub;

import android.view.KeyEvent;
import java.util.HashSet;
import java.util.Set;
import java.util.HashMap;
import java.util.Map;

/** Captures foreground physical Alt+Tab and Windows keys; ordinary keys pass through. */
public final class HubSystemShortcutKeys {
    public interface Sender {
        void step(boolean reverse); void finish(); default void taskView() {}
        default void windowsKey(boolean right, boolean down) {}
    }
    private final Set<Integer> tabs = new HashSet<>(), switching = new HashSet<>();
    private final Map<Integer,Integer> alts = new HashMap<>(), metas = new HashMap<>();
    public boolean handle(KeyEvent event, boolean active, Sender sender) {
        if (!active) { if(!switching.isEmpty() || !metas.isEmpty())sender.finish(); reset(); return false; }
        if (event.getDeviceId() <= 0 || (event.getFlags() & KeyEvent.FLAG_VIRTUAL_HARD_KEY) != 0) return false;
        int device=event.getDeviceId(), key=event.getKeyCode(), action=event.getAction();
        int metaBit=key==KeyEvent.KEYCODE_META_LEFT?1:key==KeyEvent.KEYCODE_META_RIGHT?2:0;
        if(metaBit!=0) {
            boolean wasHeld=metas.values().stream().anyMatch(value->(value&metaBit)!=0);
            int held=metas.getOrDefault(device,0);
            if(action==KeyEvent.ACTION_DOWN)held|=metaBit;else if(action==KeyEvent.ACTION_UP)held&=~metaBit;
            if(held==0)metas.remove(device);else metas.put(device,held);
            boolean nowHeld=metas.values().stream().anyMatch(value->(value&metaBit)!=0);
            if(wasHeld!=nowHeld)sender.windowsKey(metaBit==2,nowHeld);
            // Android starts its long-press shortcut overlay from Meta DOWN.
            // Consume the complete Windows-key sequence while remote input is active.
            return true;
        }
        int bit=key==KeyEvent.KEYCODE_ALT_LEFT?1:key==KeyEvent.KEYCODE_ALT_RIGHT?2:0;
        if(bit!=0) {
            int held=alts.getOrDefault(device,0);
            if(action==KeyEvent.ACTION_DOWN)held|=bit;else if(action==KeyEvent.ACTION_UP)held&=~bit;
            if(held==0)alts.remove(device);else alts.put(device,held);
            if(!switching.contains(device))return false;
            if(action==KeyEvent.ACTION_UP && held==0) {
                switching.remove(device);if(switching.isEmpty())sender.finish();
            }
            return true;
        }
        if(key!=KeyEvent.KEYCODE_TAB)return false;
        if(action==KeyEvent.ACTION_UP)return tabs.remove(device);
        if(action!=KeyEvent.ACTION_DOWN)return false;
        if(event.isMetaPressed() || metas.getOrDefault(device,0)!=0) {
            if(event.isCtrlPressed() || event.isAltPressed() || alts.getOrDefault(device,0)!=0 || event.isShiftPressed())return false;
            boolean first=tabs.add(device);
            if(first && event.getRepeatCount()==0)sender.taskView();
            return true;
        }
        if(!event.isAltPressed() && alts.getOrDefault(device,0)==0)return false;
        if(event.isCtrlPressed()||event.isMetaPressed())return false;
        tabs.add(device);switching.add(device);sender.step(event.isShiftPressed());
        return true;
    }
    public void reset() { tabs.clear();switching.clear();alts.clear();metas.clear(); }
}
