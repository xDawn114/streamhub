package com.limelight.streamhub;
import com.limelight.nvstream.input.KeyboardPacket;
import org.junit.Test;
import java.util.*;
import static org.junit.Assert.*;
public class HubAltTabSessionTest {
 @Test public void holdsAltAcrossTabsAndBalancesAllOwnedKeysOnFinish() {
  Set<String> held=new HashSet<>();List<String> packets=new ArrayList<>();
  HubAltTabSession session=new HubAltTabSession((key,direction,modifiers,flags)->{
   String id=key+":"+flags;packets.add(id+":"+direction+":"+modifiers);
   if(direction==KeyboardPacket.KEY_DOWN)held.add(id);else held.remove(id);
  });
  session.step(false);assertEquals(Collections.singleton("164:0"),held);
  int first=packets.size();session.step(true);
  assertEquals(Collections.singleton("164:0"),held);
  assertEquals(1,packets.stream().filter(x->x.startsWith("164:0:"+KeyboardPacket.KEY_DOWN+":")).count());
  assertTrue(packets.size()>first);session.finish();assertTrue(held.isEmpty());
  int ended=packets.size();session.finish();assertEquals(ended,packets.size());
  session.step(false);assertEquals(Collections.singleton("164:0"),held);session.finish();assertTrue(held.isEmpty());
 }
}
