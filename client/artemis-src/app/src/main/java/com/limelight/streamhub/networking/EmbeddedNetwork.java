package com.limelight.streamhub.networking;

import android.content.Context;
import com.limelight.streamhub.HubStore;
import com.limelight.streamhub.HubTransports;
import android.content.Intent;
import android.content.SharedPreferences;
import android.net.VpnService;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;

import org.json.JSONArray;
import org.json.JSONObject;

import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

import libtailscale.Application;
import libtailscale.InputStream;
import libtailscale.Libtailscale;
import libtailscale.LocalAPIResponse;

/** The in-process Tailscale backend. It never starts or reads another VPN app. */
public final class EmbeddedNetwork {
    public interface Callback { void complete(Status status, String error); }
    public static final class Status {
        public final String state, message, authorizationUrl, account, provider, revision;
        public final List<String> addresses;
        public final List<Peer> peers;
        public final boolean vpnActive;
        Status(JSONObject json, HubTransports.Settings settings, String revision) {
            this.revision = revision;
            provider = settings.provider;
            state = json.optString("BackendState", "NoState");
            message = "Local".equals(state) ? settings.transport().label() + " · 无需云账号" : stateMessage(state);
            authorizationUrl = HubTransports.authorizationUrl(settings, json.optString("AuthURL"));
            addresses = strings(json.optJSONArray("TailscaleIPs"));
            JSONObject self = json.optJSONObject("Self");
            JSONObject users = json.optJSONObject("User");
            JSONObject user = self != null && users != null ? users.optJSONObject(self.optString("UserID")) : null;
            account = user == null ? "" : user.optString("LoginName");
            peers = new ArrayList<>();
            JSONObject map = json.optJSONObject("Peer");
            if (map != null) {
                java.util.Iterator<String> keys = map.keys();
                while (keys.hasNext()) { JSONObject peer = map.optJSONObject(keys.next()); if (peer != null) peers.add(new Peer(peer)); }
            }
            vpnActive = EmbeddedVpnService.isTunnelActive();
        }
        public boolean loggedIn() { return !addresses.isEmpty() && !"NeedsLogin".equals(state); }
    }
    public static final class Peer {
        public final String name, operatingSystem, routeText, relayRegion;
        public final List<String> addresses;
        public final boolean online;
        Peer(JSONObject json) {
            name = json.optString("HostName"); operatingSystem = json.optString("OS");
            addresses = strings(json.optJSONArray("TailscaleIPs")); online = json.optBoolean("Online");
            // Tailscale reports CurAddr for a direct path and PeerRelay/Relay when
            // traffic is using a DERP relay. Keep this small status beside the
            // device entry so the user can tell why an otherwise online peer is slow.
            String direct = json.optString("CurAddr");
            String peerRelay = json.optString("PeerRelay");
            relayRegion = json.optString("Relay");
            if (!direct.isEmpty()) routeText = "直连";
            else if (!peerRelay.isEmpty()) routeText = relayRegion.isEmpty() ? "中继" : "中继 " + relayRegion;
            else if (!relayRegion.isEmpty()) routeText = "中继 " + relayRegion;
            else routeText = online ? "路径检测中" : "离线";
        }
    }
    private static volatile EmbeddedNetwork instance;
    private final Context context;
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Handler main = new Handler(Looper.getMainLooper());
    private final NetworkAppContext appContext;
    private final SharedPreferences runtime;
    private final HubStore store;
    private final HubNetworkProviderSession providerSession = new HubNetworkProviderSession(this::call);
    private boolean resumeInFlight;
    private Application backend;
    private EmbeddedNetwork(Context context) {
        this.context = context.getApplicationContext();
        appContext = new NetworkAppContext(this.context);
        store = new HubStore(this.context);
        runtime = this.context.getSharedPreferences("embedded-network-runtime", Context.MODE_PRIVATE);
    }
    public static EmbeddedNetwork get(Context context) {
        if (instance == null) synchronized (EmbeddedNetwork.class) { if (instance == null) instance = new EmbeddedNetwork(context); }
        return instance;
    }
    private synchronized Application backend() {
        if (backend == null) {
            java.io.File state = new java.io.File(context.getNoBackupFilesDir(), "embedded-network");
            java.io.File files = new java.io.File(context.getCacheDir(), "network-files");
            if ((!state.isDirectory() && !state.mkdirs()) || (!files.isDirectory() && !files.mkdirs())) throw new IllegalStateException("无法创建内置连接数据目录。");
            backend = Libtailscale.start(state.getAbsolutePath(), files.getAbsolutePath(), false, appContext);
            appContext.startNetworkMonitor();
        }
        return backend;
    }
    private JSONObject call(String method, String endpoint, String body) throws Exception {
        LocalAPIResponse response = backend().callLocalAPI(15000, method, "http://local-tailscaled.sock/localapi/v0/" + endpoint, body == null ? null : new Body(body));
        byte[] bytes = response.bodyBytes();
        if (response.statusCode() >= 400) throw new IllegalStateException("内置连接操作失败（" + response.statusCode() + "）。");
        if (bytes == null || bytes.length == 0) return new JSONObject();
        String text = new String(bytes, StandardCharsets.UTF_8);
        return text.trim().startsWith("{") ? new JSONObject(text) : new JSONObject();
    }
    private interface Action { Status run() throws Exception; }
    private void run(Action action, Callback callback) {
        worker.execute(() -> {
            Status result = null; String error = null;
            try { result = action.run(); } catch (Throwable ex) { error = "内置连接暂不可用：" + (ex.getMessage() == null ? ex.getClass().getSimpleName() : ex.getMessage()); }
            Status finalResult = result; String finalError = error;
            if (callback != null) main.post(() -> callback.complete(finalResult, finalError));
        });
    }
    private Status readStatus() throws Exception {
        HubTransports.Settings settings = store.transport();
        return new Status(settings.transport().usesAccount() ? providerSession.status(settings) : new JSONObject().put("BackendState", "Local"), settings, store.get("transportRevision", ""));
    }
    public void status(Callback callback) { run(this::readStatus, callback); }
    public void configure(HubTransports.Settings settings, Callback callback) {
        run(() -> {
            if (com.limelight.streamhub.HubSessionLease.active() || com.limelight.streamhub.HubFileService.busy()) throw new IllegalStateException("请先结束串流和文件传输，再切换通道。");
            if (backend != null || settings.transport().usesAccount()) providerSession.configure(settings);
            if (!runtime.edit().putBoolean("autoConnect", false).commit()) throw new IllegalStateException("无法保存异地连接暂停状态，请重试。");
            context.stopService(new Intent(context, EmbeddedVpnService.class));
            store.saveTransport(settings);
            return readStatus();
        }, callback);
    }
    public void login(Callback callback) {
        run(() -> {
            providerSession.requireCurrent(store.transport());
            startService(EmbeddedVpnService.ACTION_LOGIN);
            call("POST", "start", "{}");
            setRunningNow(true);
            Status existing = readStatus();
            if (existing.loggedIn()) return existing;
            call("POST", "login-interactive", null);
            for (int attempt = 0; attempt < 30; attempt++) {
                Status status = readStatus();
                if (!status.authorizationUrl.isEmpty() || status.loggedIn()) return status;
                Thread.sleep(500);
            }
            throw new IllegalStateException("未取得账号授权地址，请检查网络后重试。");
        }, callback);
    }
    public Intent prepareVpn() { return VpnService.prepare(context); }
    /** Call only after prepareVpn returned null or its activity returned RESULT_OK. */
    public void connect(Callback callback) {
        if (prepareVpn() != null) { if (callback != null) main.post(() -> callback.complete(null, "请先允许我控制建立VPN连接。")); return; }
        run(() -> {
            providerSession.requireCurrent(store.transport());
            runtime.edit().putBoolean("autoConnect", true).apply();
            startService(EmbeddedVpnService.ACTION_CONNECT);
            return readStatus();
        }, callback);
    }
    /** Restore an explicitly enabled tunnel after the app process or activity was recreated. */
    public void resumeIfNeeded(Callback callback) {
        try { if (!store.transport().transport().usesAccount()) { status(callback); return; } }
        catch (IllegalArgumentException error) { if (callback != null) callback.complete(null, error.getMessage()); return; }
        if (!runtime.getBoolean("autoConnect", false) || EmbeddedVpnService.isTunnelActive()) {
            status(callback);
            return;
        }
        if (prepareVpn() != null) {
            status(callback);
            return;
        }
        synchronized (this) {
            if (resumeInFlight) { status(callback); return; }
            resumeInFlight = true;
        }
        connect((state, error) -> {
            synchronized (EmbeddedNetwork.this) { resumeInFlight = false; }
            if (callback != null) callback.complete(state, error);
        });
    }
    public void disconnect(Callback callback) {
        runtime.edit().putBoolean("autoConnect", false).apply();
        run(() -> { setRunningNow(false); context.stopService(new Intent(context, EmbeddedVpnService.class)); return readStatus(); }, callback);
    }
    public void logout(Callback callback) {
        runtime.edit().putBoolean("autoConnect", false).apply();
        run(() -> { setRunningNow(false); context.stopService(new Intent(context, EmbeddedVpnService.class)); call("POST", "logout", null); return readStatus(); }, callback);
    }
    void serviceConnected(EmbeddedVpnService service) {
        run(() -> { backend(); setRunningNow(true); Libtailscale.requestVPN(service); return readStatus(); }, null);
    }
    void serviceDisconnected(EmbeddedVpnService service) { worker.execute(() -> { if (backend != null) Libtailscale.serviceDisconnect(service); }); }
    void pauseAfterRevoke() {
        runtime.edit().putBoolean("autoConnect", false).apply();
        run(() -> { if (backend != null) setRunningNow(false); return readStatus(); }, null);
    }
    NetworkAppContext appContext() { return appContext; }
    private void startService(String action) {
        Intent intent = new Intent(context, EmbeddedVpnService.class).setAction(action);
        if (Build.VERSION.SDK_INT >= 26) context.startForegroundService(intent); else context.startService(intent);
    }
    private void setRunningNow(boolean running) throws Exception {
        if (running) providerSession.requireCurrent(store.transport());
        call("PATCH", "prefs", new JSONObject().put("WantRunning", running).put("WantRunningSet", true).put("CorpDNS", false).put("CorpDNSSet", true).put("RouteAll", false).put("RouteAllSet", true).toString());
    }
    private static List<String> strings(JSONArray array) { List<String> result = new ArrayList<>(); if (array != null) for (int i = 0; i < array.length(); i++) result.add(array.optString(i)); return result; }
    private static String stateMessage(String state) {
        switch (state) { case "Running": return "内置异地连接已就绪"; case "NeedsLogin": return "等待账号登录"; case "NeedsMachineAuth": return "等待账号批准设备"; case "Stopped": return "异地连接已暂停"; case "Starting": return "异地连接启动中"; default: return "正在准备内置连接"; }
    }
    private static final class Body implements InputStream {
        private byte[] bytes;
        Body(String body) { bytes = body.getBytes(StandardCharsets.UTF_8); }
        @Override public byte[] read() { byte[] result = bytes; bytes = null; return result; }
        @Override public void close() { bytes = null; }
    }
}
