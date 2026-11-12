package com.limelight.streamhub;

import android.content.Context;
import android.os.SystemClock;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Locale;
import org.json.JSONArray;
import org.json.JSONObject;

/** Bounded numeric diagnostics: never store addresses, invitations, account IDs or tokens. */
public final class HubSessionDiagnostics {
    private static long lastSample;
    private HubSessionDiagnostics() { }
    public static synchronized void begin(Context context, String mode) {
        begin(context, mode, "");
    }
    public static synchronized void begin(Context context, String mode, String route) {
        try {
            JSONObject value = new JSONObject().put("version", 2).put("traceId", java.util.UUID.randomUUID().toString())
                    .put("startedAt", System.currentTimeMillis())
                    .put("mode", "tablet".equals(mode) ? "tablet" : "remote")
                    .put("route", safeRoute(route)).put("phases", new JSONObject())
                    .put("hostStagesMs", new JSONObject()).put("mediaStagesMs", new JSONObject())
                    .put("mediaEventsMs", new JSONObject()).put("mediaIntervalsMs", new JSONArray())
                    .put("samples", new JSONArray());
            new HubStore(context).put("sessionDiagnostics", value.toString()); lastSample = 0;
        } catch (Exception ignored) { }
    }
    public static synchronized void route(Context context, String route) {
        try {
            HubStore store = new HubStore(context); JSONObject value = new JSONObject(store.get("sessionDiagnostics", "{}"));
            if (value.length() == 0) return;
            value.put("route", safeRoute(route)); store.put("sessionDiagnostics", value.toString());
        } catch (Exception ignored) { }
    }
    public static synchronized void phase(Context context, String phase, long elapsed) {
        if (!phase.matches("[a-z0-9-]{1,40}")) return;
        try {
            HubStore store = new HubStore(context); JSONObject value = new JSONObject(store.get("sessionDiagnostics", "{}"));
            JSONObject phases = value.optJSONObject("phases"); if (phases == null || phases.length() >= 16 && !phases.has(phase)) return;
            phases.put(phase, Math.max(0, elapsed)); store.put("sessionDiagnostics", value.toString());
        } catch (Exception ignored) { }
    }

    public static synchronized String traceId(Context context) {
        try { return new JSONObject(read(context)).optString("traceId", ""); }
        catch (Exception ignored) { return ""; }
    }

    /** Return false for an old video generation, so no stale callback is logged as current. */
    public static synchronized boolean phase(Context context, String id, String key, long elapsed) {
        if (!validKey(key) || id.isEmpty() || elapsed < 0 || elapsed > 600000) return false;
        try {
            JSONObject value = new JSONObject(read(context));
            if (!id.equals(value.optString("traceId"))) return false;
            JSONObject phases = value.getJSONObject("phases");
            if (phases.has(key) || phases.length() >= 32) return false;
            phases.put(key, elapsed);
            new HubStore(context).put("sessionDiagnostics", value.toString());
            return true;
        } catch (Exception ignored) { return false; }
    }

    public static synchronized boolean mediaInterval(Context context, String id, String key, long start, long end) {
        if (!validKey(key) || id.isEmpty() || start < 0 || end < start || end > 600000) return false;
        try {
            JSONObject value = new JSONObject(read(context));
            if (!id.equals(value.optString("traceId"))) return false;
            JSONArray intervals = value.getJSONArray("mediaIntervalsMs");
            if (intervals.length() >= 64) return false;
            intervals.put(new JSONObject().put("key", key).put("startMs", start).put("endMs", end));
            value.getJSONObject("mediaStagesMs").put(key, end - start);
            new HubStore(context).put("sessionDiagnostics", value.toString());
            return true;
        } catch (Exception ignored) { return false; }
    }

