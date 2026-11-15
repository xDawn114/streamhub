package com.limelight.streamhub;

import com.limelight.nvstream.input.KeyboardPacket;
import com.limelight.nvstream.jni.MoonBridge;
import org.junit.Test;
import java.util.*;
import static org.junit.Assert.*;

public class HubTaskViewShortcutTest {
    @Test public void sendsTaskViewAndReleasesPhysicalAndGeneratedModifiers() {
        Set<String> held=new HashSet<>(Arrays.asList("92:"+MoonBridge.SS_KBE_FLAG_NON_NORMALIZED,"162:0"));
        List<Short> pressed=new ArrayList<>();
        HubTaskViewShortcut.send((key,direction,modifiers,flags)->{
            String id=key+":"+flags;
            if(direction==KeyboardPacket.KEY_DOWN) {
                held.add(id);pressed.add(key);
                if(key==9)assertEquals(KeyboardPacket.MODIFIER_META,modifiers);
            } else held.remove(id);
        });
        assertEquals(Arrays.asList((short)91,(short)9),pressed);
        assertTrue("No Windows, Alt, Ctrl or Shift key may remain held",held.isEmpty());
    }
    @Test public void physicalWindowsKeyStaysHeldUntilReleaseAndFocusLossReleasesIt() {
        Set<String> held=new HashSet<>();List<Short> pressed=new ArrayList<>();
        HubTaskViewShortcut shortcut=new HubTaskViewShortcut((key,direction,modifiers,flags)->{
            String id=key+":"+flags;
            if(direction==KeyboardPacket.KEY_DOWN){held.add(id);pressed.add(key);}else held.remove(id);
        });
        shortcut.windowsKey(true,true);shortcut.windowsKey(true,true);shortcut.open();
        assertEquals(Collections.singleton("92:0"),held);
        assertEquals(Arrays.asList((short)92,(short)9),pressed);
        shortcut.windowsKey(true,false);assertTrue(held.isEmpty());
        shortcut.windowsKey(false,true);shortcut.finish();assertTrue(held.isEmpty());
        shortcut.finish();assertTrue(held.isEmpty());
    }
}
