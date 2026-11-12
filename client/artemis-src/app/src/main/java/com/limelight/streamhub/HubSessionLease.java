package com.limelight.streamhub;

import android.content.Context;
import android.content.Intent;
import android.os.SystemClock;
import org.json.JSONObject;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

/** Each stream owns a fresh lease; ending an old stream cannot tear down its replacement. */
public final class HubSessionLease {
    private static final ConcurrentHashMap<String, HubSessionLease> leases = new ConcurrentHashMap<>();
    private final ScheduledExecutorService worker = Executors.newSingleThreadScheduledExecutor();
    private final java.util.concurrent.ExecutorService reporter = Executors.newSingleThreadExecutor();
    private final HubStore store;
    private final Context context;
    private final String host, leaseId, mode, requestId;
    private final HubSessionTiming timing;
    private final AtomicBoolean closed = new AtomicBoolean();
    private final AtomicBoolean reportPending = new AtomicBoolean();
    private volatile HubStartupTrace startupTrace;
    private final ConcurrentHashMap<String, Long> mediaStageStarts = new ConcurrentHashMap<>();
    private volatile HubControlClient control;
    private volatile boolean userExit;
    public void userExit(){userExit=true;}
    private boolean lockConfigured, lockSupported;
    private boolean readyEvidence, fastPrivacyHeartbeat, boundedPrivacyHeartbeat;
    private HubSessionLease(Context context, String host, String leaseId, String mode, String requestId, long started) {
        this.context = context.getApplicationContext(); store = new HubStore(this.context); this.host = host; this.leaseId = leaseId;
        this.mode = mode; this.requestId = requestId; this.timing = new HubSessionTiming(started);
        this.startupTrace = new HubStartupTrace(this.context, started);
    }
    /** Normal tablet exits end capture but rotation retains the existing app. */
    public static boolean active() { return !leases.isEmpty(); }
    public static boolean shouldQuitBackend(boolean managed, String mode, boolean quitOnStop, boolean rotating) {
        return !rotating && (managed ? "tablet".equals(mode) : quitOnStop);
    }
    public static HubSessionLease start(Context context, Intent intent) {
        return start(context, intent.getStringExtra(com.limelight.Game.EXTRA_HOST), intent.getStringExtra("HubLeaseId"),
                intent.getStringExtra(com.limelight.Game.EXTRA_STREAMHUB_MODE), intent.getStringExtra("HubRequestId"),
                intent.getLongExtra("HubConnectionStartedAt", SystemClock.elapsedRealtime()));
    }
    public static HubSessionLease start(Context context, String host, String leaseId, String mode, String requestId, long started) {
        if (leaseId == null || leaseId.isEmpty() || host == null || host.isEmpty()) return null;
        HubSessionLease candidate = new HubSessionLease(context, host, leaseId, mode, requestId == null ? "" : requestId, started);
        HubSessionLease existing = leases.putIfAbsent(leaseId, candidate);
        if (existing != null) {
            candidate.worker.shutdown();
            candidate.reporter.shutdown();
            existing.beginConnection(started);
            return existing;
        }
        candidate.worker.scheduleAtFixedRate(candidate::heartbeat, 0, 1, TimeUnit.SECONDS);
        return candidate;
    }
    private synchronized HubControlClient client() throws Exception {
        if (control == null) {
            HubControlClient candidate = new HubControlClient(host, store.invitation());
            JSONObject caps = candidate.probeInfo().optJSONObject("capabilities");
            lockSupported=caps!=null && caps.optBoolean("remoteDesktopLock");
            readyEvidence = caps != null && caps.optBoolean("liveSessionState");
            fastPrivacyHeartbeat = caps != null && caps.optBoolean("fastPrivacyHeartbeat");
            boundedPrivacyHeartbeat = caps != null && caps.optBoolean("boundedPrivacyHeartbeat");
            control = candidate;
        }
        return control;
    }
    private JSONObject body() throws Exception {
        JSONObject result = new JSONObject().put("leaseId", leaseId).put("mode", mode).put("clientId", store.clientId());
        // Old PC versions reject unknown fields; send readiness only after negotiation.
        if (readyEvidence) result.put("streamReady", timing.mediaConnected());
        if (fastPrivacyHeartbeat && "remote".equals(mode)) result.put("fastPrivacyHeartbeat", true);
        if (boundedPrivacyHeartbeat && "remote".equals(mode)) result.put("boundedPrivacyHeartbeat", true);
        return result;
    }
    private void heartbeat() {
        if (closed.get()) return;
        try {
            client().requestHeartbeat(body());
            if(lockSupported && "remote".equals(mode) && !lockConfigured && timing.mediaConnected()) {
                boolean enabled=context.getSharedPreferences("streamhub_controls",Context.MODE_PRIVATE).getBoolean("lock_on_exit",false);
                client().request("session/desktop",new JSONObject().put("leaseId",leaseId).put("clientId",store.clientId())
                        .put("action","configure").put("enabled",enabled));lockConfigured=true;
            }
            if (!timing.connectionReported() && !requestId.isEmpty() && timing.mediaConnected()
                    && reportPending.compareAndSet(false, true)) {
                try { reporter.execute(() -> { try { reportConnected(); } finally { reportPending.set(false); } }); }
                catch (java.util.concurrent.RejectedExecutionException ignored) { reportPending.set(false); }
            }
        } catch (Exception ignored) { }
    }
    public void connected() {
        if (closed.get()) return;
        if (timing.markMediaConnected())
            android.util.Log.i("HubConnection", "video-ready elapsedMs=" + timing.elapsed(SystemClock.elapsedRealtime()));
        try { worker.execute(this::heartbeat); } catch (java.util.concurrent.RejectedExecutionException ignored) { }
    }
    public void mediaStageStarted(String key) {
        if (!closed.get()) mediaStageStarts.put(key, timing.elapsed(SystemClock.elapsedRealtime()));
    }
    public void mediaStageCompleted(String key) {
        if (closed.get()) return;
        Long started = mediaStageStarts.remove(key);
        if (started != null) HubSessionDiagnostics.mediaStage(context, key,
                Math.max(0, timing.elapsed(SystemClock.elapsedRealtime()) - started));
    }
    public void mediaEvent(String key) {
        if (!closed.get()) HubSessionDiagnostics.mediaEvent(context, key, timing.elapsed(SystemClock.elapsedRealtime()));
    }
    public HubStartupTrace startupTrace() { return startupTrace; }
    public synchronized void firstFrame(HubStartupTrace source) {
        if (source == startupTrace && source.isCurrent() && !closed.get() && timing.markFirstFrame()) {
            long elapsed = timing.elapsed(SystemClock.elapsedRealtime());
            source.firstFrame();
            android.util.Log.i("HubConnection", "first-frame-rendered elapsedMs=" + elapsed);
        }
    }
    /** Reset only the per-video timing state; the host lease and heartbeat remain alive. */
    synchronized void beginConnection(long started) {
        if (!closed.get() && timing.begin(started)) {
            startupTrace = new HubStartupTrace(context, started);
            mediaStageStarts.clear();
            android.util.Log.i("HubConnection", "video-generation-start elapsedMs=0");
        }
    }
    public long connectionElapsedMs() { return timing.elapsed(SystemClock.elapsedRealtime()); }
    private void reportConnected() {
        if (closed.get() || requestId.isEmpty() || !timing.mediaConnected() || timing.connectionReported()) return;
        try {
            JSONObject answer = client().requestLease("requests/" + requestId + "/respond", new JSONObject().put("clientId", store.clientId()).put("action", "connected").put("message", "视频连接已建立"));
            JSONObject request = answer.optJSONObject("request");
            String state = request == null ? "" : request.optString("state");
            // The server returns terminal requests unchanged. Stop reporting
            // those too: retries may confirm a connection, never revive it.
            if ("connected".equals(state) || "cancelled".equals(state) || "expired".equals(state)
                    || "failed".equals(state) || "rejected".equals(state)) timing.markConnectionReported();
        } catch (Exception ignored) { }
    }
    /** Called after native streaming and authenticated Sunshine quit have both returned. */
    public void end() {
        if (!closed.compareAndSet(false, true)) return;
        leases.remove(leaseId, this);
        worker.shutdownNow();
        reporter.shutdownNow();
        // End has its own request so cancelling a heartbeat cannot cancel cleanup.
        try {
            HubControlClient cleanup = new HubControlClient(host, store.invitation());
            for (int attempt = 0; attempt < 2; attempt++) {
                try {
                    JSONObject end=body();
                    if(lockSupported)end.put("exitReason",userExit?"user":"internal").put("lockOnExit",
                            context.getSharedPreferences("streamhub_controls",Context.MODE_PRIVATE).getBoolean("lock_on_exit",false));
                    cleanup.request("session/end",end);break;
                }
                catch (Exception e) { if (attempt == 1) android.util.Log.w("HubConnection", "Lease cleanup deferred to host expiry"); }
            }
        } catch (Exception ignored) { }
    }
    public static void endPrepared(Context context, String host, String leaseId, String mode, String requestId) {
        HubSessionLease lease = leases.get(leaseId);
        if (lease == null) lease = new HubSessionLease(context, host, leaseId, mode, requestId, SystemClock.elapsedRealtime());
        lease.end();
    }
}