    public static synchronized boolean mediaEvent(Context context, String id, String key, long elapsed) {
        if (!validKey(key) || id.isEmpty() || elapsed < 0 || elapsed > 600000) return false;
        try {
            JSONObject value = new JSONObject(read(context));
            if (!id.equals(value.optString("traceId"))) return false;
            JSONObject events = value.getJSONObject("mediaEventsMs");
            if (events.has(key) || events.length() >= 32) return false;
            events.put(key, elapsed);
            new HubStore(context).put("sessionDiagnostics", value.toString());
            return true;
        } catch (Exception ignored) { return false; }
    }
    /** Store the host's bounded preparation breakdown without copying addresses or tokens. */
    public static synchronized void hostStages(Context context, JSONObject input) {
        if (input == null) return;
        try {
            HubStore store = new HubStore(context); JSONObject value = new JSONObject(store.get("sessionDiagnostics", "{}"));
            if (value.length() == 0) return;
            JSONObject stages = value.optJSONObject("hostStagesMs");
            if (stages == null) { stages = new JSONObject(); value.put("hostStagesMs", stages); }
            java.util.Iterator<String> keys = input.keys();
            while (keys.hasNext() && stages.length() < 16) {
                String key = keys.next(); long duration = input.optLong(key, -1);
                if (key.matches("[a-z0-9-]{1,40}") && duration >= 0 && duration <= 600000) stages.put(key, duration);
            }
            if (stages.length() > 0) store.put("sessionDiagnostics", value.toString());
        } catch (Exception ignored) { }
    }

    /** Store a bounded media-stage duration without copying addresses or tokens. */
    public static synchronized void mediaStage(Context context, String key, long duration) {
        if (!validKey(key) || duration < 0 || duration > 600000) return;
        try {
            HubStore store = new HubStore(context); JSONObject value = new JSONObject(store.get("sessionDiagnostics", "{}"));
            JSONObject stages = value.optJSONObject("mediaStagesMs");
            if (stages == null) { stages = new JSONObject(); value.put("mediaStagesMs", stages); }
            if (stages.length() < 16 || stages.has(key)) { stages.put(key, duration); store.put("sessionDiagnostics", value.toString()); }
        } catch (Exception ignored) { }
    }

    /** Store the elapsed position of a media event relative to connection start. */
    public static synchronized void mediaEvent(Context context, String key, long elapsed) {
        if (!validKey(key) || elapsed < 0 || elapsed > 600000) return;
        try {
            HubStore store = new HubStore(context); JSONObject value = new JSONObject(store.get("sessionDiagnostics", "{}"));
            JSONObject events = value.optJSONObject("mediaEventsMs");
            if (events == null) { events = new JSONObject(); value.put("mediaEventsMs", events); }
            if (events.length() < 16 || events.has(key)) { events.put(key, elapsed); store.put("sessionDiagnostics", value.toString()); }
        } catch (Exception ignored) { }
    }
    public static synchronized void performance(Context context, float requestedFps, float displayHz,
            float requestedDisplayHz, int panelModeId, int requestedPanelModeId, int hostPreparedFps, float receivedFps,
            float renderedFps, float decodeMs, int rttMs, int lost, int frames) {
        if (!Float.isFinite(requestedFps) || !Float.isFinite(displayHz) || !Float.isFinite(requestedDisplayHz)
                || !Float.isFinite(receivedFps) || !Float.isFinite(renderedFps) || !Float.isFinite(decodeMs)) return;
        long now = SystemClock.elapsedRealtime(); if (lastSample != 0 && now - lastSample < 5000) return; lastSample = now;
        try {
            HubStore store = new HubStore(context); JSONObject value = new JSONObject(store.get("sessionDiagnostics", "{}"));
            JSONArray old = value.optJSONArray("samples"), samples = new JSONArray();
            if (old != null) for (int i = Math.max(0, old.length() - 59); i < old.length(); i++) samples.put(old.get(i));
            JSONObject sample = new JSONObject().put("at", System.currentTimeMillis()).put("requestedFps", requestedFps)
                    .put("panelHz", displayHz).put("requestedPanelHz", requestedDisplayHz).put("panelModeId", panelModeId)
                    .put("requestedPanelModeId", requestedPanelModeId)
                    .put("hostPreparedFps", hostPreparedFps).put("receivedFps", receivedFps)
                    .put("renderedFps", renderedFps).put("decodeMs", decodeMs).put("rttMs", rttMs).put("lostFrames", lost).put("totalFrames", frames);
            samples.put(sample); value.put("samples", samples); store.put("sessionDiagnostics", value.toString());
            android.util.Log.i("HubPerformance", sample.toString());
        } catch (Exception ignored) { }
    }
    public static String read(Context context) { return new HubStore(context).get("sessionDiagnostics", "尚无连接记录"); }

    /** Concise text for the device card; missing measurements remain explicitly unknown. */
    public static String summary(Context context) { return format(context, false); }

