package com.limelight.streamhub;

import com.limelight.nvstream.input.KeyboardPacket;

/** Owns a balanced host Alt key while the remote window switcher is open. */
public final class HubAltTabSession {
    public interface Sender { void key(short key, byte direction, byte modifiers, byte flags); }
    private final Sender sender;
    private boolean active;
    public HubAltTabSession(Sender sender) { this.sender=sender; }
    private void key(short key,byte action,byte modifiers) { sender.key(key,action,modifiers,(byte)0); }
    public void step(boolean reverse) {
        if(!active) {
            HubWindowSwitchShortcut.releaseHostModifiers((key,flags)->sender.key(key,KeyboardPacket.KEY_UP,(byte)0,flags));
            key((short)0xA4,KeyboardPacket.KEY_DOWN,(byte)0);active=true;
        }
        byte modifiers=KeyboardPacket.MODIFIER_ALT;
        if(reverse){key((short)0xA0,KeyboardPacket.KEY_DOWN,modifiers);modifiers|=KeyboardPacket.MODIFIER_SHIFT;}
        key((short)9,KeyboardPacket.KEY_DOWN,modifiers);
        key((short)9,KeyboardPacket.KEY_UP,modifiers);
        if(reverse)key((short)0xA0,KeyboardPacket.KEY_UP,KeyboardPacket.MODIFIER_ALT);
    }
    public void finish() {
        if(!active)return;active=false;
        for(short key:new short[]{(short)0xA4,(short)0xA5}) {
            key(key,KeyboardPacket.KEY_UP,(byte)0);
            sender.key(key,KeyboardPacket.KEY_UP,(byte)0,com.limelight.nvstream.jni.MoonBridge.SS_KBE_FLAG_NON_NORMALIZED);
        }
    }
}
