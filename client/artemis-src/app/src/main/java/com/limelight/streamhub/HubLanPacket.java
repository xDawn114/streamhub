package com.limelight.streamhub;

import java.io.ByteArrayOutputStream;
import java.io.DataOutputStream;
import java.nio.ByteBuffer;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/** Bounded DNS-SD response decoder for local links not exposed to Android NSD. */
public final class HubLanPacket {
    private static final String SERVICE = "_streamhub._tcp.local";
    private HubLanPacket() { }
    public static byte[] query() throws Exception {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        DataOutputStream out = new DataOutputStream(bytes);
        out.writeShort(0); out.writeShort(0); out.writeShort(1);
        out.writeShort(0); out.writeShort(0); out.writeShort(0);
        for (String label : SERVICE.split("\\.")) { byte[] value=label.getBytes(StandardCharsets.US_ASCII); out.writeByte(value.length); out.write(value); }
        // QU: reply to the querying socket instead of relying on multicast reception.
        out.writeByte(0); out.writeShort(12); out.writeShort(0x8001);
        return bytes.toByteArray();
    }
    private static final class Record {
        String owner; int type, offset, length;
        Record(String owner,int type,int offset,int length) { this.owner=owner;this.type=type;this.offset=offset;this.length=length; }
    }
    private static int u16(byte[] bytes,int offset) {
        if(offset<0 || offset+2>bytes.length)throw new IllegalArgumentException();
        return (bytes[offset]&255)<<8 | bytes[offset+1]&255;
    }
    private static String name(byte[] bytes,int[] position) {
        StringBuilder text=new StringBuilder(); int cursor=position[0],hops=0; boolean jumped=false;
        while(true) {
            if(++hops>64 || cursor>=bytes.length)throw new IllegalArgumentException();
            int count=bytes[cursor++]&255;
            if(count==0) { if(!jumped)position[0]=cursor;return text.toString().toLowerCase(java.util.Locale.ROOT); }
            if((count&0xc0)==0xc0) {
                if(cursor>=bytes.length)throw new IllegalArgumentException();
                int target=((count&63)<<8)|(bytes[cursor++]&255);
                if(target>=cursor-2)throw new IllegalArgumentException();
                if(!jumped)position[0]=cursor; jumped=true;cursor=target;continue;
            }
            if(count>63 || cursor+count>bytes.length)throw new IllegalArgumentException();
            if(text.length()>0)text.append('.');
            for(int i=0;i<count;i++) { int value=bytes[cursor++]&255;if(value<33 || value>126)throw new IllegalArgumentException();text.append((char)value); }
            if(text.length()>253)throw new IllegalArgumentException();
        }
    }
    public static HubLanDiscovery.Device parse(byte[] bytes,String source) {
        try {
            if(bytes.length<12 || bytes.length>4096 || !HubLanDiscovery.localIpv4(source) || (bytes[2]&0xf8)!=0x80)return null;
            int questions=u16(bytes,4),count=u16(bytes,6)+u16(bytes,8)+u16(bytes,10);
            if(questions>32 || count>64)return null;
            int[] position={12};
            for(int i=0;i<questions;i++){name(bytes,position);position[0]+=4;if(position[0]>bytes.length)return null;}
            List<Record> records=new ArrayList<>();
            for(int i=0;i<count;i++) {
                String owner=name(bytes,position);int at=position[0];
                if(at+10>bytes.length)return null;
                int type=u16(bytes,at),length=u16(bytes,at+8);position[0]=at+10+length;
                if(position[0]>bytes.length)return null;
                if((u16(bytes,at+2)&0x7fff)==1 && (bytes[at+4]!=0 || bytes[at+5]!=0 || bytes[at+6]!=0 || bytes[at+7]!=0))
                    records.add(new Record(owner,type,at+10,length));
            }
            for(Record ptr:records) {
                if(ptr.type!=12 || !SERVICE.equals(ptr.owner))continue;
                position[0]=ptr.offset;String instance=name(bytes,position);
                if(position[0]!=ptr.offset+ptr.length || !instance.endsWith("."+SERVICE))continue;
                String host=null,address=null;Map<String,String> attributes=new HashMap<>();
                for(Record record:records) {
                    if(!instance.equals(record.owner))continue;
                    if(record.type==33 && record.length>=7 && u16(bytes,record.offset+4)==49199) {
                        position[0]=record.offset+6;String target=name(bytes,position);
                        if(position[0]==record.offset+record.length)host=target;
                    }
                    if(record.type==16) {
                        int cursor=record.offset,end=cursor+record.length;
                        while(cursor<end) {
                            int size=bytes[cursor++]&255;if(size==0 || cursor+size>end)return null;
                            String value=StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
                                    .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes,cursor,size)).toString();
                            cursor+=size;int split=value.indexOf('=');if(split<=0)return null;
                            if(attributes.put(value.substring(0,split),value.substring(split+1))!=null)return null;
                        }
                    }
                }
                for(Record record:records)if(record.type==1 && record.length==4 && record.owner.equals(host)) {
                    int at=record.offset;String candidate=(bytes[at]&255)+"."+(bytes[at+1]&255)+"."+(bytes[at+2]&255)+"."+(bytes[at+3]&255);
                    if(candidate.equals(source))address=candidate;
                }
                String id=attributes.get("id"),label=attributes.get("name"),pin=attributes.get("pin");
                if(address!=null && "1".equals(attributes.get("v")) && id!=null && id.length()==36 &&
                        java.util.UUID.fromString(id).toString().equalsIgnoreCase(id) && label!=null && !label.isEmpty() && label.length()<=64 &&
                        !label.matches("(?s).*[\\p{Cntrl}].*") && pin!=null && pin.matches("[A-Fa-f0-9]{64}"))
                    return new HubLanDiscovery.Device(instance,id,label,address,pin.toUpperCase(java.util.Locale.ROOT));
            }
        } catch(Exception ignored) { }
        return null;
    }
}