    /** Human-readable details for the diagnostic dialog. */
    public static String detail(Context context) { return format(context, true); }

    /** Structured stage rows used by the aligned diagnostic table. */
    public static JSONArray timeline(Context context) {
        JSONArray rows = new JSONArray();
        try {
            JSONObject value = new JSONObject(read(context));
            JSONObject phases = value.optJSONObject("phases");
            long firstFrame = phases == null ? -1 : phases.optLong("first-frame", -1);
            addSummaryDuration(rows, "首帧总耗时", firstFrame);
            long addressProbe = phases == null ? -1 : phases.optLong("address-probe-complete", -1);
            addInterval(rows, "地址探测", 0, addressProbe, 0);
            long prepareStarted = phases == null ? -1 : phases.optLong("prepare-job-created", -1);
            long hostReady = phases == null ? -1 : phases.optLong("host-ready", -1);
            addInterval(rows, "电脑准备", prepareStarted, hostReady, 0);
            JSONObject hostStages = value.optJSONObject("hostStagesMs");
            if (hostStages != null) addHostTimeline(rows, hostStages);
            addInterval(rows, "媒体身份确认", phases, "host-ready", "media-identity-confirmed", 0);
            long secureStarted = phases == null ? -1 : phases.optLong("media-identity-confirmed", -1);
            if (secureStarted < 0) secureStarted = hostReady;
            addInterval(rows, "HTTPS 通道", secureStarted, phases == null ? -1 : phases.optLong("https-ready", -1), 0);
            JSONObject mediaStages = value.optJSONObject("mediaStagesMs");
            if (mediaStages != null) {
                addDuration(rows, "启动电脑串流会话", mediaStages.optLong("host-app-start", -1), 0);
                addDuration(rows, "读取媒体状态", mediaStages.optLong("app-serverinfo", -1), 1);
                addDuration(rows, "判断网络类型", mediaStages.optLong("connection-type", -1), 1);
                addDuration(rows, "请求电脑启动画面", mediaStages.optLong("app-launch", -1), 1);
                addDuration(rows, "请求恢复画面", mediaStages.optLong("app-resume", -1), 1);
                addDuration(rows, "连接重试退避", mediaStages.optLong("app-retry-backoff", -1), 0);
                addDuration(rows, "等待前次连接结束", mediaStages.optLong("connection-semaphore", -1), 0);
                addDuration(rows, "等待原生串流锁", mediaStages.optLong("native-lock-wait", -1), 0);
                addDuration(rows, "启动原生串流", mediaStages.optLong("native-start", -1), 0);
                addDuration(rows, "RTSP 握手", mediaStages.optLong("rtsp-handshake", -1), 0);
                addDuration(rows, "视频串流初始化", mediaStages.optLong("video-initialization", -1), 0);
                addDuration(rows, "视频串流建立", mediaStages.optLong("video-establishment", -1), 0);
                addDuration(rows, "音频初始化", mediaStages.optLong("audio-initialization", -1), 0);
                addDuration(rows, "音频通道建立", mediaStages.optLong("audio-establishment", -1), 0);
                addDuration(rows, "控制通道建立", mediaStages.optLong("control-establishment", -1), 0);
                addDuration(rows, "输入通道建立", mediaStages.optLong("input-establishment", -1), 0);
                addDuration(rows, "解码器配置", mediaStages.optLong("decoder-config", -1), 0);
                addDuration(rows, "创建解码器", mediaStages.optLong("decoder-create", -1), 1);
                addDuration(rows, "配置解码器", mediaStages.optLong("decoder-configure", -1), 1);
                addDuration(rows, "启动解码器", mediaStages.optLong("decoder-start", -1), 1);
                addDuration(rows, "等待输出格式", mediaStages.optLong("decoder-output-format", -1), 1);
            }
            JSONObject events = value.optJSONObject("mediaEventsMs");
            if (events != null) {
                addEvent(rows, "串流页面创建", events.optLong("activity-created", -1));
                addEvent(rows, "请求开始媒体连接", events.optLong("connection-start-requested", -1));
                addEvent(rows, "媒体通道就绪", events.optLong("transport-ready", -1));
                addEvent(rows, "首个完整视频单元", events.optLong("first-video-unit", -1));
                addEvent(rows, "首个视频包", events.optLong("first-video-packet", -1));
                addEvent(rows, "首个关键帧单元", events.optLong("first-keyframe-unit", -1));
                addEvent(rows, "首次解码输出", events.optLong("first-decoder-output", -1));
                if (events.has("decoder-config-failed")) addEvent(rows, "解码器配置失败", events.optLong("decoder-config-failed", -1));
            }
            addEvent(rows, "首帧出画", phases == null ? -1 : phases.optLong("first-frame", -1));
            JSONObject coverage = startupCoverage(value);
            if (coverage.optBoolean("available"))
                addDuration(rows, "尚未归因的等待", coverage.getLong("unattributedMs"), 0);
            markSlowest(rows);
        } catch (Exception ignored) { }
        return rows;
    }

