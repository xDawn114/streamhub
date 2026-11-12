package com.limelight.streamhub;

import org.junit.Test;
import java.util.Arrays;
import java.util.Base64;
import java.util.Random;
import static org.junit.Assert.*;

public class HubLanPacketTest {
    // Produced by the Windows LanDiscoveryPacket.Response implementation, not this decoder.
    private static byte[] windowsPacket() {
        return Base64.getDecoder().decode("AACEAAAAAAQAAAAACl9zdHJlYW1odWIEX3RjcAVsb2NhbAAADAABAAAAeAAyGlN0cmVhbUh1Yi0xMTExMTExMTIyMjIzMzMzCl9zdHJlYW1odWIEX3RjcAVsb2NhbAAaU3RyZWFtSHViLTExMTExMTExMjIyMjMzMzMKX3N0cmVhbWh1YgRfdGNwBWxvY2FsAAAhgAEAAAB4ACgAAAAAwC8ac3RyZWFtaHViLTExMTExMTExMjIyMjMzMzMFbG9jYWwAGlN0cmVhbUh1Yi0xMTExMTExMTIyMjIzMzMzCl9zdHJlYW1odWIEX3RjcAVsb2NhbAAAEIABAAAAeAB9A3Y9MSdpZD0xMTExMTExMS0yMjIyLTMzMzMtNDQ0NC01NTU1NTU1NTU1NTULbmFtZT1URVNUUENEcGluPUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUEac3RyZWFtaHViLTExMTExMTExMjIyMjMzMzMFbG9jYWwAAAGAAQAAAHgABAoBAgM=");
    }
    @Test public void windowsResponseIsDecodedWithExactOwnerAndAddress() {
        HubLanDiscovery.Device device=HubLanPacket.parse(windowsPacket(),"10.1.2.3");
        assertNotNull(device);assertEquals("TESTPC",device.name);assertEquals("10.1.2.3",device.address);
        assertEquals("11111111-2222-3333-4444-555555555555",device.id);assertEquals(64,device.pin.length());
    }
    @Test public void unrelatedSourceAndOverlayCannotAdvertiseAnotherAddress() {
        assertNull(HubLanPacket.parse(windowsPacket(),"10.1.2.4"));
        assertNull(HubLanPacket.parse(windowsPacket(),"100.64.0.7"));
        assertNull(HubLanPacket.parse(windowsPacket(),"8.8.8.8"));
    }
    @Test public void everyTruncatedWindowsResponseIsRejected() {
        byte[] packet=windowsPacket();for(int size=0;size<packet.length;size++)assertNull("length="+size,HubLanPacket.parse(Arrays.copyOf(packet,size),"10.1.2.3"));
    }
    @Test public void queriesAndWrongServicePortCannotBecomeDevices() throws Exception {
        byte[] query=HubLanPacket.query();
        assertEquals(0x80,query[query.length-2]&255);assertEquals(1,query[query.length-1]);
        assertNull(HubLanPacket.parse(HubLanPacket.query(),"10.1.2.3"));
        byte[] packet=windowsPacket();
        for(int i=0;i<packet.length-1;i++)if((packet[i]&255)==0xc0 && (packet[i+1]&255)==0x2f){packet[i+1]=0x30;break;}
        assertNull(HubLanPacket.parse(packet,"10.1.2.3"));
    }
    @Test public void legacyDirectResponseWithEchoedQuestionIsAccepted() throws Exception {
        byte[] query=HubLanPacket.query(),response=windowsPacket();
        byte[] direct=new byte[response.length+query.length-12];
        System.arraycopy(response,0,direct,0,12);direct[0]=0x12;direct[1]=0x34;direct[5]=1;
        System.arraycopy(query,12,direct,12,query.length-12);
        System.arraycopy(response,12,direct,query.length,response.length-12);
        assertNotNull(HubLanPacket.parse(direct,"10.1.2.3"));
        assertNull(HubLanPacket.parse(direct,"10.1.2.4"));
    }
    @Test public void malformedCountsPointersAndRandomPacketsAreBounded() {
        byte[] loop=new byte[24];loop[2]=(byte)0x84;loop[7]=1;loop[12]=(byte)0xc0;loop[13]=12;
        assertNull(HubLanPacket.parse(loop,"10.1.2.3"));
        byte[] many=windowsPacket();many[6]=127;many[7]=127;assertNull(HubLanPacket.parse(many,"10.1.2.3"));
        Random random=new Random(71);for(int i=0;i<2000;i++){byte[] bytes=new byte[random.nextInt(4200)];random.nextBytes(bytes);assertNull(HubLanPacket.parse(bytes,"10.1.2.3"));}
    }
}
