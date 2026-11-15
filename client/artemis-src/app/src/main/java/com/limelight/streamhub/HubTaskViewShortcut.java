package com.limelight.streamhub;

import com.limelight.nvstream.input.KeyboardPacket;
import com.limelight.nvstream.jni.MoonBridge;

/** Owns forwarded Windows keys and opens Task View without leaking local long presses. */
public final class HubTaskViewShortcut {
    public interface Sender { void key(short key, byte direction, byte modifiers, byte flags); }
    private final Sender sender;
    private int held;
    public HubTaskViewShortcut(Sender sender) { this.sender=sender; }
    public static void send(Sender sender) {
        new HubTaskViewShortcut(sender).open();
    }
    public void windowsKey(boolean right,boolean down) {
        int bit=right?2:1;
        if(((held&bit)!=0)==down)return;
        short key=(short)(right?0x5C:0x5B);
        if(down) {
            sender.key(key,KeyboardPacket.KEY_DOWN,held==0?(byte)0:KeyboardPacket.MODIFIER_META,(byte)0);
            held|=bit;
        } else {
            held&=~bit;
            byte modifiers=held==0?(byte)0:KeyboardPacket.MODIFIER_META;
            sender.key(key,KeyboardPacket.KEY_UP,modifiers,(byte)0);
            sender.key(key,KeyboardPacket.KEY_UP,modifiers,MoonBridge.SS_KBE_FLAG_NON_NORMALIZED);
        }
    }
    public void open() {
        HubWindowSwitchShortcut.releaseHostModifiers((key, flags) ->
                sender.key(key, KeyboardPacket.KEY_UP, (byte) 0, flags));
        boolean temporary=held==0;
        if(temporary)windowsKey(false,true);
        sender.key((short) 9, KeyboardPacket.KEY_DOWN, KeyboardPacket.MODIFIER_META, (byte) 0);
        sender.key((short) 9, KeyboardPacket.KEY_UP, KeyboardPacket.MODIFIER_META, (byte) 0);
        if(temporary)finish();
    }
    public void finish() {
        if(held==0)return;
        held=0;
        for (short key : new short[] {0x5B, 0x5C}) {
            sender.key(key, KeyboardPacket.KEY_UP, (byte) 0, (byte) 0);
            sender.key(key, KeyboardPacket.KEY_UP, (byte) 0, MoonBridge.SS_KBE_FLAG_NON_NORMALIZED);
        }
    }
}