    /** Union of measured ranges, clipped to first render: nested/parallel stages count once. */
    public static JSONObject startupCoverage(JSONObject value) {
        JSONObject result = new JSONObject();
        try {
            JSONObject phases = value.optJSONObject("phases");
            long first = phases == null ? -1 : phases.optLong("first-frame", -1);
            if (first < 0 || first > 600000 || !value.has("mediaIntervalsMs")) return result.put("available", false);
            ArrayList<long[]> ranges = new ArrayList<>();
            String[] checkpoints = {"address-probe-complete", "prepare-job-created", "host-ready",
                    "media-identity-confirmed", "https-ready", "app-list-complete", "intent-delivered"};
            long previous = 0;
            for (String key : checkpoints) {
                long end = phases.optLong(key, -1);
                if (previous >= 0 && end >= previous && end <= first) ranges.add(new long[]{previous, end});
                previous = end;
            }
            JSONArray intervals = value.optJSONArray("mediaIntervalsMs");
            for (int i = 0; intervals != null && i < Math.min(64, intervals.length()); i++) {
                JSONObject interval = intervals.optJSONObject(i); if (interval == null) continue;
                long start = interval.optLong("startMs", -1), end = interval.optLong("endMs", -1);
                if (start >= 0 && end >= start && start < first) ranges.add(new long[]{start, Math.min(first, end)});
            }
            ranges.sort(java.util.Comparator.comparingLong(range -> range[0]));
            long covered = 0, start = -1, end = -1;
            for (long[] range : ranges) {
                if (start < 0) { start = range[0]; end = range[1]; }
                else if (range[0] <= end) end = Math.max(end, range[1]);
                else { covered += end - start; start = range[0]; end = range[1]; }
            }
            if (start >= 0) covered += end - start;
            return result.put("available", true).put("firstRenderMs", first).put("measuredMs", covered)
                    .put("unattributedMs", Math.max(0, first - covered));
        } catch (Exception ignored) { return result; }
    }

    /** Numeric summary values are formatted here so the home and stream views share one source. */
    public static JSONObject performanceSummary(Context context) {
        JSONObject result = new JSONObject();
        try {
            PerformanceSummary value = PerformanceSummary.from(new JSONObject(read(context)).optJSONArray("samples"));
            if (value == null) return result.put("available", false);
            JSONArray rows = new JSONArray();
            addMetric(rows, "请求帧率", number(value.requestedFps) + " FPS");
            addMetric(rows, "主机编码准备", number(value.hostPreparedFps) + " FPS");
            addMetric(rows, "实际接收", number(value.receivedFps) + " FPS（中位数）");
            addMetric(rows, "实际渲染", number(value.renderedFps) + " FPS（中位数）");
            addMetric(rows, "渲染均值", number(value.renderedMean) + " FPS");
            addMetric(rows, "实际面板", number(value.panelHz) + " Hz · mode " + value.panelModeId);
            addMetric(rows, "请求面板", number(value.requestedPanelHz) + " Hz" + requestedModeLabel(value.requestedPanelModeId)
                    + " · 状态：" + panelState(value));
            addMetric(rows, "解码耗时", number(value.decodeMs) + " ms（中位数）");
            addMetric(rows, "网络 RTT", integer(value.rttMs) + " ms（中位数）");
            addMetric(rows, "渲染范围", number(value.renderedMin) + "–" + number(value.renderedMax) + " FPS");
            addMetric(rows, "采样", value.count + " 个 · 丢帧 " + value.lostTotal + "（单点最多 " + value.lostMax + "）");
            return result.put("available", true).put("rows", rows);
        } catch (Exception ignored) { return result; }
    }

