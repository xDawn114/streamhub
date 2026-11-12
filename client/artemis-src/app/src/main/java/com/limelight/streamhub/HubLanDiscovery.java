package com.limelight.streamhub;

import android.content.Context;
import android.net.nsd.NsdManager;
import android.net.nsd.NsdServiceInfo;
import android.net.wifi.WifiManager;
import android.os.Handler;
import android.os.Looper;
import java.nio.charset.StandardCharsets;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.atomic.AtomicInteger;

/** DNS-SD metadata is a hint only. It never replaces explicit owner-approved binding. */
public final class HubLanDiscovery {
    public static final String SERVICE = "_streamhub._tcp.";
    public static final class Device {
        public final String service, id, name, address, pin;
        public Device(String service, String id, String name, String address, String pin) {
            this.service=service; this.id=id; this.name=name; this.address=address; this.pin=pin;
        }
    }
    public interface Listener { void update(List<Device> devices, String message); }
    private static final class SearchStats {
        volatile int interfaces;
        volatile String state="尚未查找", lastError="无";
        final AtomicInteger sent=new AtomicInteger(),received=new AtomicInteger(),accepted=new AtomicInteger(),
                rejected=new AtomicInteger(),errors=new AtomicInteger();
        void error(String operation,Exception error){errors.incrementAndGet();lastError=operation+" / "+error.getClass().getSimpleName();}
        String describe(){return "状态："+state+"\n本地网络接口："+interfaces+"\n直接回复查询："+sent.get()
                +"\n收到的数据包："+received.get()+"\n有效电脑回复："+accepted.get()+"\n未采纳数据包："+rejected.get()
                +"\n错误："+errors.get()+"\n最近错误："+lastError
                +"\n\n没有回复不等于网络隔离。可在电脑端读取附近发现状态，核对是否收到查询及发出回复。";}
    }
    private volatile SearchStats stats=new SearchStats();
    private String requestedAddress="";
    public String diagnostics(){return stats.describe();}
    private final NsdManager manager;
    private final android.net.ConnectivityManager connectivity;
    private android.net.ConnectivityManager.NetworkCallback networkCallback;
    private final WifiManager.MulticastLock multicast;
    private final Handler main=new Handler(Looper.getMainLooper());
    private final Map<String,Device> devices=new LinkedHashMap<>();
    private final ArrayDeque<NsdServiceInfo> pending=new ArrayDeque<>();
    private NsdManager.DiscoveryListener discovery;
    private boolean resolving;
    private volatile int generation;
    private int nsdGeneration;
    private Listener activeListener;
    private final Runnable networkRefresh=()->{if(activeListener!=null){devices.clear();activeListener.update(new ArrayList<>(),"网络已变化，正在重新查找…");startUsb(generation,activeListener);startNsd(generation,activeListener);}};
    private volatile java.net.MulticastSocket usbSocket;
    private volatile int usbGeneration;
    public HubLanDiscovery(Context context) {
        connectivity=(android.net.ConnectivityManager)context.getSystemService(Context.CONNECTIVITY_SERVICE);
        manager=(NsdManager)context.getSystemService(Context.NSD_SERVICE);
        WifiManager wifi=(WifiManager)context.getApplicationContext().getSystemService(Context.WIFI_SERVICE);
        multicast=wifi == null ? null : wifi.createMulticastLock("StreamHub nearby devices");
        if (multicast != null) multicast.setReferenceCounted(false);
    }
    public static boolean localIpv4(String value) {
        if (!HubRoutes.isUsableIpv4(value)) return false;
        String[] p=value.split("\\."); int a=Integer.parseInt(p[0]),b=Integer.parseInt(p[1]);
        return a==10 || a==192 && b==168 || a==172 && b>=16 && b<=31;
    }
    public void start(Listener listener) { start(listener,""); }
    public void start(Listener listener,String address) {
        stop(); final int run=++generation; activeListener=listener;
        requestedAddress=address==null?"":address.trim();
        if(!requestedAddress.isEmpty() && !localIpv4(requestedAddress)){listener.update(new ArrayList<>(),"请输入电脑显示的局域网 IPv4 地址。");return;}
        if(multicast!=null)try{multicast.acquire();}catch(RuntimeException ignored){}
        startUsb(run,listener);
        if(connectivity!=null)try {
            networkCallback=new android.net.ConnectivityManager.NetworkCallback(){
                private void changed(){main.post(()->{if(run==generation){main.removeCallbacks(networkRefresh);main.postDelayed(networkRefresh,600);}});}
                @Override public void onAvailable(android.net.Network network){changed();}
                @Override public void onLost(android.net.Network network){changed();}
                @Override public void onLinkPropertiesChanged(android.net.Network network,android.net.LinkProperties properties){changed();}
            };
            connectivity.registerNetworkCallback(new android.net.NetworkRequest.Builder().build(),networkCallback);
        }catch(RuntimeException ignored){networkCallback=null;}

        startNsd(run,listener);
    }
    private void startNsd(int run,Listener listener) {
        final int nsd=++nsdGeneration;
        if(discovery!=null && manager!=null)try{manager.stopServiceDiscovery(discovery);}catch(RuntimeException ignored){}
        discovery=null;pending.clear();resolving=false;
        if (manager==null) return; // The independent socket search remains active.
        listener.update(new ArrayList<>(),"正在查找同一网络中的电脑…");
        discovery=new NsdManager.DiscoveryListener() {
            private void show(String message) { main.post(() -> { if(run==generation && nsd==nsdGeneration)listener.update(new ArrayList<>(devices.values()),message); }); }
            @Override public void onDiscoveryStarted(String type) { }
            @Override public void onDiscoveryStopped(String type) { }
            @Override public void onStartDiscoveryFailed(String type,int error) { main.post(()->{if(run==generation && nsd==nsdGeneration){stats.errors.incrementAndGet();stats.lastError="系统发现 / "+error;}}); }
            @Override public void onStopDiscoveryFailed(String type,int error) { }
            @Override public void onServiceFound(NsdServiceInfo service) {
                main.post(() -> { if(run==generation && nsd==nsdGeneration && pending.size()<16) { pending.add(service); resolve(run,nsd,listener); } });
            }
            @Override public void onServiceLost(NsdServiceInfo service) {
                main.post(() -> { if(run==generation && nsd==nsdGeneration) { devices.values().removeIf(d -> d.service.equalsIgnoreCase(service.getServiceName())); show(devices.isEmpty()?"暂无附近电脑，可重试或使用电脑地址查找。":"请选择电脑并输入它显示的短码。"); } });
            }
        };
        try {
            manager.discoverServices(SERVICE,NsdManager.PROTOCOL_DNS_SD,discovery);
        } catch (RuntimeException error) { stats.error("系统发现",error); }
    }
    private void resolve(int run,int nsd,Listener listener) {
        if(resolving || pending.isEmpty() || run!=generation || nsd!=nsdGeneration)return;
        resolving=true;
        try { manager.resolveService(pending.remove(),new NsdManager.ResolveListener() {
            private void done() { main.post(() -> { if(run==generation && nsd==nsdGeneration){resolving=false;resolve(run,nsd,listener);} }); }
            @Override public void onResolveFailed(NsdServiceInfo service,int error) { done(); }
            @Override public void onServiceResolved(NsdServiceInfo service) {
                main.post(() -> {
                    if(run!=generation || nsd!=nsdGeneration)return;
                    try {
                        Map<String,byte[]> attrs=service.getAttributes();
                        String id=text(attrs,"id"),name=text(attrs,"name"),pin=text(attrs,"pin");
                        String address=service.getHost()==null?"":service.getHost().getHostAddress();
                        if(service.getPort()==49199 && localIpv4(address) && attachedAddress(address) && "1".equals(text(attrs,"v")) &&
                                (requestedAddress.isEmpty() || requestedAddress.equals(address)) && id.length()==36 && java.util.UUID.fromString(id).toString().equalsIgnoreCase(id) &&
                                !name.isEmpty() && name.length()<=64 && !name.matches("(?s).*[\\p{Cntrl}].*") && pin.matches("[A-Fa-f0-9]{64}") && devices.size()<16) {
                            devices.put(id,new Device(service.getServiceName(),id,name,address,pin.toUpperCase(java.util.Locale.ROOT)));
                            listener.update(new ArrayList<>(devices.values()),"请选择电脑并输入它显示的短码。");
                        }
                    } catch(Exception ignored) { }
                    resolving=false; resolve(run,nsd,listener);
                });
            }
        }); } catch(RuntimeException error) { resolving=false; main.postDelayed(() -> resolve(run,nsd,listener),500); }
    }
    private static String text(Map<String,byte[]> attrs,String key) {
        byte[] value=attrs.get(key);return value==null || value.length>255?"":new String(value,StandardCharsets.UTF_8);
    }
    private void startUsb(int run,Listener listener) {
        final int raw=++usbGeneration;
        final SearchStats counters=new SearchStats();stats=counters;
        final String target=requestedAddress;
        synchronized(this){if(usbSocket!=null)usbSocket.close();usbSocket=null;}
        Thread worker=new Thread(() -> {
            java.net.MulticastSocket socket=null;
            try {
                List<java.net.NetworkInterface> links=new ArrayList<>();
                for(java.net.NetworkInterface link:java.util.Collections.list(java.net.NetworkInterface.getNetworkInterfaces())) {
                    String name=link.getName().toLowerCase(java.util.Locale.ROOT);
                    if(link.isUp() && (name.contains("rndis") || name.contains("usb") || name.contains("ncm") || name.matches("eth[0-9]+") || name.startsWith("wlan") || name.startsWith("wifi"))) {
                        boolean local=false;
                        for(java.net.InetAddress address:java.util.Collections.list(link.getInetAddresses()))
                            if(localIpv4(address.getHostAddress()))local=true;
                        if(local && links.size()<8)links.add(link);
                    }
                }
                counters.interfaces=links.size();
                if(links.isEmpty()){counters.state="没有可用的局域网接口";return;}
                if(!target.isEmpty() && !attachedAddress(target)){counters.state="电脑地址不在当前直连网络";return;}
                if(usbGeneration!=raw || generation!=run)return;
                java.net.InetSocketAddress group=new java.net.InetSocketAddress("224.0.0.251",5353);
                // Query from an ephemeral port so Android NSD does not consume the reply.
                socket=new java.net.MulticastSocket(null);
                socket.bind(new java.net.InetSocketAddress(0));socket.setTimeToLive(255);socket.setSoTimeout(500);counters.state="查找中";
                synchronized(this) { if((usbGeneration!=raw || generation!=run))return;usbSocket=socket; }
                long started=android.os.SystemClock.elapsedRealtime(),nextQuery=started;
                while(usbGeneration==raw && generation==run && android.os.SystemClock.elapsedRealtime()-started<12000) {
                    long now=android.os.SystemClock.elapsedRealtime();
                    if(now>=nextQuery) {
                        byte[] query=HubLanPacket.query();
                        if(target.isEmpty())for(java.net.NetworkInterface link:links)try {
                            socket.setNetworkInterface(link);socket.send(new java.net.DatagramPacket(query,query.length,group));counters.sent.incrementAndGet();
                        }catch(java.io.IOException error){counters.error("发送查询",error);}
                        else try{socket.send(new java.net.DatagramPacket(query,query.length,new java.net.InetSocketAddress(target,5353)));counters.sent.incrementAndGet();}
                        catch(java.io.IOException error){counters.error("地址查询",error);}
                        nextQuery=now+1000;
                    }
                    byte[] bytes=new byte[4097];java.net.DatagramPacket packet=new java.net.DatagramPacket(bytes,bytes.length);
                    try { socket.receive(packet); } catch(java.net.SocketTimeoutException ignored) { continue; }
                    counters.received.incrementAndGet();
                    if(packet.getPort()!=5353 || packet.getLength()>4096){counters.rejected.incrementAndGet();continue;}
                    String source=packet.getAddress().getHostAddress();
                    boolean attached=false;
                    for(java.net.NetworkInterface link:links)for(java.net.InterfaceAddress own:link.getInterfaceAddresses())
                        if(own.getAddress() instanceof java.net.Inet4Address && sameSubnet(own,packet.getAddress()))attached=true;
                    if(!attached || !target.isEmpty() && !target.equals(source)){counters.rejected.incrementAndGet();continue;}
                    Device device=HubLanPacket.parse(java.util.Arrays.copyOf(bytes,packet.getLength()),source);
                    if(device==null){counters.rejected.incrementAndGet();continue;}
                    counters.accepted.incrementAndGet();main.post(() -> { if(run==generation && raw==usbGeneration && devices.size()<16) {
                        devices.put(device.id,device);listener.update(new ArrayList<>(devices.values()),"请选择电脑并输入它显示的短码。");
                    }});
                }
                counters.state=counters.accepted.get()>0?"已收到电脑回复":"本次查找结束";
            } catch(Exception error) { if(raw==usbGeneration && run==generation){counters.error("直接查找",error);counters.state="直接查找未完成";} }
            finally {
                if(socket!=null)socket.close();synchronized(this){if(usbSocket==socket)usbSocket=null;}
                main.post(()->{if(run==generation && raw==usbGeneration && devices.isEmpty())listener.update(new ArrayList<>(),
                    counters.sent.get()>0?"本次未收到有效电脑回复。可重试、查看查找详情，或使用电脑地址与短码。":
                    "暂时无法发送查找请求，请检查本地网络或使用已有绑定连接。");});
            }
        },"StreamHub local interface discovery");
        worker.setDaemon(true);worker.start();
    }
    private static boolean sameSubnet(java.net.InterfaceAddress own,java.net.InetAddress peer) {
        byte[] a=own.getAddress().getAddress(),b=peer.getAddress();int prefix=own.getNetworkPrefixLength();
        return sameSubnet(a,b,prefix);
    }
    static boolean sameSubnet(byte[] a,byte[] b,int prefix) {
        if(a.length!=4 || b.length!=4 || prefix<=0 || prefix>32)return false;
        for(int i=0;i<4 && prefix>0;i++,prefix-=8) { int mask=prefix>=8?255:255<<(8-prefix);if((a[i]&mask)!=(b[i]&mask))return false; }
        return true;
    }
    public void stop() {
        generation++; nsdGeneration++; activeListener=null; pending.clear(); devices.clear();resolving=false;
        main.removeCallbacks(networkRefresh);
        if(networkCallback!=null && connectivity!=null)try{connectivity.unregisterNetworkCallback(networkCallback);}catch(RuntimeException ignored){}
        networkCallback=null;

        usbGeneration++;synchronized(this){if(usbSocket!=null)usbSocket.close();usbSocket=null;}
        if(discovery!=null && manager!=null)try{manager.stopServiceDiscovery(discovery);}catch(RuntimeException ignored){}
        discovery=null;
        if(multicast!=null && multicast.isHeld())try{multicast.release();}catch(RuntimeException ignored){}
    }
    private static boolean attachedAddress(String address) throws Exception {
        java.net.InetAddress peer=java.net.InetAddress.getByName(address);
        for(java.net.NetworkInterface link:java.util.Collections.list(java.net.NetworkInterface.getNetworkInterfaces()))
            if(link.isUp() && !link.isLoopback())for(java.net.InterfaceAddress own:link.getInterfaceAddresses())
                if(sameSubnet(own,peer))return true;
        return false;
    }
}
