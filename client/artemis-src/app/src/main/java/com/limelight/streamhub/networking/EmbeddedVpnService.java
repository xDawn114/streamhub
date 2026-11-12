package com.limelight.streamhub.networking;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Intent;
import android.net.IpPrefix;
import android.net.Network;
import android.net.VpnService;
import android.os.Build;
import android.system.OsConstants;

import java.net.InetAddress;
import java.util.UUID;

import libtailscale.IPNService;
import libtailscale.ParcelFileDescriptor;
import libtailscale.VPNServiceBuilder;

/** Own VpnService and TUN; requires Android's normal, explicit VPN consent. */
public final class EmbeddedVpnService extends VpnService implements IPNService {
    public static final String ACTION_CONNECT = "com.limelight.streamhub.network.CONNECT";
    public static final String ACTION_LOGIN = "com.limelight.streamhub.network.LOGIN";
    private static final String CHANNEL = "streamhub-network";
    private static volatile boolean tunnelActive;
    private final String identity = UUID.randomUUID().toString();
    private boolean closed;
    private EmbeddedNetwork network;
    public static boolean isTunnelActive() { return tunnelActive; }
    @Override public void onCreate() { super.onCreate(); network = EmbeddedNetwork.get(this); }
    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        showForeground();
        if (intent != null && ACTION_LOGIN.equals(intent.getAction())) return START_NOT_STICKY;
        if (VpnService.prepare(this) != null) { stopSelf(); return START_NOT_STICKY; }
        network.serviceConnected(this);
        return START_STICKY;
    }
    @Override public String id() { return identity; }
    @Override public VPNServiceBuilder newBuilder() {
        Builder builder = new Builder().setSession("我控制 · 异地连接").allowFamily(OsConstants.AF_INET).allowFamily(OsConstants.AF_INET6);
        Intent launch = getPackageManager().getLaunchIntentForPackage(getPackageName());
        if (launch != null) builder.setConfigureIntent(PendingIntent.getActivity(this, 0, launch, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE));
        // Only this product uses the overlay. Other apps retain their normal routes.
        try { builder.addAllowedApplication(getPackageName()); }
        catch (android.content.pm.PackageManager.NameNotFoundException ex) { throw new IllegalStateException("无法确认当前应用的VPN路由。", ex); }
        Network underlying = network.appContext().underlyingNetwork();
        builder.setUnderlyingNetworks(underlying == null ? null : new Network[] { underlying });
        if (Build.VERSION.SDK_INT >= 29) builder.setMetered(false);
        return new TunBuilder(builder);
    }
    @Override public void updateVpnStatus(boolean active) { tunnelActive = active; }
    @Override public void close() { if (closed) return; closed = true; tunnelActive = false; if (network != null) network.serviceDisconnected(this); stopSelf(); }
    @Override public void disconnectVPN() { close(); }
    @Override public void onDestroy() { close(); super.onDestroy(); }
    @Override public void onRevoke() { if (network != null) network.pauseAfterRevoke(); close(); super.onRevoke(); }
    private void showForeground() {
        NotificationManager manager = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        if (Build.VERSION.SDK_INT >= 26) manager.createNotificationChannel(new NotificationChannel(CHANNEL, "异地连接", NotificationManager.IMPORTANCE_LOW));
        Notification.Builder builder = Build.VERSION.SDK_INT >= 26 ? new Notification.Builder(this, CHANNEL) : new Notification.Builder(this);
        builder.setSmallIcon(android.R.drawable.stat_sys_upload_done).setContentTitle("我控制").setContentText("内置异地连接正在运行").setOngoing(true);
        Intent launch = getPackageManager().getLaunchIntentForPackage(getPackageName());
        if (launch != null) builder.setContentIntent(PendingIntent.getActivity(this, 0, launch, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE));
        startForeground(47021, builder.build());
    }
    private static final class TunBuilder implements VPNServiceBuilder {
        private final VpnService.Builder builder;
        TunBuilder(VpnService.Builder builder) { this.builder = builder; }
        @Override public void setMTU(int mtu) { builder.setMtu(mtu); }
        @Override public void addDNSServer(String address) { builder.addDnsServer(address); }
        @Override public void addSearchDomain(String domain) { builder.addSearchDomain(domain); }
        @Override public void addAddress(String address, int prefix) { builder.addAddress(address, prefix); }
        @Override public void addRoute(String address, int prefix) { builder.addRoute(address, prefix); }
        @Override public void excludeRoute(String address, int prefix) throws Exception { if (Build.VERSION.SDK_INT >= 33) builder.excludeRoute(new IpPrefix(InetAddress.getByName(address), prefix)); }
        @Override public ParcelFileDescriptor establish() throws Exception {
            android.os.ParcelFileDescriptor descriptor = builder.establish();
            if (descriptor == null) throw new IllegalStateException("VPN授权已失效，请重新允许连接。");
            return descriptor::detachFd;
        }
    }
}