    private static String format(Context context, boolean detailed) {
        try {
            JSONObject value = new JSONObject(read(context));
            JSONObject phases = value.optJSONObject("phases");
            JSONArray samples = value.optJSONArray("samples");
            PerformanceSummary performance = PerformanceSummary.from(samples);
            String mode = "tablet".equals(value.optString("mode")) ? "扩展屏" : "远程控制";
            String route = routeLabel(value.optString("route"));
            StringBuilder out = new StringBuilder();
            if (!detailed) {
                out.append(mode).append(" · ").append(route);
                return out.toString();
            }
            out.append("连接：").append(mode).append(" · ").append(route).append('\n');
            out.append("阶段时间线\n");
            JSONArray timeline = timeline(context);
            for (int i = 0; i < timeline.length(); i++) {
                JSONObject row = timeline.optJSONObject(i);
                if (row == null) continue;
                out.append("· ").append(row.optString("label"));
                if ("event".equals(row.optString("kind"))) out.append("（连接后 ").append(duration(row.optLong("eventMs", -1))).append("）");
                else if ("checkpoint".equals(row.optString("kind")))
                    out.append("（检查阶段 ").append(duration(row.optLong("checkpointMs", -1))).append("）");
                else out.append(" ").append(duration(row.optLong("durationMs", -1)));
                out.append('\n');
            }
            out.append("\n帧率与延迟\n");
            if (performance == null) out.append("暂无采样，尚未获得视频统计。\n");
            else {
                out.append("请求 ").append(number(performance.requestedFps)).append(" / 主机准备 ")
                        .append(number(performance.hostPreparedFps)).append(" / 接收中位数 ")
                        .append(number(performance.receivedFps)).append("（均值 ")
                        .append(number(performance.receivedMean)).append("） / 渲染中位数 ")
                        .append(number(performance.renderedFps)).append("（均值 ")
                        .append(number(performance.renderedMean)).append("） FPS\n实际面板 ")
                        .append(number(performance.panelHz)).append(" Hz（mode ")
                        .append(performance.panelModeId).append("，请求 ")
                        .append(number(performance.requestedPanelHz)).append(" Hz")
                        .append(requestedModeLabel(performance.requestedPanelModeId)).append(" · 状态：")
                        .append(panelState(performance)).append("）\n解码中位数 ")
                        .append(number(performance.decodeMs)).append(" ms · RTT 中位数 ")
                        .append(integer(performance.rttMs)).append(" ms\n");
                out.append("渲染范围 ").append(number(performance.renderedMin)).append(" - ")
                        .append(number(performance.renderedMax)).append(" FPS · 样本 ")
                        .append(performance.count).append(" · 采样丢帧合计 ").append(performance.lostTotal)
                        .append(" · 单点最大 ").append(performance.lostMax);
            }
            return out.toString();
        } catch (Exception ignored) {
            return "暂无可读的连接诊断。完成一次连接后再试。";
        }
    }

    private static void addHostTimeline(JSONArray rows, JSONObject stages) throws Exception {
        addDuration(rows, "检查电脑", stages.optLong("checking", -1), 1);
        addDuration(rows, "停止旧引擎", stages.optLong("stop-engine", -1), 1);
        if (stages.has("virtual-screen")) addDuration(rows, "配置虚拟屏模式", stages.optLong("virtual-screen", -1), 1);
        else addDuration(rows, "配置虚拟屏", stages.optLong("display", -1), 1);
        addDuration(rows, "检测虚拟屏输出", stages.optLong("virtual-output", -1), 1);
        if (stages.has("engine-start")) addDuration(rows, "启动引擎进程", stages.optLong("engine-start", -1), 1);
        else addDuration(rows, "启动引擎", stages.optLong("engine", -1), 1);
        if (stages.has("capture-ready")) addDuration(rows, "捕获就绪", stages.optLong("capture-ready", -1), 1);
        else addDuration(rows, "等待捕获", stages.optLong("capture", -1), 1);
        // The service currently reports serverinfo-200 from the same readiness
        // wait as capture-ready. Keep it as a checkpoint so the dialog cannot
        // make the overlapping values look like additive durations.
        addCheckpoint(rows, "电脑串流服务首次响应", stages.optLong("serverinfo-200", -1), 1);
        addDuration(rows, "协商通道", stages.optLong("negotiate", -1), 1);
    }

