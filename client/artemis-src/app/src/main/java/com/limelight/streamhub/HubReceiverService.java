package com.limelight.streamhub;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import org.json.JSONArray;
import org.json.JSONObject;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

/** User-enabled receiver. Background requests use notifications, never unsolicited activity launches. */
public final class HubReceiverService extends Service {
    public interface Listener { void requestChanged(JSONObject request); }
    public interface Completion { void complete(Exception error); }
    public static final String EXTRA_REQUEST = "HubPcRequest";
    private static final String CHANNEL = "hub-display-receiver";
    private static final int NOTIFICATION = 18410;
    private static volatile HubReceiverService instance;
    private static volatile Listener listener;
    private static volatile JSONObject currentRequest;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final ScheduledExecutorService worker = Executors.newSingleThreadScheduledExecutor();
    private HubStore store;
    private HubControlClient control;
    private String selectedHost = "", suppressedId = "";
    private volatile boolean stopped;
    public static void listen(Listener value) {
        listener = value;
        if (value != null) new Handler(Looper.getMainLooper()).post(() -> { if (listener == value) value.requestChanged(currentRequest); });
    }
    public static void enable(Context context, boolean enabled) {
        new HubStore(context).put("receiverEnabled", Boolean.toString(enabled));
        Intent intent = new Intent(context, HubReceiverService.class);
        if (!enabled) context.stopService(intent);
        else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) context.startForegroundService(intent);
        else context.startService(intent);
    }
    public static boolean isEnabled(Context context) { return "true".equals(new HubStore(context).get("receiverEnabled", "false")); }
    @Override public void onCreate() {
        super.onCreate(); instance = this; store = new HubStore(this);
        NotificationManager manager = (NotificationManager)getSystemService(NOTIFICATION_SERVICE);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) manager.createNotificationChannel(new NotificationChannel(CHANNEL, "电脑副屏请求", NotificationManager.IMPORTANCE_DEFAULT));
        startForeground(NOTIFICATION, notification(null));
        worker.scheduleWithFixedDelay(this::poll, 0, 2500, TimeUnit.MILLISECONDS);
    }
    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        if (!isEnabled(this) || !store.bound()) { stopSelf(); return START_NOT_STICKY; }
        if (intent != null && "reject".equals(intent.getAction())) {
            String id = intent.getStringExtra("requestId");
            if (id != null) respond(this, id, "reject", error -> { });
        }
        return START_STICKY;
    }
    private String transportRevision = "";
    private HubControlClient connect() throws Exception {
        String revision = store.get("transportRevision", "");
        if (!revision.equals(transportRevision)) { if (control != null) control.cancel(); control = null; selectedHost = ""; transportRevision = revision; currentRequest = null; publish(null); }
        if (control != null) return control;
        List<String> candidates = store.candidates("auto");
        Exception failure = null;
        for (String host : candidates) {
            if (stopped) return null;
            try {
                HubControlClient candidate = new HubControlClient(host, store.invitation());
                JSONObject info = candidate.probeInfo();
                String expected = store.get("deviceId", "");
                if (!expected.isEmpty() && !expected.equals(info.optString("deviceId"))) { candidate.cancel(); continue; }
                if (!revision.equals(store.get("transportRevision", ""))) { candidate.cancel(); throw new java.util.concurrent.CancellationException("连接通道已改变。"); }
                store.mergeAddresses(info.optJSONArray("addresses"), host);
                selectedHost = host; control = candidate; return control;
            } catch (Exception e) { failure = e; }
        }
        if (failure != null) throw failure;
        throw new java.io.IOException("电脑尚未在线");
    }
    private void poll() {
        if (stopped || !store.bound()) return;
        try {
            HubControlClient client = connect(); if (client == null) return;
            JSONObject body = HubDisplayCapabilities.describe(this, 0).put("clientId", store.clientId())
                    .put("name", Build.MODEL).put("orientations", new JSONArray(new int[]{0, 90, 180, 270}));
            JSONObject request = client.request("receiver/poll", body).optJSONObject("request");
            if (stopped || !transportRevision.equals(store.get("transportRevision", ""))) return;
            if (request != null && !"waiting".equals(request.optString("state"))) request = null;
            if (request != null && suppressedId.equals(request.optString("id"))) request = null;
            String before = currentRequest == null ? "" : currentRequest.optString("id");
            String after = request == null ? "" : request.optString("id");
            currentRequest = request;
            if (!before.equals(after)) publish(request);
        } catch (Exception ignored) { control = null; }
    }
    private void publish(JSONObject request) {
        ((NotificationManager)getSystemService(NOTIFICATION_SERVICE)).notify(NOTIFICATION, notification(request));
        main.post(() -> { Listener target = listener; if (target != null) target.requestChanged(request); });
    }
    private Notification notification(JSONObject request) {
        Intent open = new Intent(this, HubActivity.class).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP | Intent.FLAG_ACTIVITY_SINGLE_TOP);
        if (request != null) open.putExtra(EXTRA_REQUEST, request.toString());
        int immutable = PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE;
        PendingIntent content = PendingIntent.getActivity(this, request == null ? 0 : 1, open, immutable);
        Notification.Builder builder = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O ? new Notification.Builder(this, CHANNEL) : new Notification.Builder(this);
        builder.setSmallIcon(android.R.drawable.ic_menu_share).setContentTitle(request == null ? "我控制 · 等待电脑副屏请求" : "电脑请求使用此设备作为副屏")
                .setContentText(request == null ? "点此打开；可在连接页关闭接收" : "点按接受，或选择拒绝")
                .setContentIntent(content).setOngoing(true).setOnlyAlertOnce(true).setVisibility(Notification.VISIBILITY_PRIVATE);
        if (request != null) {
            Intent reject = new Intent(this, HubReceiverService.class).setAction("reject").putExtra("requestId", request.optString("id"));
            builder.addAction(0, "接受", content);
            builder.addAction(0, "拒绝", PendingIntent.getService(this, 2, reject, immutable));
        }
        return builder.build();
    }
    public static void respond(Context context, String id, String action, Completion completion) {
        HubReceiverService service = instance;
        if (service == null || service.stopped) { completion.complete(new java.io.IOException("请先开启接收电脑副屏请求。")); return; }
        service.worker.execute(() -> {
            Exception failure = null;
            try {
                JSONObject answer = service.connect().request("requests/" + id + "/respond", new JSONObject().put("clientId", service.store.clientId()).put("action", action));
                JSONObject request = answer.optJSONObject("request");
                String expected = "accept".equals(action) ? "accepted" : "reject".equals(action) ? "rejected" : action;
                if (request == null || !expected.equals(request.optString("state"))) throw new java.io.IOException(request == null ? "电脑没有返回请求状态。" : request.optString("message", "请求已取消或过期。"));
                service.suppressedId = id; currentRequest = null;
                service.store.put("lastHost", service.selectedHost); service.publish(null);
            } catch (Exception e) { failure = e; }
            Exception result = failure; service.main.post(() -> completion.complete(result));
        });
    }
    @Override public void onDestroy() {
        stopped = true; instance = null; currentRequest = null;
        if (control != null) control.cancel(); worker.shutdownNow();
        Listener target = listener; if (target != null) target.requestChanged(null);
        stopForeground(true); super.onDestroy();
    }
    @Override public IBinder onBind(Intent intent) { return null; }
}
