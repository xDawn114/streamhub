package com.limelight.streamhub;

import android.content.Context;
import android.os.SystemClock;
import java.time.Duration;
import org.json.JSONArray;
import org.json.JSONObject;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.annotation.Config;
import org.robolectric.shadows.ShadowSystemClock;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28, application=android.app.Application.class)
public class HubStartupTraceTest {
    private final Context context = RuntimeEnvironment.getApplication();
    private HubStartupTrace begin() {
        HubSessionDiagnostics.begin(context, "remote", "usb");
        return new HubStartupTrace(context, SystemClock.elapsedRealtime());
    }
    private JSONObject data() throws Exception { return new JSONObject(HubSessionDiagnostics.read(context)); }
    private void advance(long ms) { ShadowSystemClock.advanceBy(Duration.ofMillis(ms)); }

    @Test public void replacementRejectsLateEventsStagesAndFirstFrame() throws Exception {
        HubStartupTrace old = begin(); old.stageStarted("app-launch"); advance(100);
        HubStartupTrace current = begin();
        String before = HubSessionDiagnostics.read(context);
        old.stageCompleted("app-launch"); old.event("first-video-unit"); old.firstFrame();
        assertEquals(before, HubSessionDiagnostics.read(context));
        current.event("first-video-unit"); current.firstFrame();
        assertTrue(data().getJSONObject("mediaEventsMs").has("first-video-unit"));
        assertTrue(data().getJSONObject("phases").has("first-frame"));
    }

    @Test public void firstEventsAreImmutableAndRetriesRetainSeparateIntervals() throws Exception {
        HubStartupTrace trace = begin(); trace.event("first-video-unit"); advance(20);
        trace.event("first-video-unit");
        assertEquals(0, data().getJSONObject("mediaEventsMs").getLong("first-video-unit"));
        trace.stageStarted("app-launch"); advance(40); trace.stageCompleted("app-launch");
        advance(10); trace.stageStarted("app-launch"); advance(80); trace.stageCompleted("app-launch");
        JSONArray intervals = data().getJSONArray("mediaIntervalsMs");
        assertEquals(2, intervals.length());
        assertEquals(40, intervals.getJSONObject(0).getLong("endMs") - intervals.getJSONObject(0).getLong("startMs"));
        assertEquals(80, intervals.getJSONObject(1).getLong("endMs") - intervals.getJSONObject(1).getLong("startMs"));
        trace.firstFrame(); advance(25); trace.firstFrame();
        assertEquals(150, data().getJSONObject("phases").getLong("first-frame"));
    }

    @Test public void coverageUnionsNestedAndParallelIntervalsAndClipsAfterFirstRender() throws Exception {
        JSONObject value = new JSONObject().put("phases", new JSONObject().put("first-frame", 1000)
                .put("address-probe-complete", 100)).put("mediaIntervalsMs", new JSONArray()
                .put(interval(200, 700)).put(interval(250, 400)).put(interval(600, 800))
                .put(interval(900, 1200)).put(interval(-1, 100)).put(interval(800, 700)));
        JSONObject coverage = HubSessionDiagnostics.startupCoverage(value);
        assertTrue(coverage.getBoolean("available"));
        assertEquals(800, coverage.getLong("measuredMs"));
        assertEquals(200, coverage.getLong("unattributedMs"));
    }

    @Test public void missingCheckpointsAreNotInventedAndLegacyRecordsRemainUnknown() throws Exception {
        JSONObject value = new JSONObject().put("phases", new JSONObject().put("first-frame", 1000)
                .put("host-ready", 900)).put("mediaIntervalsMs", new JSONArray());
        assertEquals(1000, HubSessionDiagnostics.startupCoverage(value).getLong("unattributedMs"));
        value.remove("mediaIntervalsMs");
        assertFalse(HubSessionDiagnostics.startupCoverage(value).getBoolean("available"));
    }

    @Test public void invalidAndUnboundedInputCannotGrowTheExport() throws Exception {
        HubStartupTrace trace = begin(); String id = HubSessionDiagnostics.traceId(context);
        assertFalse(HubSessionDiagnostics.mediaInterval(context, id, "bad key", 0, 1));
        assertFalse(HubSessionDiagnostics.mediaInterval(context, id, "bad-order", 10, 1));
        assertFalse(HubSessionDiagnostics.mediaInterval(context, id, "too-long", 0, 600001));
        for (int i = 0; i < 80; i++) {
            trace.stageStarted("stage-" + i); advance(1); trace.stageCompleted("stage-" + i);
            trace.event("event-" + i);
        }
        assertEquals(64, data().getJSONArray("mediaIntervalsMs").length());
        assertEquals(32, data().getJSONObject("mediaEventsMs").length());
    }

    private JSONObject interval(long start, long end) throws Exception {
        return new JSONObject().put("startMs", start).put("endMs", end);
    }
}
