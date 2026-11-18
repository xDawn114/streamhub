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
    private static final Object endQueueGate = new Object();
    private final ScheduledExecutorService worker = Executors.newSingleThreadScheduledExecutor();
    private final java.util.concurrent.ExecutorService reporter = Executors.newSingleThreadExecutor();
    private final HubStore store;
    private final Context context;
    private final String host, leaseId, mode, requestId;
    private final HubSessionEndQueue.Owner owner;
    private final HubSessionTiming timing;
    private final AtomicBoolean closed = new AtomicBoolean();
    private final AtomicBoolean reportPending = new AtomicBoolean();
    private volatile HubStartupTrace startupTrace;
    private final ConcurrentHashMap<String, Long> mediaStageStarts = new ConcurrentHashMap<>();
    private volatile HubControlClient control;
    private volatile boolean userExit;
    public void userExit(){userExit=true;}
    private boolean lockConfigured, lockSupported, hostManagedPolicy;
    private volatile boolean remoteBackendRelease;
    private boolean readyEvidence, fastPrivacyHeartbeat, boundedPrivacyHeartbeat;
    private HubSessionLease(Context context, String host, String leaseId, String mode, String requestId, long started) {
        this.context = context.getApplicationContext(); store = new HubStore(this.context); this.host = host; this.leaseId = leaseId;
        this.mode = mode; this.requestId = requestId; this.timing = new HubSessionTiming(started);
        this.owner = owner(store);
        this.startupTrace = new HubStartupTrace(this.context, started);
    }
    /** Tablet still uses media quit; remote cleanup is owned by the host lease. */
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
            HubControlClient candidate = scopedClient();
            JSONObject info = candidate.probeInfo(); store.rememberHostPolicy(info);
            JSONObject caps = info.optJSONObject("capabilities");
            hostManagedPolicy = caps != null && caps.optBoolean("hostManagedPolicy");
            rememberCapabilities(info);
            lockSupported=caps!=null && caps.optBoolean("remoteDesktopLock");
            readyEvidence = caps != null && caps.optBoolean("liveSessionState");
            fastPrivacyHeartbeat = caps != null && caps.optBoolean("fastPrivacyHeartbeat");
            boundedPrivacyHeartbeat = caps != null && caps.optBoolean("boundedPrivacyHeartbeat");
            control = candidate;
        }
        return control;
    }
    private JSONObject body() throws Exception {
        JSONObject result = new JSONObject().put("leaseId", leaseId).put("mode", mode).put("clientId", owner.client);
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
            if(lockSupported && !hostManagedPolicy && "remote".equals(mode) && !lockConfigured && timing.mediaConnected()) {
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
        long generation = timing.pendingReportGeneration();
        if (closed.get() || requestId.isEmpty() || generation < 0) return;
        try {
            JSONObject answer = client().requestLease("requests/" + requestId + "/respond", new JSONObject().put("clientId", store.clientId()).put("action", "connected").put("message", "视频连接已建立"));
            JSONObject request = answer.optJSONObject("request");
            String state = request == null ? "" : request.optString("state");
            // The server returns terminal requests unchanged. Stop reporting
            // those too: retries may confirm a connection, never revive it.
            if ("connected".equals(state) || "cancelled".equals(state) || "expired".equals(state)
                    || "failed".equals(state) || "rejected".equals(state)) timing.markConnectionReported(generation);
        } catch (Exception ignored) { }
    }
    /** An unused prepared lease must not close a backend application. */
    public void end() { end(false); }

    /** Called after this activity has finished stopping native streaming. */
    public void endAfterStream() { end(true); }

    private void end(boolean streamAttempted) {
        if (!closed.compareAndSet(false, true)) return;
        leases.remove(leaseId, this);
        worker.shutdownNow();
        reporter.shutdownNow();
        // End has its own request so cancelling a heartbeat cannot cancel cleanup.
        boolean releaseBackend = streamAttempted && "remote".equals(mode) && remoteBackendRelease;
        try {
            if (!sameOwner(owner, owner(store))) throw new IllegalStateException("Binding changed before session cleanup");
            if (releaseBackend) {
                synchronized (endQueueGate) {
                    endQueue(context).remember(owner, leaseId, userExit);
                    HubControlClient cleanup = scopedClient();
                    for (int attempt = 0; attempt < 2; attempt++) {
                        try {
                            endQueue(context).retry(owner, true, cleanup::requestSessionEnd, () -> { });
                            return;
                        } catch (Exception error) {
                            if (attempt == 1) android.util.Log.w("HubConnection", "Remote backend cleanup pending; retained exact lease for retry");
                        }
                    }
                }
                return;
            }
            HubControlClient cleanup = scopedClient();
            for (int attempt = 0; attempt < 2; attempt++) {
                try {
                    JSONObject end=body();
                    if(lockSupported) {
                        end.put("exitReason",userExit?"user":"internal");
                        if (!hostManagedPolicy) end.put("lockOnExit",context.getSharedPreferences("streamhub_controls",Context.MODE_PRIVATE).getBoolean("lock_on_exit",false));
                    }
                    cleanup.requestSessionEnd(end);break;
                }
                catch (Exception e) { if (attempt == 1) android.util.Log.w("HubConnection", "Lease cleanup deferred to host expiry"); }
            }
        } catch (Exception error) {
            android.util.Log.w("HubConnection", "Lease cleanup not confirmed; host ownership was not bypassed");
        }
    }

    static boolean supportsBackendRelease(JSONObject info) {
        JSONObject caps = info.optJSONObject("capabilities");
        return caps != null && caps.optBoolean("remoteBackendRelease");
    }
    void rememberCapabilities(JSONObject info) { remoteBackendRelease = supportsBackendRelease(info); }
    private HubControlClient scopedClient() throws Exception {
        // Match identity and capture immutable transport trust atomically with
        // binding edits. A later edit cannot substitute the new PC's credentials.
        synchronized (HubStore.class) {
            if (!sameOwner(owner, owner(store))) throw new IllegalStateException("Binding changed before lease request");
            return new HubControlClient(host, store.invitation());
        }
    }
    private static HubSessionEndQueue.Owner owner(HubStore store) {
        return new HubSessionEndQueue.Owner(store.get("deviceId", ""), store.clientId(), store.get("bindingRevision", ""));
    }
    private static boolean sameOwner(HubSessionEndQueue.Owner a, HubSessionEndQueue.Owner b) {
        return a.device.equals(b.device) && a.client.equals(b.client) && a.revision.equals(b.revision);
    }
    private static HubSessionEndQueue endQueue(Context context) {
        android.content.SharedPreferences preferences = context.getSharedPreferences("streamhub-session-cleanup", Context.MODE_PRIVATE);
        return new HubSessionEndQueue(new HubSessionEndQueue.Storage() {
            @Override public String read() { return preferences.getString("pendingRemoteEnd", ""); }
            @Override public boolean write(String value) { return preferences.edit().putString("pendingRemoteEnd", value).commit(); }
        });
    }
    static boolean retryPending(Context context, HubControlClient control, JSONObject info,
                                HubSessionEndQueue.Cancellation cancellation) throws Exception {
        synchronized (endQueueGate) {
            HubSessionEndQueue.Owner current = owner(new HubStore(context));
            if (!current.device.equals(info.optString("deviceId"))) throw new IllegalStateException("Binding changed before session cleanup");
            return endQueue(context).retry(current, supportsBackendRelease(info),
                    control::requestSessionEnd, cancellation);
        }
    }
    public static void endPrepared(Context context, String host, String leaseId, String mode, String requestId) {
        HubSessionLease lease = leases.get(leaseId);
        if (lease == null) lease = new HubSessionLease(context, host, leaseId, mode, requestId, SystemClock.elapsedRealtime());
        lease.end();
    }
}