    private static void addInterval(JSONArray rows, String label, JSONObject phases, String startKey, String endKey, int depth) throws Exception {
        long start = phases == null ? -1 : phases.optLong(startKey, -1);
        long end = phases == null ? -1 : phases.optLong(endKey, -1);
        addInterval(rows, label, start, end, depth);
    }

    private static void addInterval(JSONArray rows, String label, long start, long end, int depth) throws Exception {
        if (start >= 0 && end >= start) rows.put(new JSONObject().put("kind", "duration").put("label", label)
                .put("durationMs", end - start).put("offsetMs", start).put("depth", depth)
                .put("summary", false).put("slowest", false));
    }

    private static void addDuration(JSONArray rows, String label, long duration, int depth) throws Exception {
        if (duration >= 0 && duration <= 600000) rows.put(new JSONObject().put("kind", "duration")
                .put("label", label).put("durationMs", duration).put("offsetMs", JSONObject.NULL).put("depth", depth)
                .put("summary", false).put("slowest", false));
    }

    private static void addSummaryDuration(JSONArray rows, String label, long duration) throws Exception {
        if (duration >= 0 && duration <= 600000) rows.put(new JSONObject().put("kind", "duration")
                .put("label", label).put("durationMs", duration).put("offsetMs", JSONObject.NULL).put("depth", 0)
                .put("summary", true).put("slowest", false));
    }

    private static void addCheckpoint(JSONArray rows, String label, long elapsed, int depth) throws Exception {
        if (elapsed >= 0 && elapsed <= 600000) rows.put(new JSONObject().put("kind", "checkpoint")
                .put("label", label).put("checkpointMs", elapsed).put("depth", depth).put("slowest", false));
    }

    private static void markSlowest(JSONArray rows) throws Exception {
        int slowest = -1;
        long maximum = -1;
        for (int i = 0; i < rows.length(); i++) {
            JSONObject row = rows.optJSONObject(i);
            if (row == null || !"duration".equals(row.optString("kind")) || row.optBoolean("summary")) continue;
            if (isContainer(row.optString("label"))) continue;
            long duration = row.optLong("durationMs", -1);
            if (duration > maximum) { maximum = duration; slowest = i; }
        }
        if (slowest < 0) {
            for (int i = 0; i < rows.length(); i++) {
                JSONObject row = rows.optJSONObject(i);
                if (row == null || !"duration".equals(row.optString("kind")) || row.optBoolean("summary")) continue;
                long duration = row.optLong("durationMs", -1);
                if (duration > maximum) { maximum = duration; slowest = i; }
            }
        }
        if (slowest >= 0) rows.optJSONObject(slowest).put("slowest", true);
    }

    private static boolean isContainer(String label) {
        return "电脑准备".equals(label) || "解码器配置".equals(label);
    }

    private static void addEvent(JSONArray rows, String label, long elapsed) throws Exception {
        if (elapsed >= 0) rows.put(new JSONObject().put("kind", "event").put("label", label).put("eventMs", elapsed));
    }

    private static void addMetric(JSONArray rows, String label, String value) throws Exception {
        rows.put(new JSONObject().put("label", label).put("value", value));
    }

    private static String duration(long millis) {
        if (millis < 0) return "暂无";
        if (millis < 1000) return millis + " ms";
        return String.format(Locale.ROOT, "%.1f 秒", millis / 1000d);
    }

    private static String number(double value) {
        return Double.isFinite(value) ? String.format(Locale.ROOT, "%.1f", value) : "暂无";
    }

    private static String integer(double value) {
        return Double.isFinite(value) ? Long.toString(Math.round(value)) : "暂无";
    }

    private static String requestedModeLabel(int modeId) {
        return modeId > 0 ? " · mode " + modeId : " · Surface 请求";
    }

    private static String panelState(PerformanceSummary value) {
        if (!Double.isFinite(value.requestedPanelHz) || value.requestedPanelHz <= 0) return "未知";
        return HubDisplayModePolicy.isApplied((float) value.panelHz, (float) value.requestedPanelHz,
                value.panelModeId, value.requestedPanelModeId) ? "已应用" : "系统调度";
    }

