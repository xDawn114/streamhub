package com.limelight.streamhub;

import android.app.Activity;
import android.os.Build;
import android.content.Intent;
import android.view.Display;
import android.util.Base64;
import com.limelight.Game;
import com.limelight.binding.PlatformBinding;
import com.limelight.computers.IdentityManager;
import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.nvstream.http.NvApp;
import com.limelight.nvstream.http.NvHTTP;
import com.limelight.nvstream.http.PairingManager;
import org.json.JSONObject;
import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.security.cert.CertificateFactory;
import java.security.cert.X509Certificate;
import java.util.List;
import java.util.LinkedHashSet;
import java.util.concurrent.FutureTask;
import java.util.concurrent.TimeUnit;
import android.os.SystemClock;
import java.util.UUID;
import java.util.concurrent.CancellationException;

/** The product binding authorizes the PC to approve the normal Moonlight certificate handshake. */
public final class HubSessionCoordinator {
    public interface Progress { void update(String message); }
    public interface RemoteFallback { List<String> enableAndGetHosts(HubConnectBudget budget) throws Exception; }
    private final Activity activity;
    private final HubStore store;
    private final Progress progress;
    private final RemoteFallback remoteFallback;
    private String selectedHost;
    public String selectedHost() { return selectedHost == null ? "" : selectedHost; }
    private volatile boolean cancelled;
    private volatile HubControlClient activeControl;
    private volatile NvHTTP activeHttp;
    private final java.util.Set<HubControlClient> probes = java.util.concurrent.ConcurrentHashMap.newKeySet();
    private final String bindingRevision, transportRevision;
    private JSONObject selectedInfo;
    private String jobId = "", leaseId = "", requestId = "";
    public void cancel() {
        cancelled = true;
        HubControlClient control = activeControl; if (control != null) control.cancel();
        for (HubControlClient probe : probes) probe.cancel();
        NvHTTP http = activeHttp; if (http != null) http.cancelProductOperations();
    }
    public boolean isCancelled() { return cancelled; }
    private void checkCancelled() { if (!transportRevision.equals(store.get("transportRevision", ""))) throw new CancellationException("连接通道已改变，请重新连接。"); if (cancelled || Thread.currentThread().isInterrupted()) throw new CancellationException("已取消连接。"); }
    public HubSessionCoordinator(Activity activity, Progress progress) { this(activity, progress, null); }
    public HubSessionCoordinator(Activity activity, Progress progress, RemoteFallback remoteFallback) {
        this.activity = activity; store = new HubStore(activity); this.progress = progress; this.remoteFallback = remoteFallback;
        bindingRevision = store.get("bindingRevision", ""); transportRevision = store.get("transportRevision", "");
    }
    private static final class ControlMatch {
        final HubControlClient control; final JSONObject info;
        ControlMatch(HubControlClient control, JSONObject info) { this.control = control; this.info = info; }
    }
    private HubControlClient probeRound(List<String> hosts, HubConnectBudget budget, long maximumMs) throws Exception {
        final String invitation = store.invitation(), expected = store.get("deviceId", "");
        java.util.concurrent.atomic.AtomicBoolean ended = new java.util.concurrent.atomic.AtomicBoolean();
        HubControlClient winner = null;
        try {
            HubProbeRace.Match<ControlMatch> match = HubTransportProbe.first(hosts, host -> {
                checkCancelled();
                HubControlClient control = new HubControlClient(host, invitation); probes.add(control);
                if (ended.get()) { probes.remove(control); control.cancel(); throw new CancellationException("地址检测已结束。"); }
                try {
                    JSONObject info = control.probeInfo(); checkCancelled();
                    if (!expected.isEmpty() && !expected.equals(info.optString("deviceId"))) throw new IOException("这不是已绑定的电脑。");
                    if (ended.get()) throw new CancellationException("地址检测已结束。");
                    return new ControlMatch(control, info);
                } catch (Exception error) { probes.remove(control); control.cancel(); throw error; }
            }, () -> cancelled, budget.remaining(maximumMs));
            budget.check();
            synchronized (HubStore.class) {
                checkCancelled();
                if (!bindingRevision.equals(store.get("bindingRevision", ""))) throw new CancellationException("电脑绑定已改变，请重新连接。");
                selectedHost = match.host; selectedInfo = match.value.info; activeControl = winner = match.value.control;
                store.mergeAddresses(selectedInfo.optJSONArray("addresses"), selectedHost);
                store.put("lastHost", selectedHost); store.put("deviceId", selectedInfo.optString("deviceId", expected));
                store.put("deviceName", selectedInfo.optString("deviceName", selectedInfo.optString("name", "我的电脑")));
            }
            return winner;
        } finally {
            ended.set(true);
            for (HubControlClient probe : probes) { if (probe != winner) probe.cancel(); probes.remove(probe); }
        }
    }
    private HubControlClient findControl(String route, boolean allowRemoteFallback) throws Exception {
        HubConnectBudget budget = new HubConnectBudget(allowRemoteFallback ? 45000 : 3500, () -> cancelled);
        try {
            try { return probeRound(store.candidates(route), budget, 6000); }
            catch (CancellationException error) { throw error; }
            catch (Exception first) {
                if (!"usb".equals(route)) throw first;
                progress.update("正在更新 USB 连接地址…");
                List<String> teachers = store.candidates("auto"); teachers.removeAll(store.candidates("usb"));
                // Authenticated metadata may teach a USB address. Never prepare
                // media on the alternate route used only for this discovery.
                probeRound(teachers, budget, 3500);
                return probeRound(store.candidates("usb"), budget, 3500);
            }
        } catch (CancellationException error) { throw error; }
        catch (Exception failure) {
            if (allowRemoteFallback && remoteFallback != null && store.transport().transport().usesAccount() && !"usb".equals(route)) {
                progress.update("本地路径不可达，正在自动建立异地连接…");
                List<String> discovered = remoteFallback.enableAndGetHosts(budget);
                budget.check();
                LinkedHashSet<String> candidates = new LinkedHashSet<>(discovered);
                candidates.addAll(store.candidates("remote"));
                List<String> remote = new java.util.ArrayList<>();
                for (String host : candidates) if (HubRoutes.isUsableIpv4(host) && ("self-hosted".equals(store.transport().provider) || "remote".equals(HubRoutes.type(host)))) remote.add(host);
                HubControlClient selected = probeRound(remote, budget, 6000);
                progress.update("已自动切换到安全异地连接"); return selected;
            }
            throw new IOException("无法连接电脑，请确认电脑在线和连接方式。" + failure.getMessage(), failure);
        }
    }
    public JSONObject check() throws Exception { findControl("auto", false); return selectedInfo; }
    /** Validation uses proposed trust in memory; failed attempts never replace saved trust. */
    public void bind(String addresses, String proposedInvitation) throws Exception {
        String trust = proposedInvitation.isEmpty() ? store.invitation() : proposedInvitation;
        java.util.concurrent.atomic.AtomicBoolean ended = new java.util.concurrent.atomic.AtomicBoolean();
        try {
            HubProbeRace.Match<JSONObject> match = HubTransportProbe.first(store.transport().transport().candidates(HubRoutes.usableAddresses(addresses), store.transport(), "auto"), host -> {
                HubControlClient client = new HubControlClient(host, trust); probes.add(client);
                try { if (ended.get()) throw new CancellationException(); return client.probeInfo(); }
                finally { client.cancel(); probes.remove(client); }
            }, () -> cancelled, 6000);
            synchronized (HubStore.class) {
                checkCancelled();
                if (!bindingRevision.equals(store.get("bindingRevision", ""))) throw new CancellationException("绑定已改变，请重试。");
                if (proposedInvitation.isEmpty() && !store.get("deviceId", "").equals(match.value.optString("deviceId")))
                    throw new IOException("电脑身份与原绑定不一致，原绑定已保留。");
                store.acceptBinding(trust, match.host, match.value, () -> cancelled);
            }
        } finally { ended.set(true); for (HubControlClient probe : probes) { probe.cancel(); probes.remove(probe); } }
    }
    private String certKey(String mode) { return "cert-" + store.get("deviceId", "computer") + "-" + mode; }
    private X509Certificate certificate(String mode) throws Exception {
        String der = store.get(certKey(mode), "");
        return der.isEmpty() ? null : (X509Certificate) CertificateFactory.getInstance("X.509").generateCertificate(new ByteArrayInputStream(Base64.decode(der, Base64.NO_WRAP)));
    }
    public Intent prepare(String mode, int orientation, String link, boolean autoRotation) throws Exception {
        return prepare(mode, orientation, link, autoRotation, "");
    }
    public Intent prepare(String mode, int orientation, String link, boolean autoRotation, String pcRequestId) throws Exception {
        return prepare(mode, orientation, link, autoRotation, pcRequestId, "");
    }
    public Intent prepare(String mode, int orientation, String link, boolean autoRotation, String pcRequestId, String requestedLeaseId) throws Exception {
        return prepare(mode, orientation, link, autoRotation, pcRequestId, requestedLeaseId, 0, 0, 0);
    }
    public Intent prepare(String mode, int orientation, String link, boolean autoRotation, String pcRequestId, String requestedLeaseId,
                          int targetWidth, int targetHeight, int targetFps) throws Exception {
        requestId = pcRequestId == null ? "" : pcRequestId;
        boolean delivered = false;
        final long started = SystemClock.elapsedRealtime();
        HubSessionDiagnostics.begin(activity, mode);
        try {
        checkCancelled();
        if (!"remote".equals(mode) && !"tablet".equals(mode)) throw new IOException("连接模式无效。");
        if (!Game.awaitHubConnectionStop(5000)) throw new IOException("上次串流仍在结束，请稍后重试。");
        progress.update("正在检查已绑定的电脑…");
        HubControlClient control = findControl(link, true);
        logPhase(started, "address-probe-complete");
        HubSessionDiagnostics.route(activity, HubRoutes.type(selectedHost));
        progress.update("已选择 " + HubRoutes.label(selectedHost) + " · " + selectedHost);
        // Sunshine may keep the launched desktop busy after Android has stopped
        // its video connection. End only the previous mode recorded by this APK,
        // using its own paired identity, before preparing a fresh connection.
        String previousMode = store.get("lastMode", "");
        boolean reusingLease = requestedLeaseId != null && !requestedLeaseId.isEmpty();
        if (!reusingLease && ("remote".equals(previousMode) || "tablet".equals(previousMode)) && certificate(previousMode) != null) {
            JSONObject previous = selectedInfo.optJSONObject(previousMode);
            if (previous != null && previous.optBoolean("backendBusy", previous.optBoolean("streaming"))) {
                progress.update("正在结束本设备上次的串流会话…");
                finishPrevious();
                HubSessionDrain.awaitIdle(() -> {
                    checkCancelled();
                    JSONObject current = control.request("info", null).optJSONObject(previousMode);
                    if (current == null) throw new IOException("电脑未返回上次会话状态，请重试。");
                    return !current.optBoolean("backendBusy", current.optBoolean("streaming"));
                }, ms -> { checkCancelled(); Thread.sleep(ms); checkCancelled(); });
            }
        }
        progress.update(reusingLease ? "正在调整画面并恢复视频…" : "正在准备" + ("tablet".equals(mode) ? "副屏" : "远程桌面") + "，首次初始化可能需要约一分钟…");
        checkCancelled();
        // During sensor rotation the old video session has already stopped, but
        // its host lease is intentionally kept alive. Reusing that lease lets
        // HostRuntime hot-reconfigure the virtual display without detaching the
        // driver or restarting Sunshine.
        leaseId = requestedLeaseId == null || requestedLeaseId.isEmpty() ? UUID.randomUUID().toString() : requestedLeaseId;
        if (!leaseId.matches("[A-Za-z0-9_-]{8,128}")) throw new IOException("旋转会话标识无效，请重新连接。");
        String resolvedRoute = HubRoutes.type(selectedHost);
        // Coalesce rotations that happened while the old native stream stopped
        // or while discovery ran; prepare the current display, not an old event.
        if (reusingLease && autoRotation) orientation = HubDisplayCapabilities.currentOrientation(activity);
        String capabilityRoute = "tablet".equals(mode) ? resolvedRoute : "usb";
        JSONObject prepare = HubDisplayCapabilities.describe(activity, orientation, capabilityRoute).put("mode", mode).put("orientation", orientation)
                .put("codecs", HubDisplayCapabilities.codecs()).put("clientId", store.clientId()).put("leaseId", leaseId);
        if (targetWidth > 0 || targetHeight > 0 || targetFps > 0) {
            if (!reusingLease || !HubStreamResolution.valid(targetWidth, targetHeight, targetFps)) throw new IOException("当前分辨率调整参数无效，请重新连接。");
            JSONObject capabilities = selectedInfo.optJSONObject("capabilities");
            if (capabilities == null || !capabilities.optBoolean("streamResolution")) throw new IOException("电脑端需要更新后才能在串流中调整分辨率。");
            if ("tablet".equals(mode) && ((orientation == 90 || orientation == 270) != (targetHeight > targetWidth))) {
                int swap = targetWidth; targetWidth = targetHeight; targetHeight = swap;
            }
            prepare.put("width",targetWidth).put("height",targetHeight).put("refreshRate",targetFps);
            if ("remote".equals(mode)) prepare.put("streamSizeOverride",true);
        }
        if (!requestId.isEmpty()) prepare.put("requestId", requestId);
        JSONObject created = control.request("prepare-jobs", prepare);
        jobId = created.getString("jobId");
        logPhase(started, "prepare-job-created");
        JSONObject ready = null;
        String lastJobStage = "";
        int pendingPolls = 0;
        while (SystemClock.elapsedRealtime() - started < 240000) {
            checkCancelled();
            JSONObject job = control.request("jobs/" + jobId, null);
            String state = job.optString("state"), message = job.optString("message", "正在准备连接…");
            HubSessionDiagnostics.hostStages(activity, job.optJSONObject("stageDurationsMs"));
            String stage = job.optString("stage", "");
            if (!stage.isEmpty() && !stage.equals(lastJobStage)) {
                lastJobStage = stage;
                android.util.Log.i("HubConnection", "host-stage=" + stage +
                        " clientElapsedMs=" + (SystemClock.elapsedRealtime() - started) +
                        " stageElapsedMs=" + job.optLong("stageElapsedMs", -1));
            }
            long elapsed = job.optLong("elapsedMs", SystemClock.elapsedRealtime() - started);
            progress.update(message + " · " + Math.max(0, elapsed / 1000) + " 秒");
            if ("succeeded".equals(state)) {
                ready = job.getJSONObject("result");
                HubSessionDiagnostics.hostStages(activity, ready.optJSONObject("hostStagesMs"));
                break;
            }
            if ("failed".equals(state)) throw new IOException(message);
            if ("cancelled".equals(state)) throw new CancellationException(message);
            Thread.sleep(HubJobPollPolicy.delayAfterPending(++pendingPolls));
        }
        if (ready == null) throw new IOException("准备连接超时，请检查电脑状态后重试。");
        logPhase(started, "host-ready");
        checkCancelled();
        HubSessionLease.start(activity, selectedHost, leaseId, mode, requestId, started);
        int port = ready.optInt("httpPort", "tablet".equals(mode) ? 49089 : 48989);
        int httpsPort = ready.optInt("httpsPort", port - 5);
        String uniqueId = new IdentityManager(activity).getUniqueId();
        com.limelight.nvstream.http.LimelightCryptoProvider crypto = PlatformBinding.getCryptoProvider(activity);
        X509Certificate recovered = HubMediaIdentity.recover(body -> control.request("media-identity", body), mode, crypto.getClientCertificate());
        if (recovered != null) {
            saveMediaCertificate(mode, recovered);
            logPhase(started, "media-identity-confirmed");
        }
        NvHTTP http = new NvHTTP(new ComputerDetails.AddressTuple(selectedHost, port), httpsPort, uniqueId, certificate(mode), crypto);
        activeHttp = http;
        http.boundProductOperations();
        String serverInfo = http.getServerInfo(true);
        ComputerDetails details = http.getComputerDetails(serverInfo);
        logPhase(started, "https-ready");
        if (details.pairState != PairingManager.PairState.PAIRED || certificate(mode) == null) {
            progress.update("正在为" + ("tablet".equals(mode) ? "副屏" : "主屏") + "建立加密配对…");
            final String pin = PairingManager.generatePinString();
            final String pairingInfo = serverInfo;
            FutureTask<PairingManager.PairState> pairing = new FutureTask<>(() -> http.getPairingManager().pair(pairingInfo, pin, null));
            Thread pairThread = new Thread(pairing, "StreamHub-Pair"); pairThread.setDaemon(true); pairThread.start();
            Exception approvalError = null;
            for (int attempt = 0; attempt < 6 && !pairing.isDone(); attempt++) {
                checkCancelled();
                Thread.sleep(400);
                try { control.request("pair", new JSONObject().put("mode", mode).put("pin", pin).put("name", "我控制 · " + Build.MODEL)); approvalError = null; break; }
                catch (Exception e) { approvalError = e; }
            }
            try {
                if (approvalError != null) throw new IOException("电脑未能确认配对，请重试。", approvalError);
                if (pairing.get(50, TimeUnit.SECONDS) != PairingManager.PairState.PAIRED) throw new IOException("加密配对未完成，请重试。");
            } catch (Exception e) { http.cancelProductOperations(); pairing.cancel(true); throw e; }
            X509Certificate paired = http.getPairingManager().getPairedCert();
            if (paired == null) throw new IOException("配对没有返回电脑证书。");
            saveMediaCertificate(mode, paired);
            serverInfo = http.getServerInfo(true); details = http.getComputerDetails(serverInfo);
            logPhase(started, "pairing-complete");
        } else {
            logPhase(started, "pairing-existing");
        }
        List<NvApp> apps = http.getAppList(); NvApp selected = null;
        logPhase(started, "app-list-complete");
        checkCancelled();
        for (NvApp app : apps) {
            String name = app.getAppName();
            boolean matches = "tablet".equals(mode) ? ("平板副屏".equals(name) || "Tablet Second Screen".equalsIgnoreCase(name))
                    : ("远程桌面".equals(name) || "远程主屏".equals(name) || "Desktop".equalsIgnoreCase(name));
            if (matches) { selected = app; break; }
        }
        if (selected == null) throw new IOException("电脑没有提供所选模式，请修复电脑端组件。");
        Intent intent = new Intent(activity, Game.class);
        intent.putExtra(Game.EXTRA_HOST, selectedHost); intent.putExtra(Game.EXTRA_PORT, port); intent.putExtra(Game.EXTRA_HTTPS_PORT, details.httpsPort);
        intent.putExtra(Game.EXTRA_APP_NAME, selected.getAppName()); intent.putExtra(Game.EXTRA_APP_UUID, selected.getAppUUID()); intent.putExtra(Game.EXTRA_APP_ID, selected.getAppId());
        intent.putExtra(Game.EXTRA_APP_HDR, selected.isHdrSupported()); intent.putExtra(Game.EXTRA_UNIQUEID, uniqueId);
        intent.putExtra(Game.EXTRA_PC_UUID, details.uuid); intent.putExtra(Game.EXTRA_PC_NAME, store.get("deviceName", "我的电脑"));
        intent.putExtra(Game.EXTRA_SERVER_CERT, certificate(mode).getEncoded()); intent.putExtra(Game.EXTRA_STREAMHUB_MODE, mode);
        intent.putExtra("HubManaged", true); intent.putExtra("HubAutoRotate", autoRotation); intent.putExtra("HubOrientation", orientation); intent.putExtra("HubLink", link);
        // Keep the actual route selected by auto discovery across activity
        // recreation (including a sensor rotation reconnect).  The requested
        // route may be "auto", but the streaming profile must know whether it
        // is USB, LAN/Wi-Fi, or a remote tunnel.
        intent.putExtra("HubRouteType", resolvedRoute);
        intent.putExtra("HubPreparedWidth", ready.optInt("width", 0));
        intent.putExtra("HubPreparedHeight", ready.optInt("height", 0));
        intent.putExtra("HubPreparedFps", ready.optInt("fps", 0));
        intent.putExtra("HubPreparedDegraded", ready.optBoolean("degraded", false));
        intent.putExtra("HubSupportedDisplayModes", ready.optJSONArray("supportedModes") == null ? "[]" : ready.getJSONArray("supportedModes").toString());
        intent.putExtra("HubNativeWidth", ready.optInt("nativeWidth", ready.optInt("width", 0)));
        intent.putExtra("HubNativeHeight", ready.optInt("nativeHeight", ready.optInt("height", 0)));
        intent.putExtra("HubCustomResolution", targetWidth > 0);
        JSONObject capabilities = selectedInfo.optJSONObject("capabilities");
        intent.putExtra("HubSupportsResolution", capabilities != null && capabilities.optBoolean("streamResolution"));
        intent.putExtra("HubLeaseId", leaseId); intent.putExtra("HubClientId", store.clientId()); intent.putExtra("HubRequestId", requestId);
        intent.putExtra("HubConnectionStartedAt", started);
        HubSessionLease.start(activity, intent);
        store.put("lastMode", mode); store.put("lastLink", link);
        checkCancelled();
        android.util.Log.i("HubConnection", "prepare-ready mode=" + mode + " elapsedMs=" + (SystemClock.elapsedRealtime() - started));
        logPhase(started, "intent-delivered");
        progress.update("电脑已就绪，正在建立视频连接…"); delivered = true; return intent;
        } catch (Exception error) {
            if (cancelled) throw new CancellationException("已取消连接，电脑显示正在还原。");
            throw error;
        } finally {
            activeHttp = null;
            if (!delivered && activeControl != null) {
                Thread.interrupted();
                if (!jobId.isEmpty()) try { activeControl.request("jobs/" + jobId + "/cancel", new JSONObject()); } catch (Exception ignored) { }
                if (!leaseId.isEmpty()) HubSessionLease.endPrepared(activity, selectedHost, leaseId, mode, requestId);
                if (!requestId.isEmpty()) try { activeControl.request("requests/" + requestId + "/respond", new JSONObject().put("clientId", store.clientId()).put("action", "failed").put("message", cancelled ? "平板已取消连接" : "平板连接未完成")); } catch (Exception ignored) { }
            }
        }
    }
    private void logPhase(long started, String phase) {
        HubSessionDiagnostics.phase(activity, phase, SystemClock.elapsedRealtime() - started);
        android.util.Log.i("HubConnection", "phase=" + phase + " elapsedMs=" + (SystemClock.elapsedRealtime() - started));
    }
    private void saveMediaCertificate(String mode, X509Certificate certificate) throws Exception {
        synchronized (HubStore.class) {
            checkCancelled();
            if (!bindingRevision.equals(store.get("bindingRevision", "")))
                throw new CancellationException("电脑绑定已改变，请重新连接。");
            store.put(certKey(mode), Base64.encodeToString(certificate.getEncoded(), Base64.NO_WRAP));
        }
    }
    /** Cancel only the session authenticated by this APK's own paired certificate before rotation. */
    public void finishPrevious() throws Exception {
        String mode = store.get("lastMode", "tablet"), host = store.get("lastHost", "");
        X509Certificate cert = certificate(mode);
        if (host.isEmpty() || cert == null) return;
        int port = "tablet".equals(mode) ? 49089 : 48989;
        NvHTTP http = new NvHTTP(new ComputerDetails.AddressTuple(host, port), port - 5, new IdentityManager(activity).getUniqueId(), cert, PlatformBinding.getCryptoProvider(activity));
        http.boundProductOperations();
        activeHttp = http;
        try {
            checkCancelled();
            // The ordinary authenticated cancel endpoint also restores the engine's captured display state.
            if (!http.quitApp()) throw new IOException("电脑未确认结束上次会话，请重试。");
            checkCancelled();
        } finally { if (activeHttp == http) activeHttp = null; }
    }
}
