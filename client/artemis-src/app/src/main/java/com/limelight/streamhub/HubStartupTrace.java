package com.limelight.streamhub;

import android.content.Context;
import android.os.SystemClock;
import java.util.concurrent.ConcurrentHashMap;

/** One video generation's numeric startup trace; old callbacks cannot edit a replacement. */
public final class HubStartupTrace {
    private final Context context;
    private final String id;
    private final long started;
    private final ConcurrentHashMap<String, Long> stages = new ConcurrentHashMap<>();

    public HubStartupTrace(Context context, long started) {
        this.context = context.getApplicationContext();
        this.started = started;
        this.id = HubSessionDiagnostics.traceId(this.context);
    }

    public boolean isCurrent() {
        return !id.isEmpty() && id.equals(HubSessionDiagnostics.traceId(context));
    }

    public long elapsed() { return Math.max(0, SystemClock.elapsedRealtime() - started); }

    public void stageStarted(String key) {
        if (isCurrent() && key != null && key.matches("[a-z0-9-]{1,40}") && stages.size() < 48)
            stages.putIfAbsent(key, elapsed());
    }

    public void stageCompleted(String key) {
        if (key == null) return;
        Long begin = stages.remove(key);
        if (begin == null || !isCurrent()) return;
        long end = elapsed();
        if (HubSessionDiagnostics.mediaInterval(context, id, key, begin, end))
            android.util.Log.i("HubStartup", "trace=" + id + " stage=" + key +
                    " startMs=" + begin + " endMs=" + end);
    }

    public void event(String key) {
        long at = elapsed();
        if (HubSessionDiagnostics.mediaEvent(context, id, key, at))
            android.util.Log.i("HubStartup", "trace=" + id + " event=" + key + " elapsedMs=" + at);
    }

    public void firstFrame() {
        if (HubSessionDiagnostics.phase(context, id, "first-frame", elapsed())) event("first-frame-rendered");
    }
}