    private static final class PerformanceSummary {
        final int count;
        final int panelModeId, requestedPanelModeId;
        final double requestedFps, panelHz, requestedPanelHz, hostPreparedFps;
        final double receivedFps, receivedMean, renderedFps, renderedMean, decodeMs, rttMs;
        final double renderedMin, renderedMax;
        final long lostTotal;
        final int lostMax;

        private PerformanceSummary(int count, int panelModeId, int requestedPanelModeId, double requestedFps, double panelHz,
                double requestedPanelHz, double hostPreparedFps, double receivedFps, double receivedMean,
                double renderedFps, double renderedMean, double decodeMs, double rttMs,
                double renderedMin, double renderedMax, long lostTotal, int lostMax) {
            this.count = count; this.panelModeId = panelModeId; this.requestedPanelModeId = requestedPanelModeId;
            this.requestedFps = requestedFps; this.panelHz = panelHz;
            this.requestedPanelHz = requestedPanelHz;
            this.hostPreparedFps = hostPreparedFps; this.receivedFps = receivedFps;
            this.receivedMean = receivedMean; this.renderedFps = renderedFps; this.renderedMean = renderedMean;
            this.decodeMs = decodeMs; this.rttMs = rttMs; this.renderedMin = renderedMin; this.renderedMax = renderedMax;
            this.lostTotal = lostTotal; this.lostMax = lostMax;
        }

        static PerformanceSummary from(JSONArray samples) {
            if (samples == null || samples.length() == 0) return null;
            ArrayList<Double> requested = new ArrayList<>(), requestedPanel = new ArrayList<>(), prepared = new ArrayList<>();
            ArrayList<Double> received = new ArrayList<>(), rendered = new ArrayList<>(), decode = new ArrayList<>(), rtt = new ArrayList<>();
            long lostTotal = 0; int lostMax = 0, panelModeId = -1;
            JSONObject latest = null;
            for (int i = 0; i < samples.length(); i++) {
                JSONObject sample = samples.optJSONObject(i); if (sample == null) continue;
                latest = sample;
                add(requested, sample.optDouble("requestedFps", Double.NaN));
                add(requestedPanel, sample.optDouble("requestedPanelHz", Double.NaN));
                add(prepared, sample.optDouble("hostPreparedFps", Double.NaN));
                add(received, sample.optDouble("receivedFps", Double.NaN));
                add(rendered, sample.optDouble("renderedFps", Double.NaN));
                add(decode, sample.optDouble("decodeMs", Double.NaN));
                add(rtt, sample.optDouble("rttMs", Double.NaN));
                int lost = Math.max(0, sample.optInt("lostFrames", 0));
                lostTotal += lost; lostMax = Math.max(lostMax, lost); panelModeId = sample.optInt("panelModeId", -1);
            }
            if (rendered.isEmpty()) return null;
            Collections.sort(rendered);
            return new PerformanceSummary(rendered.size(), panelModeId, latest.optInt("requestedPanelModeId", 0),
                    median(requested), latest.optDouble("panelHz", Double.NaN),
                    latest.optDouble("requestedPanelHz", Double.NaN), median(prepared), median(received), mean(received), median(rendered),
                    mean(rendered), median(decode), median(rtt), rendered.get(0), rendered.get(rendered.size() - 1),
                    lostTotal, lostMax);
        }

        private static void add(ArrayList<Double> values, double value) {
            if (Double.isFinite(value)) values.add(value);
        }

        private static double median(ArrayList<Double> values) {
            if (values.isEmpty()) return Double.NaN;
            Collections.sort(values);
            int middle = values.size() / 2;
            return values.size() % 2 == 0 ? (values.get(middle - 1) + values.get(middle)) / 2d : values.get(middle);
        }

        private static double mean(ArrayList<Double> values) {
            if (values.isEmpty()) return Double.NaN;
            double total = 0; for (double value : values) total += value;
            return total / values.size();
        }
    }

    private static String safeRoute(String route) {
        return "usb".equals(route) || "lan".equals(route) || "remote".equals(route) ? route : "";
    }

    private static boolean validKey(String key) {
        return key != null && key.matches("[a-z0-9-]{1,40}");
    }

    private static String routeLabel(String route) {
        if ("usb".equals(route)) return "USB 有线";
        if ("remote".equals(route)) return "异地网络";
        if ("lan".equals(route)) return "局域网 / Wi-Fi";
        return "历史记录 · 通道未知";
    }
}
