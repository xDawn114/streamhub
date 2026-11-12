package com.limelight.streamhub;

import android.content.Context;
import org.json.JSONObject;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.annotation.Config;
import org.robolectric.shadows.ShadowSystemClock;
import java.time.Duration;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28, application=android.app.Application.class)
public class HubSessionDiagnosticsTest {
    @Test public void boundedSamplesRetainRealSeparateRatesWithoutBindingSecrets() throws Exception {
        Context context = RuntimeEnvironment.getApplication();
        new HubStore(context).put("invitation", "fixture-binding-secret");
        HubSessionDiagnostics.begin(context, "tablet", "usb");
        HubSessionDiagnostics.phase(context, "host-ready", 1234);
        HubSessionDiagnostics.phase(context, "prepare-job-created", 200);
        HubSessionDiagnostics.phase(context, "serverinfo-200", 10000);
        HubSessionDiagnostics.phase(context, "first-frame", 5432);
        HubSessionDiagnostics.phase(context, "address-probe-complete", 150);
        HubSessionDiagnostics.phase(context, "prepare-job-created", 200);
        HubSessionDiagnostics.phase(context, "media-identity-confirmed", 1400);
        HubSessionDiagnostics.phase(context, "https-ready", 1700);
        HubSessionDiagnostics.hostStages(context, new JSONObject().put("display", 15000).put("capture", 10000));
        HubSessionDiagnostics.hostStages(context, new JSONObject().put("virtual-screen", 15000)
                .put("serverinfo-200", 10000).put("capture-ready", 10000));
        HubSessionDiagnostics.mediaStage(context, "decoder-config", 6400);
        HubSessionDiagnostics.mediaStage(context, "decoder-create", 4);
        HubSessionDiagnostics.mediaStage(context, "decoder-configure", 18);
        HubSessionDiagnostics.mediaStage(context, "decoder-start", 7);
        HubSessionDiagnostics.mediaStage(context, "decoder-output-format", 6300);
        HubSessionDiagnostics.mediaEvent(context, "first-video-packet", 4100);
        for (int i=0; i<70; i++) {
            ShadowSystemClock.advanceBy(Duration.ofSeconds(6));
            float rendered = i < 69 ? 35 : 90;
            HubSessionDiagnostics.performance(context, 90, 144, 90, 7, 2, 90, 47, rendered, 12, 4, 0, 100);
        }
        String exported = HubSessionDiagnostics.read(context);
        JSONObject value = new JSONObject(exported);
        assertEquals(60, value.getJSONArray("samples").length());
        JSONObject sample = value.getJSONArray("samples").getJSONObject(59);
        assertEquals(144, sample.getInt("panelHz")); assertEquals(47, sample.getInt("receivedFps"));
        assertEquals(90, sample.getInt("renderedFps")); assertEquals(7, sample.getInt("panelModeId"));
        assertEquals(2, sample.getInt("requestedPanelModeId"));
        assertEquals(1234, value.getJSONObject("phases").getLong("host-ready"));
        assertEquals(10000, value.getJSONObject("phases").getLong("serverinfo-200"));
        assertFalse(exported.contains("fixture-binding-secret")); assertFalse(exported.contains("invitation"));
        assertTrue(HubSessionDiagnostics.summary(context).contains("扩展屏 · USB 有线"));
        assertFalse(HubSessionDiagnostics.summary(context).contains("90.0 FPS"));
        JSONObject performance = HubSessionDiagnostics.performanceSummary(context);
        assertEquals("35.0 FPS（中位数）", performance.getJSONArray("rows").getJSONObject(3).getString("value"));
        assertEquals("35.9 FPS", performance.getJSONArray("rows").getJSONObject(4).getString("value"));
        assertEquals("144.0 Hz · mode 7", performance.getJSONArray("rows").getJSONObject(5).getString("value"));
        assertEquals("90.0 Hz · mode 2 · 状态：系统调度", performance.getJSONArray("rows").getJSONObject(6).getString("value"));
        assertEquals("35.0–90.0 FPS", performance.getJSONArray("rows").getJSONObject(9).getString("value"));
        assertEquals("60 个 · 丢帧 0（单点最多 0）", performance.getJSONArray("rows").getJSONObject(10).getString("value"));
        String detail = HubSessionDiagnostics.detail(context);
        assertTrue(detail.contains("实际面板 144.0 Hz（mode 7，请求 90.0 Hz · mode 2 · 状态：系统调度"));
        assertTrue(detail.contains("首帧总耗时 5.4 秒"));
        assertTrue(detail.contains("配置虚拟屏模式 15.0 秒")); assertTrue(detail.contains("捕获就绪 10.0 秒"));
        assertTrue(detail.contains("电脑串流服务首次响应（检查阶段 10.0 秒）"));
        assertFalse(detail.contains("首次 serverinfo=200"));
        org.json.JSONArray timeline = HubSessionDiagnostics.timeline(context);
        assertTrue(hasTimelineRow(timeline, "电脑准备", 1034));
        assertTrue(hasTimelineRow(timeline, "首帧总耗时", 5432));
        assertTrue(hasTimelineRow(timeline, "解码器配置", 6400));
        assertTrue(hasTimelineRow(timeline, "创建解码器", 4));
        assertTrue(hasTimelineRow(timeline, "配置解码器", 18));
        assertTrue(hasTimelineRow(timeline, "启动解码器", 7));
        assertTrue(hasTimelineRow(timeline, "等待输出格式", 6300));
        assertTrue(hasTimelineEvent(timeline, "首个视频包", 4100));
        assertTrue(hasTimelineEvent(timeline, "首帧出画", 5432));
        assertTrue(hasSlowestTimelineRow(timeline, "配置虚拟屏模式", 15000));
        assertFalse(hasSlowestTimelineRow(timeline, "解码器配置", 6400));
        assertFalse(hasSlowestTimelineRow(timeline, "等待输出格式", 6300));
        HubSessionDiagnostics.begin(context, "remote");
        value = new JSONObject(HubSessionDiagnostics.read(context));
        assertEquals(0, value.getJSONArray("samples").length()); assertEquals(0, value.getJSONObject("phases").length());
        assertFalse(HubSessionDiagnostics.performanceSummary(context).getBoolean("available"));
        assertEquals(0, HubSessionDiagnostics.timeline(context).length());
    }

    private static boolean hasTimelineRow(org.json.JSONArray rows, String label, long duration) {
        for (int i = 0; i < rows.length(); i++) {
            JSONObject row = rows.optJSONObject(i);
            if (row != null && label.equals(row.optString("label")) && duration == row.optLong("durationMs", -1)) return true;
        }
        return false;
    }

    private static boolean hasTimelineEvent(org.json.JSONArray rows, String label, long elapsed) {
        for (int i = 0; i < rows.length(); i++) {
            JSONObject row = rows.optJSONObject(i);
            if (row != null && label.equals(row.optString("label")) && elapsed == row.optLong("eventMs", -1)) return true;
        }
        return false;
    }

    private static boolean hasSlowestTimelineRow(org.json.JSONArray rows, String label, long duration) {
        for (int i = 0; i < rows.length(); i++) {
            JSONObject row = rows.optJSONObject(i);
            if (row != null && row.optBoolean("slowest") && label.equals(row.optString("label"))
                    && duration == row.optLong("durationMs", -1)) return true;
        }
        return false;
    }
}
