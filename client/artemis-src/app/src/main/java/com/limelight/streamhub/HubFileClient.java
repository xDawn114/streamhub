package com.limelight.streamhub;

import android.content.Context;
import android.os.Build;
import org.json.JSONObject;
import okhttp3.MediaType;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;
import java.io.IOException;
import java.util.concurrent.CancellationException;

/** Uses the exact same pin/secret and NO_PROXY transport as the product control API. */
public final class HubFileClient {
    private final HubStore store;
    private volatile HubControlClient control;
    private volatile boolean cancelled;
    private boolean browseSupported, rootsSupported;
    public boolean supportsRoots(){return rootsSupported;}
    public boolean supportsBrowse() { return browseSupported; }
    private final String revision, transportRevision;
    public HubFileClient(Context context) { store = new HubStore(context); revision = store.get("bindingRevision", ""); transportRevision = store.get("transportRevision", ""); }
    public void cancel() { cancelled = true; HubControlClient active = control; if (active != null) active.cancel(); }
    public void check() {
        if (!transportRevision.equals(store.get("transportRevision", ""))) throw new CancellationException("连接通道已改变，请重新打开文件页面。");
        if (cancelled || Thread.currentThread().isInterrupted()) throw new CancellationException("传输已暂停，可继续。");
        if (!revision.equals(store.get("bindingRevision", ""))) throw new CancellationException("电脑绑定已改变，请重新打开文件页面。");
    }
    public void connect() throws Exception {
        check();
        final String trust = store.invitation(), expected = store.get("deviceId", "");
        HubProbeRace.Match<String> match;
        try { match = HubTransportProbe.first(store.candidates("auto"), host -> {
            HubControlClient probe = new HubControlClient(host, trust);
            try {
                JSONObject info = probe.probeInfo();
                if (!expected.equals(info.optString("deviceId"))) throw new IOException("这不是已绑定电脑。");
                JSONObject caps = info.optJSONObject("capabilities");
                if (caps == null || !caps.optBoolean("fileTransfer")) throw new IOException("电脑端尚未支持文件传输，请更新后重试。");
                return host;
            } finally { probe.cancel(); }
        }, () -> cancelled, 6000); }
        catch (IOException error) { throw new HubFileRetryPolicy.DiscoveryFailure(error); }
        check(); control = new HubControlClient(match.host, trust);
        JSONObject capabilities = control.probeInfo().optJSONObject("capabilities");
        browseSupported = capabilities != null && capabilities.optBoolean("fileBrowse");
        rootsSupported=capabilities!=null && capabilities.optBoolean("fileRoots");
    }
    public JSONObject register() throws Exception {
        check(); return control.fileJson("files/register", new JSONObject().put("clientId", store.clientId()).put("name", Build.MODEL).put("key", store.fileKey()), store.clientId(), store.fileKey());
    }
    public JSONObject json(String path, JSONObject body) throws Exception {
        check(); return control.fileJson("files/" + path, body, store.clientId(), store.fileKey());
    }
    public byte[] data(String id, long offset, byte[] upload) throws Exception {
        check(); return control.fileData(id, offset, upload, store.clientId(), store.fileKey());
    }
}
